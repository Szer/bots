# Testing

AlitaBot has three tiers, same model as VahterBanBot/CouponHubBot:

1. **Hermetic suite** — `tests/AlitaBot.Tests` (Testcontainers + `FakeTgApi`/
   `FakeAzureOcrApi`, no network egress, no external API cost). This is **the PR gate**
   (`alita-build.yml`) and the default way to verify a change locally — run it before
   pushing, exactly like you would for Vahter or Coupon.
2. **Real suite** — `tests/AlitaBot.RealTests` (`make real-test`), a developer
   quick-iteration harness against a real Telegram test bot (ngrok tunnel + MTProto test
   user) and, for `RESPONDER_MODE=llm`, real Azure AI Foundry / Gemini. Run it
   deliberately, not as a default pre-push check — a full run invokes paid generation
   APIs (image gen, music gen, TTS/STT, chat completions). Prefer scoping to the test(s)
   you're iterating on:
   ```bash
   dotnet test tests/AlitaBot.RealTests --filter "FullyQualifiedName~ImageGenRealTests"
   ```
   or via `make real-test FILTER="FullyQualifiedName~ImageGenRealTests"` (see the Makefile section below).
3. **Full AKS E2E** — `.github/workflows/alita-real-test.yml`, the same
   `tests/AlitaBot.RealTests` suite run against a transient deployment in the shared
   `alita-test` AKS namespace. This does **not** run per-PR — it's `workflow_dispatch`
   only, invoked on demand:
   ```bash
   gh workflow run alita-real-test.yml --ref <branch>
   ```
   Run it before merging a change you want validated end-to-end over the public
   `https://alita-test.szer.dev` endpoint, or whenever you want CI-shaped confidence
   beyond the hermetic gate — not automatically on every push, since (like tier 2) it
   invokes real Telegram delivery and paid LLM/image/music generation calls. See
   `src/AlitaBot/README.md`'s "CI real-test flow" section for how it's wired.

