# Testing

AlitaBot has two independent suites: a **fake suite** (Testcontainers + `FakeTgApi`/
`FakeAzureOcrApi`, no network egress) that runs everywhere including CI, and a **real
suite** (real Telegram + real Azure AI Foundry) that only runs locally against your own
test bot and Azure resources.

## Fake suite — `tests/AlitaBot.Tests`

```bash
dotnet test tests/AlitaBot.Tests -c Release
```

Requires Postgres/Flyway/FakeTgApi/FakeAzureOcrApi containers (Testcontainers, via
podman). No Telegram or Azure credentials needed — `FakeAzureOcrApi` doubles as the fake
LLM backend (chat completions incl. SSE streaming, transcriptions, speech, images
generations/edits), reusing the same container CouponHubBot's OCR tests use.

- `SkeletonTests.fs` — mention/name/reply-to-bot triggering, target-chat privacy gate,
  message_log attribution, duplicate-webhook-delivery idempotency.
- `VoiceTests.fs` — transcription flow, TL;DR threshold, disabled/no-filepath/empty-transcript paths.
- `VisionTests.fs` — photo message handling, image parts attached to LLM requests.
- `ImageGenTests.fs` — `/img`/`!img` command parsing, text-to-image and img2img (reply-to-photo), disabled/empty-prompt/failure paths.
- `LlmTests.fs` — streaming renderers (edit/draft/plain), 429 handling.
- `CommandsTests.fs` — command registry dispatch, `/model`, `/summary`, `/usage`.
- `MemoryTests.fs` — Slice 5a: the inline embedding pipeline (`message_log` ->
  `message_embedding`, `EMBED_MESSAGES`/`EMBEDDING_MIN_CHARS`) and `/ask` semantic search,
  against `FakeAzureOcrApi`'s deterministic hash-of-text `/embeddings` fake (see
  `src/AlitaBot/README.md`'s "Memory" section for the scheme).
- `DossierTests.fs` — Slice 5b: the nightly dossier job (`POST /test/run-job?name=
  dossier_nightly_update`, extraction -> embed -> dedup -> insert -> merge), fact dedup
  (a fact scripted twice across two runs yields one active row), recall injection
  (ResponderService appends the triggering author's dossier + matching facts to the system
  prompt — asserted via `GetAzureLlmCalls`'s request body, present for the matching author,
  absent for anyone else), `DOSSIER_ENABLED=false`, `/dossier` (self/`@username`/unknown/empty),
  and `/forget-me` (purges dossier data, then the embedding pipeline and nightly job both
  skip the now-opted-out user). **Truncates `message_log`/`message_embedding`/
  `interaction_memory`/`person_dossier`/`memory_opt_out`** (`fixture.TruncateMemoryTables()`,
  admin connection) before every fact — the nightly job scans ALL of `message_log` for
  "active users", and this fixture's `TimeProvider` is a `FakeTimeProvider` that's never
  advanced across the whole assembly run (every seeded message across every test class shares
  one frozen `sent_at`), so without truncating, every user_id ever seeded by an earlier test
  class would show up as "active in the last 24h" too.

Container logs land in `test-artifacts/AlitaBot.Tests/AlitaTestContainers/` (`bot.log`,
`postgres.log`, `flyway.log`, `fake-tg-api.log`, `fake-azure-ocr.log`) on pass or fail.

Tests are black-box: they POST synthetic updates at the bot's webhook (`fixture.SendUpdate`)
and assert against `FakeTgApi`'s captured calls (`fixture.GetFakeCalls`) and Postgres rows
(`fixture.Query`/`QuerySingleOrDefault`), never against internal F# types.

## Real suite — `tests/AlitaBot.RealTests` (`make real-test`)

Exercises the bot end-to-end against real Telegram (a dedicated test bot + an MTProto
test-user client playing the human side) and, for `RESPONDER_MODE=llm`, real Azure AI
Foundry. See `src/AlitaBot/README.md`'s "One-time setup" section for how to provision the
test bot, ngrok tunnel, and MTProto session.

