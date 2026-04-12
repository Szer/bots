# Bots

Monorepo for F# Telegram bots deployed to Kubernetes via ArgoCD.

| Bot | Description | Docs |
|-----|-------------|------|
| [VahterBanBot](src/VahterBanBot/) | Spam moderation bot for Telegram chats | [README](src/VahterBanBot/README.md) |
| [CouponHubBot](src/CouponHubBot/) | Coupon management bot for a private community | [README](src/CouponHubBot/README.md) |

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
  vahter-bot/          — Helm chart + DB migrations
  coupon-hub-bot/      — Helm chart + DB migrations
  Dockerfile.bot       — shared Dockerfile
tests/
  BotTestInfra/        — shared test infrastructure
  VahterBanBot.Tests/  — integration tests
  CouponHubBot.Tests/  — integration tests
  CouponHubBot.Ocr.Tests/ — OCR unit tests
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

## License

MIT. See [LICENSE](LICENSE).

VahterBanBot has additional copyright — see [src/VahterBanBot/LICENSE](src/VahterBanBot/LICENSE).
