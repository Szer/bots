# Bots Monorepo — Agent Instructions

Monorepo for F# Telegram bots: **VahterBanBot** (spam moderation) and **CouponHubBot** (coupon management).

## Repository Structure

```
src/
  BotInfra/            — shared bot infrastructure library
  VahterBanBot/        — VahterBanBot application
  CouponHubBot/        — CouponHubBot application
  vahter-bot/          — VahterBanBot Helm chart + migrations
  coupon-hub-bot/      — CouponHubBot Helm chart + migrations
  Dockerfile.bot       — shared multi-stage Dockerfile (BOT_PROJECT build arg)
tests/
  BotTestInfra/        — shared test infrastructure (containers, helpers)
  VahterBanBot.Tests/  — VahterBanBot integration tests
  CouponHubBot.Tests/  — CouponHubBot integration tests
  CouponHubBot.Ocr.Tests/ — CouponHubBot OCR unit tests
  FakeTgApi/           — fake Telegram API for testing
  FakeAzureOcrApi/     — fake Azure OCR + OpenAI API for testing
scripts/
  setup-vpn.sh         — WireGuard VPN setup for CI
  verify-deploy.sh     — post-deploy verification (ArgoCD + Loki + Prometheus)
```

## Tech Stack

- **F# / .NET 10**, ASP.NET Core (webhook receivers)
- **PostgreSQL** + Dapper, Flyway migrations
- **Telegram.Bot** — Telegram Bot API
- **Docker** — containerization, Testcontainers for E2E tests
- **GitHub Actions** — CI/CD with reusable workflows
- **ArgoCD** — GitOps deployment to Kubernetes
- **OpenTelemetry** — traces and metrics, Serilog — structured logging

## F# Conventions

- Always use `task { }` CE for async, never `async { }`. Use `let!` for awaiting, never `.Result` or `.Wait()` — they cause deadlocks in ASP.NET Core.
- Use `%` prefix operator (defined in Utils.fs as `let inline (~%) x = ignore x`) to discard return values. Prefer `%expr` over `expr |> ignore` or `let _ = expr`.
- All database-mapped records must have `[<CLIMutable>]` attribute for Dapper compatibility.
- Use `[<RequireQualifiedAccess>]` on discriminated unions whose case names could shadow common F# identifiers (`Error`, `Ok`, `None`, `Some`).
- Always use `option` types for optional values. Never use `voption` / `ValueOption`.
- Nullable database columns use `string | null` annotation, not `string option` (for Dapper compatibility).
- Prefer exhaustive `match` expressions over nested `if/else`.
- `TreatWarningsAsErrors` is enabled — all warnings are errors.
- F# compilation order matters — new `.fs` files must be added to `.fsproj` in the correct position.
- Never use sentinel values (`DateTime.MinValue`) in domain models. If a value might not exist, use `option`. Group co-dependent fields into a single `option` of tuple/record instead of separate optionals.
- **Always `git fetch origin main && git checkout -b <branch> origin/main`** before creating a feature branch.

## Development Environment

- **Windows** with PowerShell as default shell
- Avoid bash heredoc syntax in shell commands — use `;` to chain `git` and `dotnet` commands
- F# code uses 4-space indentation
- Russian text in tests: always parse JSON with `JsonDocument` / `JsonSerializer` before comparing — never compare raw JSON strings containing Cyrillic

## Testing

- Run tests: `dotnet test -c Release`
- Run specific bot tests: `dotnet test tests/VahterBanBot.Tests -c Release` or `dotnet test tests/CouponHubBot.Tests -c Release`
- When tests fail, check container logs in `test-artifacts/<ProjectName>/<Fixture>/` (app.log, postgres.log, flyway.log)
- **Prefer black-box integration tests** — send HTTP to bot pod, observe behavior (messages sent/deleted, bans applied). Do NOT write unit tests against internal implementation.
- Tests use xUnit v3 with assembly fixtures and Testcontainers (PostgreSQL, Flyway, FakeTgApi, bot)
- When debugging runtime errors, write a minimal repro test FIRST, then fix. Don't exhaustively query databases.

