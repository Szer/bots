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
- `DATABASE_URL` — should point to `localhost:5439` (Postgres from docker-compose)
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

# Connect manually (host, port 5439)
psql -h localhost -p 5439 -U coupon_hub_bot_service -d coupon_hub_bot

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
