# Bots Monorepo — Agent Instructions

Monorepo for F# Telegram bots: **VahterBanBot** (spam moderation), **CouponHubBot** (coupon management), and **AlitaBot** (conversational chatbot).

## Repository Structure

```
src/
  BotInfra/            — shared bot infrastructure library
  VahterBanBot/        — VahterBanBot application
  CouponHubBot/        — CouponHubBot application
  AlitaBot/            — AlitaBot application (see AlitaBot/README.md, AlitaBot/docs/)
  vahter-bot/          — VahterBanBot Helm chart + migrations
  coupon-hub-bot/      — CouponHubBot Helm chart + migrations
  alita-bot/           — AlitaBot Helm chart + migrations + dev bot_setting seed
  Dockerfile.bot       — shared multi-stage Dockerfile (BOT_PROJECT build arg)
tests/
  BotTestInfra/        — shared test infrastructure (containers, helpers)
  VahterBanBot.Tests/  — VahterBanBot integration tests
  CouponHubBot.Tests/  — CouponHubBot integration tests
  CouponHubBot.Ocr.Tests/ — CouponHubBot OCR unit tests
  AlitaBot.Tests/      — AlitaBot hermetic integration tests (Testcontainers + fakes, the PR gate)
  AlitaBot.RealTests/  — AlitaBot real-Telegram/real-LLM tests (manual/dev-iteration only, paid calls)
  FakeTgApi/           — fake Telegram API for testing
  FakeAzureOcrApi/     — fake Azure OCR + OpenAI API for testing (also doubles as AlitaBot's fake LLM/embeddings/image backend)
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

- Run tests: `dotnet test -c Release`. **This is safe by default at the solution level** — it never spends real money, even on a machine where `~/.alita-test/env` / `~/.coupon-test/env` are fully populated with working credentials. See "Real/paid tests" below for why.
- Run specific bot tests: `dotnet test tests/VahterBanBot.Tests -c Release` or `dotnet test tests/CouponHubBot.Tests -c Release`
- When tests fail, check container logs in `test-artifacts/<ProjectName>/<Fixture>/` (app.log, postgres.log, flyway.log)
- **Prefer black-box integration tests** — send HTTP to bot pod, observe behavior (messages sent/deleted, bans applied). Do NOT write unit tests against internal implementation.
- Tests use xUnit v3 with assembly fixtures and Testcontainers (PostgreSQL, Flyway, FakeTgApi, bot)
- When debugging runtime errors, write a minimal repro test FIRST, then fix. Don't exhaustively query databases.

### Real/paid tests — explicit opt-in required, never automated

`tests/AlitaBot.RealTests` and `tests/CouponHubBot.RealTests` drive a **real Telegram MTProto session** and **real paid LLM/OCR backends** (Azure AI Foundry, Gemini, Azure OCR). Every test in each project skips unless BOTH are true:

1. Credentials are present (`~/.alita-test/env` / `~/.coupon-test/env`).
2. The project's opt-in env var is explicitly set: `ALITA_REAL_TESTS=1` / `COUPON_REAL_TESTS=1`.

Credential presence alone is deliberately **not** enough — a machine with a fully populated `~/.alita-test/env` (the normal state on a maintainer's dev box) must still see `dotnet test -c Release` at the solution level skip every real test, with zero real Telegram/LLM calls made. This was a real incident: a plain solution-wide `dotnet test` burned real API cost (~$6/day incident) because credential presence was the only gate. The opt-in check happens once, in each project's `RealAssemblyFixture` (`SkipUnlessCore`/`InitializeAsync`), not per-test — do not add per-test gating, and do not bypass the fixture.

These tests are for **deliberate feature work and manual dev-iteration only** (`make real-test` / `make coupon-real-test`, which set the opt-in var themselves) or the two `workflow_dispatch`-only CI workflows (`alita-real-test.yml`, `coupon-real-test.yml`), which also set it. **Agents/automation must never set `ALITA_REAL_TESTS` or `COUPON_REAL_TESTS`** — if a task seems to call for running the real suites, stop and ask a human rather than opting in yourself.

## Database

- Migration files: `V{N}__{description}.sql` (sequential number, double underscore, snake_case)
- New tables/sequences must be granted to the service role — either in the creating migration or in a dedicated later grants migration (e.g. `V3__missing_grants.sql`, `V17__grant_permissions.sql`, which also use catch-all `GRANT … ON ALL TABLES/SEQUENCES` + `ALTER DEFAULT PRIVILEGES`). The Testcontainers suite runs every migration, so a genuinely missing grant fails CI; absence of a `GRANT` in a single file is not by itself a defect.
- Use parameterized SQL only — never string-interpolate user input into SQL
- VahterBanBot DB: `vahter_db_v2`, role: `vahter_bot_service`
- CouponHubBot DB: `coupon_hub_bot`, role: `coupon_hub_bot_service`
- AlitaBot DB: `alita_bot`, role: `alita_bot_service` (pgvector-backed — `message_embedding`/`interaction_memory` use the `vector` extension, `pgvector/pgvector:pg17` everywhere AlitaBot provisions its own Postgres)

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

- Reusable workflows: `_bot-build.yml` (PR builds), `_bot-deploy.yml` (deploy on push to main), `_sre-agent.yml` (deploy-failure incident response, see Agentic Workflows below)
- Per-bot build/deploy: `vahter-build.yml`/`vahter-deploy.yml`, `coupon-build.yml`/`coupon-deploy.yml`, `alita-build.yml`/`alita-deploy.yml` — each is a thin wrapper passing bot-specific `with:`/`secrets:` into the shared reusable workflows
- Deploy pipeline: test → migrate DB → build & push Docker image to GHCR → verify deployment
- Post-deploy verification checks ArgoCD sync, pod health, Loki errors, and Prometheus 5xx rate
- VahterBanBot upstream sync: creates PRs to `fsharplang-ru/vahter-bot` mirror repo
- AlitaBot also has `alita-real-test.yml` — `workflow_dispatch`-only full E2E (real Telegram + paid LLM/media calls) against a transient AKS deployment; NOT a PR gate, see `src/AlitaBot/docs/TESTING.md`

## Agentic Workflows

- GPT-5-mini agents run via `openai/codex-action@v1` on Microsoft Foundry — SRE (`_sre-agent.yml`), Project (`project.yml`), Product (`product.yml`), Monitor (`monitor.yml`). See `src/CouponHubBot/docs/PROJECT-AGENT.md` for the full design, and `.github/AGENT-FLOWS-REDESIGN.md` for the in-flight redesign of this area.
- **SRE coverage is automatic for every bot** that uses `_bot-deploy.yml`. `_sre-agent.yml` is a reusable workflow called directly from `_bot-deploy.yml` when the deploy or its post-deploy verification fails — bot identity (`bot`, `argocd-app-name`, `container-name`, `docker-image`, `commit`, `run-url`) is passed in as workflow inputs, not read from an issue body. `verify-deploy.sh` failing still opens a `deploy-failure` issue as the incident record; its number is passed to the SRE agent when available so it can comment/close. `sre-manual.yml` (`workflow_dispatch`) gives the same agent a hand-triggered entry point for incidents outside a deploy. Opt-out of the deploy-triggered path by passing `sre-enabled: false` to `_bot-deploy.yml`.
- Project and Product agents are coupon-only for now; AlitaBot has no project/product coverage yet (§3.8 of the redesign doc: insufficient chat signal for a product agent). Both rely on `AZURE_OPENAI_API_KEY` (secret) + `AZURE_OPENAI_BASE_URL` (var, base URL only — no `/responses` suffix) — as does the SRE agent.
- **Monitor is runtime anomaly watch, per bot, baseline-relative** — every 4 hours for every bot whose `roles` include `monitor`, except AlitaBot which runs DAILY only (`traffic_class: dormant` — the cron only actually invokes it in the first slot of the UTC day, or on manual `workflow_dispatch`; the dormant carve-out also disables volume-based rules entirely for it, since 0 log lines on most days is normal, not an anomaly). Baseline history (7d/28d medians, ratios, z-scores) lives as one JSONL-per-bot-per-month file on the orphan `agent-state` branch, written by `scripts/gather/baseline.sh`. A mechanical (non-LLM) P1 check — no healthy replicas, or an extreme error burst — escalates straight to the SRE agent (`_sre-agent.yml`) regardless of the monitor agent's own judgment. `backfill-baseline.yml` (`workflow_dispatch`-only, `dry_run` default `true`) can pre-warm up to ~28 days of `agent-state` history from Loki/Postgres so baselines aren't cold on day one.
- `.github/bots.yml` is the single-source-of-truth bot registry read by every gatherer/workflow/prompt (`container`, `db_name`, `traffic_class`, `roles`, etc.) — adding a bot to Monitor/Project/Product coverage is a registry entry, not a workflow edit.

## Code Review Rules

Focus on: bugs, security, F# convention violations, missing validation, missing tests.
Do NOT flag: style preferences, minor formatting, subjective naming choices.

### Issue Categories

- **BLOCKING**: bugs, security vulnerabilities, missing validation, data loss risks, deadlocks, a new table/sequence left ungranted to the service role (verify it isn't covered by a later or catch-all grants migration before flagging)
- **NON-BLOCKING**: convention suggestions, minor improvements, naming preferences

## VahterBanBot — Specific Notes

Telegram bot for spam deletion and administrative functions in Russian-speaking F# community chats.

Commands: `/ban` (delete + global ban), `/sban [hours]` (soft-ban/mute), `/unban <user_id>`, `/ban ping` (health check).

Uses LLM-based spam detection (OpenAI API) with configurable verdicts (SPAM/NOT_SPAM/SKIP).

## CouponHubBot — Specific Notes

Telegram bot for collaborative coupon management in a private community. All UI text is in **Russian** (Cyrillic).

- **Gender-neutral Russian UI text** — users are mostly women. Never address the user with a gendered verb/adjective («ты уверен», «если передумал»), never narrate the bot's own actions in gendered past tense («Добавил», «Отметил»), and don't use «(а)» double forms. Prefer impersonal/passive («Купон добавлен», «Купон отмечен как использованный») or gender-free phrasings (imperatives «Аннулируй»; 2nd-person future «Если передумаешь»). Forms that agree with a noun (e.g. «взятый купон») are fine — they encode the noun's gender, not the user's.

Commands: `/add`, `/list`, `/my`, `/stats`, `/feedback`, `/take <id>`, `/used <id>`, `/return <id>`.

Callback data uses colon-separated format: `"action:param1:param2"`. Wizard flows persist state in `PendingAddFlow` table.

See `src/CouponHubBot/docs/` for detailed architecture, testing, database, OCR, and deployment documentation.

## AlitaBot — Specific Notes

Conversational Telegram chatbot for a ~30-person IT chat: replies when mentioned/named/replied-to, with full message history logged for context (`message_log`). Two responder modes (`echo` walking-skeleton, `llm` real Azure AI Foundry chat completions) and three streaming renderers (`plain`/`edit`/`draft`) for delivering an LLM reply as it generates.

- Persona/config is entirely `bot_setting`-driven and hot-reloadable (`POST /reload-settings`) — see this file's "Settings configuration" section. Prompts (`SYSTEM_PROMPT`, `ROAST_PROMPT`, etc.) are tuned live in prod; `src/alita-bot/dev-bot-settings.sql` only seeds dev/test values.
- Features beyond the core responder: image generation (`/img`, Azure or Gemini via `IMAGE_PROVIDER`), per-message semantic memory + `/ask` (pgvector embeddings), per-person "dossiers" from nightly fact extraction, a small social-features set (`/roast`, `/awards`, `/quote`, `/karma`), and opt-in proactive behavior (morning digest, willingness-gated interjections, meme reactions) — all default OFF/0.0 except the core reactive responder.
- Commands: `/img`, `/model`, `/summary` (`/tldr`), `/usage`, `/ask`, `/say`, `/song`, `/sql` (admin-only), `/dossier`, `/forget-me`, `/roast`, `/awards`, `/quote`, `/karma`, `/help` (`/start`) — see `src/AlitaBot/README.md`'s "Commands" table for full details.
- Uses `AZURE_FOUNDRY_ENDPOINT` (`szer-foundry.cognitiveservices.azure.com`), the **same Foundry resource CouponHubBot's project/product agents use** — every AlitaBot deployment name is prefixed `alita-` purely to avoid collisions in that shared account's flat deployment list.
- Testing tiers (same model as VahterBanBot/CouponHubBot, but with two additional real-mode tiers given the paid LLM/image/voice APIs involved): hermetic (`tests/AlitaBot.Tests`, the PR gate, `alita-build.yml`), developer real-Telegram (`tests/AlitaBot.RealTests` via `make real-test`, deliberate/scoped runs only), and full AKS E2E (`alita-real-test.yml`, `workflow_dispatch`-only). See `src/AlitaBot/docs/TESTING.md`.
- Not yet covered by the Project/Product agents (insufficient chat signal — see `.github/AGENT-FLOWS-REDESIGN.md` §3.8); SRE coverage on deploy failure is automatic like every other bot on `_bot-deploy.yml`, though `alita-deploy.yml` currently sets `sre-enabled: false` until the bot has settled in prod.

See `src/AlitaBot/README.md` and `src/AlitaBot/docs/` (`OBSERVABILITY.md`, `TECH-DEBT.md`, `TESTING.md`) for detailed architecture, testing, and operational documentation.
