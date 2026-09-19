# Bots

Monorepo for F# Telegram bots deployed to Kubernetes via ArgoCD.

| Bot | Description | Docs |
|-----|-------------|------|
| [VahterBanBot](src/VahterBanBot/) | Spam moderation bot for Telegram chats | [README](src/VahterBanBot/README.md) |
| [CouponHubBot](src/CouponHubBot/) | Coupon management bot for a private community | [README](src/CouponHubBot/README.md) |
| [Fizruk](src/Fizruk/) | Starts/stops on-demand game server Deployments from Telegram | see below |

## Tech Stack

- **F# / .NET 10** — ASP.NET Core webhook receivers
- **PostgreSQL** — database, Flyway migrations
- **Docker** — containerization, Testcontainers for integration tests
- **GitHub Actions** — CI/CD with [reusable workflows](.github/workflows/)
- **ArgoCD** — GitOps deployment to Kubernetes
- **OpenTelemetry** + **Serilog** — observability

## Repository Structure

```
src/
  BotInfra/            — shared bot infrastructure
  VahterBanBot/        — VahterBanBot application
  CouponHubBot/        — CouponHubBot application
  Fizruk/              — Fizruk application
  vahter-bot/          — Helm chart + DB migrations
  coupon-hub-bot/      — Helm chart + DB migrations
  Dockerfile.bot       — shared Dockerfile
tests/
  BotTestInfra/        — shared test infrastructure
  VahterBanBot.Tests/  — integration tests
  CouponHubBot.Tests/  — integration tests
  CouponHubBot.Ocr.Tests/ — OCR unit tests
  Fizruk.Tests/        — hermetic unit tests (no DB, no containers)
  FakeTgApi/           — fake Telegram API
  FakeAzureOcrApi/     — fake Azure OCR + OpenAI API
scripts/
  setup-vpn.sh         — WireGuard VPN for CI
  verify-deploy.sh     — post-deploy verification
```

## CI/CD

Reusable workflow templates in `.github/workflows/`:

- **`_bot-build.yml`** — PR build: test + upload artifacts
- **`_bot-deploy.yml`** — Deploy on push to `main`: test → migrate DB → Docker push to GHCR → verify deployment

Each bot has thin caller workflows (`vahter-build.yml`, `coupon-build.yml`, etc.) that pass bot-specific parameters.

VahterBanBot source is also synced to the [fsharplang-ru/vahter-bot](https://github.com/fsharplang-ru/vahter-bot) mirror via automated PRs.

## Fizruk

Fizruk (Russian for a PE teacher — the one who blows the whistle to start and stop
the game) is a Telegram bot that scales an on-demand game server's Kubernetes
Deployment up on `/start`, down on `/stop`, and back down automatically once nobody's
playing. Unlike the other bots it has no database — all state lives in the cluster
and in a mounted config file.

- **Commands**: `/start [game]`, `/stop [game]`, `/status [game]` (optional
  `@botname` suffix, case-insensitive). If a chat controls exactly one game the name
  can be omitted; a chat controlling several games must name one for `/start`/`/stop`,
  while a nameless `/status` reports on all of them.
- **Config** (`FIZRUK_CONFIG_PATH`, a mounted ConfigMap): a `games` map (per game:
  `displayName`, `namespace`, `deployment`, `podSelector`, `container`,
  `nodeLabelSelector`, `address`, an optional `players` probe — `rcon` or `raknet` —,
  `activityRegex`, `idleGraceMinutes`, `idleWindowMinutes`, `startTimeoutMinutes`) and
  a `chats` map from Telegram chat id to the list of games that chat may control.
- **Idle shutdown**: every 10 minutes, a game running past its `idleGraceMinutes`
  with nobody online (player probe) and no matching activity in its recent pod logs
  is scaled back to 0, with every controlling chat notified.
- **Env vars**: `FIZRUK_CONFIG_PATH`, `BOT_TELEGRAM_TOKEN`, `BOT_AUTH_TOKEN`,
  `BOT_USERNAME` (optional, for the `@botname` suffix), `TELEGRAM_API_URL`
  (optional test override), `BOT_WEBHOOK_URL` (optional; when set, Fizruk
  self-registers its webhook at startup via `BotInfra.WebhookRegistration` —
  see below), and each RCON-probed game's `passwordEnv` variable (e.g.
  `FACTORIO_RCON_PASSWORD`).
- **Webhook self-registration**: opt-in, via `BotInfra.WebhookRegistration`
  (any bot can adopt it). Unset/empty `BOT_WEBHOOK_URL` is a no-op. When set,
  Fizruk calls Telegram's `setWebhook` at startup (fire-and-forget, retried a
  few times with backoff, never crashes the pod) with that URL,
  `secret_token=BOT_AUTH_TOKEN`, and `allowed_updates=["message"]`.
- **GHCR image**: `ghcr.io/szer/fizruk`.

## License

MIT. See [LICENSE](LICENSE).

VahterBanBot has additional copyright — see [src/VahterBanBot/LICENSE](src/VahterBanBot/LICENSE).
