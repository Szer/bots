# CouponHubBot — Local Development Setup

## Quick Start

### 1. Start Infrastructure (PostgreSQL + FakeTgApi)

```bash
# Start Postgres and run migrations (flyway will run automatically and exit)
docker-compose -f docker-compose.dev.yml up -d postgres flyway

# Wait a moment for flyway to complete, then start FakeTgApi (optional)
docker-compose -f docker-compose.dev.yml up -d fake-tg-api
```

**Note:** Flyway runs once and exits. If you need to re-run migrations:
```bash
# Force recreate and run flyway again
docker-compose -f docker-compose.dev.yml run --rm flyway
```

### 2. Configure Environment Variables

Copy `env.example` to `.env` (or configure in Rider Run Configuration):

```bash
cp env.example .env
```

Update `.env` with your values:
- `DATABASE_URL` — should point to `localhost:15432` (Postgres from docker-compose)
- `TELEGRAM_API_URL` — set to `http://localhost:8080` if using FakeTgApi, or leave empty for real Telegram API
- `BOT_TELEGRAM_TOKEN` — your real bot token (or `123:456` for testing with FakeTgApi)
- `BOT_AUTH_TOKEN` — secret token for webhook authentication
- `FEEDBACK_ADMINS` — list of Telegram userId for `/feedback` admins (e.g. `123,456`)

### 3. Run Bot in Rider

1. Open Run Configuration
2. Set Environment Variables (or use `.env` file if you have a plugin)
3. Set `ASPNETCORE_URLS=http://localhost:5000`
4. Run with debugger attached

### 4. Test with HTTP Client

Open `coupon-hub-bot.http` in Rider and send requests:
- `GET /health` or `GET /healthz` — health check endpoints
- `POST /bot` — send Telegram update (webhook)
- `POST /test/run-reminder` — test-only endpoint (requires `TEST_MODE=true`)
  - pass `nowUtc=2026-01-19T08:00:00Z` as query parameter

### 5. View Logs

The bot will log:
- `HTTP REQUEST: {Method} {Path}` — all incoming requests
- `HTTP OUT {Method} {Url}` — outgoing requests to Telegram API
- `HTTP IN {StatusCode} ...` — responses from Telegram API

FakeTgApi logs:
- `FAKE TG IN {method} {path}` — incoming Telegram API calls
- Check `/test/calls` endpoint to see all logged API calls

## Smoke Test Against Real Telegram (Bot in Docker + ngrok)

End-to-end test the album-upload flow against the real Telegram API using a
**duplicate test bot** (NOT the production bot). Webhook traffic from Telegram
is tunnelled to the local Docker container via ngrok.

### 1. Configure secrets

```bash
cp src/coupon-hub-bot/.env.local.example src/coupon-hub-bot/.env.local
# Edit .env.local: BOT_TELEGRAM_TOKEN (from @BotFather, NEW test bot),
# BOT_AUTH_TOKEN (any random secret), FEEDBACK_ADMINS (your tg user id).
```

`.env.local` is gitignored (`**/.env.local`).

### 2. Bring up the stack

```bash
docker compose -f src/coupon-hub-bot/docker-compose.dev.yml --profile smoke up -d --build
```

This starts Postgres (15432), runs Flyway migrations, seeds `bot_setting` from a
prod snapshot (`dev-bot-settings.sql` — includes `OCR_ENABLED=true`,
`AZURE_OCR_ENDPOINT=...`, etc.), and runs the bot on host port 5000. Default
`up` (without `--profile smoke`) brings only Postgres + Flyway + the
settings seed for the Rider workflow.

Tail bot logs:

```bash
docker logs -f coupon-hub-bot-dev
```

### 3. Expose the bot to Telegram via ngrok

```bash
ngrok http 5000
```

Copy the `https://<id>.ngrok-free.app` URL ngrok prints.

### 4. Register the webhook with Telegram

```bash
curl "https://api.telegram.org/bot<BOT_TELEGRAM_TOKEN>/setWebhook" \
  -d "url=https://<ngrok-id>.ngrok-free.app/bot" \
  -d "secret_token=<BOT_AUTH_TOKEN>"

# Verify:
curl "https://api.telegram.org/bot<BOT_TELEGRAM_TOKEN>/getWebhookInfo"
```

### 5. (Optional) Tweak bot_setting