Credentials live in `~/.alita-test/env` (`KEY=VALUE`, gitignored — never commit values from
it). Field names, no values:

| Variable | Purpose |
|---|---|
| `ALITA_NGROK_DOMAIN`, `ALITA_NGROK_API_KEY`, `ALITA_NGROK_AUTHTOKEN` | ngrok tunnel (reserved domain required) |
| `ALITA_TEST_BOT_TOKEN`, `ALITA_TEST_BOT_USERNAME` | dedicated test bot (never the production bot) |
| `ALITA_WEBHOOK_SECRET` | shared secret between the harness and the bot process |
| `ALITA_TEST_CHAT_ID` | test group chat id (`make tg-chats` to find it) |
| `ALITA_TG_API_ID`, `ALITA_TG_API_HASH`, `ALITA_TG_API_PHONE` | MTProto test-user client credentials (`make tg-login` for the session) |
| `AZURE_FOUNDRY_ENDPOINT`, `AZURE_FOUNDRY_KEY`, `ALITA_LLM_DEPLOYMENT`, `ALITA_STT_DEPLOYMENT`, `ALITA_TTS_DEPLOYMENT`, `ALITA_IMAGE_DEPLOYMENT`, `ALITA_EMBEDDING_DEPLOYMENT` | real Azure AI Foundry deployments (only needed for `RESPONDER_MODE=llm` / voice / image / `/ask` real-tests; each real-test suite self-skips via `Assert.Skip` when its deployment env var is missing) |
| `RESPONDER_MODE`, `STREAM_MODE` | optional overrides (default `echo`/`edit`), upserted into `bot_setting` on every run |

```bash
make real-test    # dev DB + Release build + full real-Telegram smoke suite
make alita-logs   # tail bot.log / ngrok.log from the last run
make alita-clean  # tear down containers + deleteWebhook (run when done for the session)
```

`SmokeTests.fs` is the core conversational suite; `VoiceRealTests.fs`, `VisionRealTests.fs`,
`ImageGenRealTests.fs`, `AskRealTests.fs`, `DossierRealTests.fs` cover their respective slices
and self-skip when their deployment isn't configured. `AskRealTests.fs` (Slice 5a) sends two
GUID-marked factual messages, polls Postgres for their `message_embedding` rows, then asks
`/ask` about the fact and asserts the reply references it. `DossierRealTests.fs` (Slice 5b)
sends three GUID-marked personal-fact messages from the test user, triggers the nightly
dossier job via `POST /test/run-job?name=dossier_nightly_update` (`RealEnv.RunJobUrl` —
reachable because `DevDb.applyRealSettingsAsync` now forces `TEST_MODE=true` on every real-
test run), polls `person_dossier` for a row, then mentions the bot asking "что ты обо мне
знаешь?" and asserts the reply — and a follow-up `/dossier` — reference a seeded fact. STT
real tests accept any one of the test phrase's three words (case-insensitive) in the
transcript/reply — real STT has, once, misrecognized a single word of the short Russian test
phrase, so requiring all three verbatim made the assertion flakier than the feature it's
testing; `VoiceRealTests.fs` additionally self-retries once on any assertion failure (sends a
SECOND freshly-TTS'd voice note and re-asserts, logging the retry) for the same reason.

`make smoke` is a separate, narrower check: the same bot/DB/seed but containerized (built
from `src/Dockerfile.bot`), to catch anything the bare-`dotnet run` real-test path could hide
(missing runtime deps, `ASPNETCORE_URLS` binding, container networking). See
`src/AlitaBot/README.md`'s "Containerized smoke test" section.

## Container CLI

`podman`, not `docker` — `DOCKER` in the Makefile auto-detects the real binary since `docker`
is commonly a shell alias invisible to `make`.
