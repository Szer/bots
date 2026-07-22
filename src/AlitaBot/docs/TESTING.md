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
| `AZURE_FOUNDRY_ENDPOINT`, `AZURE_FOUNDRY_KEY`, `ALITA_LLM_DEPLOYMENT`, `ALITA_STT_DEPLOYMENT`, `ALITA_TTS_DEPLOYMENT`, `ALITA_IMAGE_DEPLOYMENT` | real Azure AI Foundry deployments (only needed for `RESPONDER_MODE=llm` / voice / image real-tests; each real-test suite self-skips via `Assert.Skip` when its deployment env var is missing) |
| `RESPONDER_MODE`, `STREAM_MODE` | optional overrides (default `echo`/`edit`), upserted into `bot_setting` on every run |

```bash
make real-test    # dev DB + Release build + full real-Telegram smoke suite
make alita-logs   # tail bot.log / ngrok.log from the last run
make alita-clean  # tear down containers + deleteWebhook (run when done for the session)
```

`SmokeTests.fs` is the core conversational suite; `VoiceRealTests.fs`, `VisionRealTests.fs`,
`ImageGenRealTests.fs` cover their respective slices and self-skip when their deployment
isn't configured. STT real tests accept any one of the test phrase's three words
(case-insensitive) in the transcript/reply — real STT has, once, misrecognized a single word
of the short Russian test phrase, so requiring all three verbatim made the assertion flakier
than the feature it's testing.

`make smoke` is a separate, narrower check: the same bot/DB/seed but containerized (built
from `src/Dockerfile.bot`), to catch anything the bare-`dotnet run` real-test path could hide
(missing runtime deps, `ASPNETCORE_URLS` binding, container networking). See
`src/AlitaBot/README.md`'s "Containerized smoke test" section.

## Container CLI

`podman`, not `docker` — `DOCKER` in the Makefile auto-detects the real binary since `docker`
is commonly a shell alias invisible to `make`.