`OCR_ENABLED`, `AZURE_OCR_ENDPOINT`, `FEEDBACK_ADMINS`, etc. are already
seeded from the prod snapshot — no manual flip needed for OCR. To change a
value mid-session and have the bot pick it up without a restart:

```bash
docker exec -it coupon-hub-postgres-dev psql -U admin -d coupon_hub_bot \
  -c "UPDATE bot_setting SET value='false' WHERE key='OCR_ENABLED';"
docker exec -it coupon-hub-bot-dev sh -c "curl -s -X POST http://localhost:5000/reload-settings -H 'X-Telegram-Bot-Api-Secret-Token: '\$BOT_AUTH_TOKEN"
```

The seed uses `ON CONFLICT DO NOTHING`, so on a subsequent `docker compose up`
your manual UPDATEs are preserved. To force a re-seed from the file:

```bash
docker exec -it coupon-hub-postgres-dev psql -U admin -d coupon_hub_bot -c "DELETE FROM bot_setting;"
docker compose -f src/coupon-hub-bot/docker-compose.dev.yml run --rm seed-bot-settings
```

### 6. Test

Send an album (multiple photos as one Telegram message) to the duplicate bot.
You should see:
- Immediate "Получил, обрабатываю купоны…" placeholder.
- After ~5s (or `BATCH_DEBOUNCE_MS` in `bot_setting`), the placeholder is deleted
  and replaced with a fresh "Подтвердить N купонов" message.

### 7. Tear down

```bash
docker compose -f src/coupon-hub-bot/docker-compose.dev.yml --profile smoke down -v
curl "https://api.telegram.org/bot<BOT_TELEGRAM_TOKEN>/deleteWebhook"
```

## OCR (CouponOcrEngine) — Separate Test Suite

OCR is extracted into `CouponOcrEngine` and tested separately without Docker containers.

### Setup

- **Images**: place files in `tests/CouponHubBot.Ocr.Tests/Images/`
- **File naming** (expected values from filename):
  - Format: `[couponValue]_[minCheck]_[validFrom]_[validTo]_[barcode].[ext]`
  - Example: `10_50_2025-01-01_2025-02-01_123456789.jpg`

### Environment Variables (required for Azure calls)

Tests make real HTTP calls to Azure Computer Vision:
- `AZURE_OCR_ENDPOINT` — base URL (e.g. `https://<name>.cognitiveservices.azure.com`)
- `AZURE_OCR_KEY`

If not set:
- If cache files exist in `tests/CouponHubBot.Ocr.Tests/AzureCache/` — tests pass without Azure
- If cache miss + no env — test fails with clear error

### Run

```bash
dotnet test tests/CouponHubBot.Ocr.Tests/CouponHubBot.Ocr.Tests.fsproj -c Release
```

### Azure OCR Cache

- First run creates cache files in `tests/CouponHubBot.Ocr.Tests/AzureCache/`
- Subsequent runs use cache (no Azure calls)
- Delete cache files to invalidate

## Troubleshooting

### Database Connection Issues

```bash
# Check if Postgres is running
docker ps | grep coupon-hub-postgres-dev

# Check Postgres logs
docker logs coupon-hub-postgres-dev

# Connect manually (host, port 15432)
psql -h localhost -p 15432 -U coupon_hub_bot_service -d coupon_hub_bot

# Or from inside container (port 5432)
docker exec -it coupon-hub-postgres-dev psql -U coupon_hub_bot_service -d coupon_hub_bot
```

### FakeTgApi Not Responding

```bash
docker ps | grep coupon-hub-fake-tg-api-dev
docker logs coupon-hub-fake-tg-api-dev
curl http://localhost:8080/health
```

### Reset Database

```bash
docker-compose -f docker-compose.dev.yml down -v
docker-compose -f docker-compose.dev.yml up -d postgres
docker-compose -f docker-compose.dev.yml --profile migrate run --rm flyway
```

## Telemetry (OpenTelemetry)

- Traces via OTel tracing (HTTP + ASP.NET Core + Npgsql)
- Metrics via `System.Diagnostics.Metrics` + OTel metrics pipeline

Environment variables:
- `OTEL_EXPORTER_OTLP_ENDPOINT` — OTel target (gRPC)
- `OTEL_EXPORTER_CONSOLE=true` — console export (local dev)
- `OTEL_SERVICE_NAME` — service name (default: `coupon-hub-bot`)