## Database

- Migration files: `V{N}__{description}.sql` (sequential number, double underscore, snake_case)
- New tables/sequences must include `GRANT` for the service role
- Use parameterized SQL only — never string-interpolate user input into SQL
- VahterBanBot DB: `vahter_db_v2`, role: `vahter_bot_service`
- CouponHubBot DB: `coupon_hub_bot`, role: `coupon_hub_bot_service`

## Settings configuration

- All **non-secret** bot configuration lives in the `bot_setting` table. Env vars are only for secrets (`BOT_TELEGRAM_TOKEN`, `BOT_AUTH_TOKEN`, `AZURE_OCR_KEY`, `GITHUB_TOKEN`, `DATABASE_URL`, etc.).
- Each bot registers `BotConfiguration` (and `BotOcrConfig` where OCR is used) as `IOptions<_>` via `BotInfra.LiveOptions<_>`. Services inject `IOptions<T>` and read `.Value` — this lets `POST /reload-settings` pick up changes without a pod restart.
- **DB-only settings** — keys with no env fallback — become silently wrong if missing from `bot_setting`. When adding such a setting in `buildBotConf`, either (a) give it an env fallback via `getEnvOr`, or (b) ship a seed INSERT in the same migration as the code change. Current DB-only keys in CouponHubBot: `OCR_ENABLED`, `OCR_MAX_FILE_SIZE_BYTES`, `REMINDER_HOUR_DUBLIN`, `REMINDER_RUN_ON_START`, `TEST_MODE`, `MAX_TAKEN_COUPONS`.
- Never add `AddSingleton<BotConfiguration>(record)` — it captures a frozen copy, defeating reload. The `LiveOptions<_>` wrapper is the only correct registration.

## Security

- Never commit secrets, tokens, or API keys — use environment variables
- Validate all Telegram callback data — it can be crafted by malicious clients
- Use parameterized SQL — never interpolate user input into queries
- CouponHubBot: verify community membership before allowing access to coupon operations

## CI/CD

- Reusable workflows: `_bot-build.yml` (PR builds), `_bot-deploy.yml` (deploy on push to main)
- Deploy pipeline: test → migrate DB → build & push Docker image to GHCR → verify deployment
- Post-deploy verification checks ArgoCD sync, pod health, Loki errors, and Prometheus 5xx rate
- VahterBanBot upstream sync: creates PRs to `fsharplang-ru/vahter-bot` mirror repo

## Code Review Rules

Focus on: bugs, security, F# convention violations, missing validation, missing tests.
Do NOT flag: style preferences, minor formatting, subjective naming choices.

### Issue Categories

- **BLOCKING**: bugs, security vulnerabilities, missing validation, data loss risks, deadlocks, missing GRANT in migrations
- **NON-BLOCKING**: convention suggestions, minor improvements, naming preferences

## VahterBanBot — Specific Notes

Telegram bot for spam deletion and administrative functions in Russian-speaking F# community chats.

Commands: `/ban` (delete + global ban), `/sban [hours]` (soft-ban/mute), `/unban <user_id>`, `/ban ping` (health check).

Uses LLM-based spam detection (OpenAI API) with configurable verdicts (SPAM/NOT_SPAM/SKIP).

## CouponHubBot — Specific Notes

Telegram bot for collaborative coupon management in a private community. All UI text is in **Russian** (Cyrillic).

Commands: `/add`, `/list`, `/my`, `/stats`, `/feedback`, `/take <id>`, `/used <id>`, `/return <id>`.

Callback data uses colon-separated format: `"action:param1:param2"`. Wizard flows persist state in `PendingAddFlow` table.

See `src/CouponHubBot/docs/` for detailed architecture, testing, database, OCR, and deployment documentation.