## Hermetic suite — `tests/AlitaBot.Tests`

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
- `ImageGenTests.fs` — `/img`/`!img` command parsing, text-to-image and img2img (reply-to-photo), disabled/empty-prompt/failure paths (Azure backend, `IMAGE_PROVIDER=azure`).
- `GeminiTests.fs` — `/img` routed to the Gemini backend (`IMAGE_PROVIDER=gemini`, `FakeAzureOcrApi`'s additive `/gemini/*` routes) and provider switching back to Azure.
- `SongTests.fs` — `/song` (Lyria music generation): delivery + `message_log`/`llm_usage` rows, per-user cooldown, over-`SONG_MAX_CHARS`, empty prompt.
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
  skip the now-opted-out user). The endpoint is fire-and-forget (returns before the job
  finishes — see README's "ScheduledJobs.fs" section), so every test polls for the job's
  effect (a DB row, or a new fake chat-completions call) instead of asserting immediately
  after `runJob()` returns; "prove nothing happened" cases wait out a fixed window instead.
  **Truncates `message_log`/`message_embedding`/
  `interaction_memory`/`person_dossier`/`memory_opt_out`** (`fixture.TruncateMemoryTables()`,
  admin connection) before every fact — the nightly job scans ALL of `message_log` for
  "active users", and this fixture's `TimeProvider` is a `FakeTimeProvider` that's never
  advanced across the whole assembly run (every seeded message across every test class shares
  one frozen `sent_at`), so without truncating, every user_id ever seeded by an earlier test
  class would show up as "active in the last 24h" too.
- `PersonaTests.fs` — Slice 6: the outcome router (`OUTCOME_WEIGHTS` at 100/0/0, 0/100/0,
  0/0/100 — reply/silence/emoji, each toggled via `fixture.SetBotSetting` + `ReloadSettings`),
  MarkdownV2 final-message rendering (a scripted `**bold** and `code`` reply lands as the
  final `editMessageText` with `parse_mode=MarkdownV2` and the correctly-escaped payload,
  `message_log` keeping the plain unescaped text), the plain-text fallback on a simulated
  Telegram 400 (`fixture.SetMdv2Rejected(true)` — a new `FakeTgApi` knob, `/test/mock/
  rejectMdv2`, that rejects any `sendMessage`/`editMessageText` carrying `parse_mode=
  MarkdownV2`), the rewriter pass (`REWRITER_ENABLED=true` -> two chat-completions calls,
  `message_log`'s bot reply is the SECOND script's text), and reply-context enrichment (a
  reply-to-message's author + text show up in the LLM request body).
- `ProactiveTests.fs` — Slice 8: the morning digest (`POST /test/run-job?name=digest_daily`
  — a chat above `DIGEST_MIN_MESSAGES` gets a scripted digest as a fresh, non-reply
  `sendMessage`; a chat below the threshold gets nothing), willingness-gated interjections
  (`INTERJECT_PROBABILITY=1.0` + a seeded burst — `BURST_MSGS`/`BURST_SPEAKERS` lowered for
  test speed — sends a scripted plain (non-reply) message; a scripted `PASS` stays silent
  though the LLM call still lands; an active cooldown or `p=0.0` skip the LLM call
  entirely), and meme reactions (a scripted `{"action":"react",...}` sets a message
  reaction; `{"action":"pass"}` and malformed JSON both do nothing, the malformed case also
  proving the bot stays responsive afterward). Interjection tests each use a **dedicated
  chat id** (never `fixture.TargetChatId`) — the cooldown check looks at every bot message
  ever logged for a chat, and the frozen `FakeTimeProvider` means any earlier test's bot
  reply to the shared target chat would otherwise "recently" cool it down forever.

Container logs land in `test-artifacts/AlitaBot.Tests/AlitaTestContainers/` (`bot.log`,
`postgres.log`, `flyway.log`, `fake-tg-api.log`, `fake-azure-ocr.log`) on pass or fail.

Tests are black-box: they POST synthetic updates at the bot's webhook (`fixture.SendUpdate`)
and assert against `FakeTgApi`'s captured calls (`fixture.GetFakeCalls`) and Postgres rows
(`fixture.Query`/`QuerySingleOrDefault`), never against internal F# types.

## Real suite — `tests/AlitaBot.RealTests` (`make real-test`)

A developer quick-iteration harness: exercises the bot end-to-end against real Telegram (a
dedicated test bot + an MTProto test-user client playing the human side) and, for
`RESPONDER_MODE=llm`, real Azure AI Foundry / Gemini. Run it deliberately, not as a default
pre-push check — a full run invokes paid generation APIs (image gen, music gen, TTS/STT,
chat completions). Prefer scoping to what you're iterating on via `dotnet test`'s `--filter`
(or `make real-test FILTER=<pattern>`, see the Makefile section below) instead of running
the whole suite. See `src/AlitaBot/README.md`'s "One-time setup" section for how to
provision the test bot, ngrok tunnel, and MTProto session.

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
| `ALITA_GEMINI_API_KEY` | real Gemini API key (Nano Banana images, Lyria music) — `ImageGenRealTests`/`SongRealTests` self-skip when unset, or when set but Gemini's own billing gate blocks generation for this key's project (`GeminiProbe.fs` probes this before asserting — see `docs/TECH-DEBT.md`) |
| `RESPONDER_MODE`, `STREAM_MODE` | optional overrides (default `echo`/`edit`), upserted into `bot_setting` on every run |

```bash
make real-test                        # dev DB + Release build + FULL real-Telegram suite — invokes paid APIs, use sparingly
make real-test FILTER="FullyQualifiedName~ImageGenRealTests"  # scoped: only the matching test(s) — prefer this for iteration
make alita-logs                       # tail bot.log / ngrok.log from the last run
make alita-clean                      # tear down containers + deleteWebhook (run when done for the session)
```

`ProactiveRealTests.fs` (Slice 8, both require `RESPONDER_MODE=llm`): enables
`DIGEST_ENABLED`, seeds a few topical messages, triggers `digest_daily` the same
`/test/run-job` way `DossierRealTests.fs` triggers the nightly job, and awaits a bot
message referencing one of a few "generous fuzzy" topic needles (F#/YAML/кофе — a real
digest paraphrases, so no literal GUID marker is expected to survive); and sets
`INTERJECT_PROBABILITY=1.0`, `BURST_MSGS=3`, `BURST_SPEAKERS=1`,
`INTERJECT_COOLDOWN_MINUTES=0`, and an `INTERJECT_PROMPT` overridden to a deterministic
"ответь ровно: INTERJECT-OK" instruction, sends 3 quick messages, and awaits that exact
marker. Both restore every setting they touched afterward via `try/with` + re-raise +
an unconditional restore call, not a plain `finally` (F#'s `task` CE doesn't allow `do!`
inside `finally`, and an un-awaited fire-and-forget restore risks the same async-restore
race PersonaRealTests' emoji test already had to work around).

`SmokeTests.fs` is the core conversational suite; `VoiceRealTests.fs`, `VisionRealTests.fs`,
`ImageGenRealTests.fs`, `AskRealTests.fs`, `DossierRealTests.fs`, `PersonaRealTests.fs` cover
their respective slices and self-skip when their deployment isn't configured.
`ImageGenRealTests.fs` runs against Gemini when `ALITA_GEMINI_API_KEY` is set (falling back
to Azure's `ALITA_IMAGE_DEPLOYMENT`), and `SongRealTests.fs` (Gemini/Lyria `/song`) also
gates on `ALITA_GEMINI_API_KEY` — both self-skip if Gemini's own billing gate blocks
generation (`GeminiProbe.isQuotaBlocked`, one real HTTP probe before the Telegram round trip
assertion), rather than hard-failing on an external blocker.
`PersonaRealTests.fs` (Slice 6, all three require `RESPONDER_MODE=llm`): (a) flips
`OUTCOME_WEIGHTS` to `emoji=100` (direct `bot_setting` upsert + `/reload-settings`, restored
in a `finally`), mentions the bot, and awaits a `TL.UpdateMessageReactions` on the triggering
message (`TgUserClient.TryAwaitReactionOn` — see its doc comment for why a plain MTProto
client sees this update and not the bot-only `UpdateBotMessageReaction*` pair) plus 15s of
silence on a text reply; (b) asks for a markdown-shaped reply and best-effort-asserts the
settled message carries real MTProto entities (`TgUserClient.LastEntitiesOf`) once MDV2 has
round-tripped through Telegram's own parser; (c) asserts a real reply contains none of a
short assistant-isms list ("как ИИ", "отличный вопрос"). `AskRealTests.fs` (Slice 5a) sends two
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

## Full AKS E2E — `.github/workflows/alita-real-test.yml`

Runs the same `tests/AlitaBot.RealTests` suite against a transient deployment in the shared
`alita-test` AKS namespace, over the public `https://alita-test.szer.dev` endpoint. Manual
`workflow_dispatch` only — it does not trigger on `pull_request`, since (like the real suite
above) it invokes real Telegram delivery and paid LLM/image/music generation calls:

```bash
gh workflow run alita-real-test.yml --ref <branch>
```

Singleton via `concurrency: group: alita-aks-real-test` — only one run uses the shared
namespace at a time, so concurrent dispatches queue rather than collide. See
`src/AlitaBot/README.md`'s "CI real-test flow" section for the full wiring (secrets,
`ALITA_REAL_MODE=remote`, node-capacity notes, teardown contract).

## Container CLI

`podman`, not `docker` — `DOCKER` in the Makefile auto-detects the real binary since `docker`
is commonly a shell alias invisible to `make`.
