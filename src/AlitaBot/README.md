# AlitaBot

A conversational Telegram chatbot: replies when mentioned (`@username`, "алита"/"alita", or a
reply-to-bot), with full message history logged for context. Two responder modes (`echo` for
walking-skeleton testing, `llm` for real Azure AI Foundry chat completions) and three streaming
renderers for how an LLM reply is delivered to Telegram as it's generated.

## Architecture sketch

```
Telegram webhook
  -> BotInfra.WebhookHost (shared webhook plumbing: secret-token check, JSON parse)
  -> BotService.OnUpdate
       - filters to TargetChatIds, requires text + from
       - logs the incoming message to message_log (DbService)
       - if triggered (mention / name / reply-to-bot): ResponderService.Respond
  -> ResponderService.Respond   ("echo" | "llm")
       echo: pong: <text>, sent as one reply
       llm : DbService.RecentContext (last N message_log rows)
             -> AzureFoundryChat.CompleteStream   (IAsyncEnumerable<ChatChunk>, SSE)
             -> ReplyRendererFactory.ForMode(STREAM_MODE)
             -> IReplyRenderer.Render(chatId, replyToMessageId, chunks, ct)
  -> BotHelpers (Funogram Req.* wrappers) -> BotInfra.ITelegramApi -> Telegram Bot API
```

`BotConfiguration` is `IOptions<_>`-backed via `BotInfra.LiveOptions` — `POST /reload-settings`
re-reads `bot_setting` without a pod restart (see AGENTS.md's Settings configuration section).

### Renderers (`Services/ReplyRenderer.fs`)

All three implement `IReplyRenderer.Render` and share one failure policy: `ContentFiltered`
before any text → fixed RU reply; any other failure before text → stay silent (Warning log);
failure after text started → finalize whatever text arrived.

| Renderer | STREAM_MODE | Behavior |
|---|---|---|
| `PlainRenderer` | `plain` | Buffers the whole stream, sends one message at the end. |
| `EditThrottleRenderer` | `edit` | Sends on the first chunk, then edits that message whenever ≥1.5s elapsed **and** ≥40 new chars accumulated, plus a final edit at completion. |
| `DraftRenderer` | `draft` | Streams via `sendMessageDraft` (Bot API 10.2) throttled to ≥500ms per update, then sends **one real message** at the end. On the first `sendMessageDraft` rejection for a chat, permanently (process-lifetime) falls back to `EditThrottleRenderer` for that chat — see findings below. |

**MarkdownV2 formatting applies once, at the FINAL message only** (Slice 6,
`Services/MarkdownRenderer.fs` + `ReplyRenderer.fs`'s `Mdv2Delivery` module) — every streamed
partial along the way (the first `sendMessage`, every throttled `editMessageText`) stays plain
text, unchanged from before this slice. `MarkdownRenderer.toMarkdownV2` walks Markdig's parsed
CommonMark AST (we do **not** vendor a third-party MDV2 emitter) and emits Telegram's
MarkdownV2 wire syntax directly: bold (`**`/`__` → MDV2 `*...*`), italic (`*`/`_` → MDV2
`_..._`), inline `` `code` ``, fenced/indented code blocks (→ `` ```lang\n...\n``` ``), links,
(un)ordered lists (→ `•`/`N\.` prefixed lines), blockquotes (→ `>`-prefixed lines), and a
best-effort `||spoiler||` pass over literal text (Markdig has no native spoiler syntax).
Everything else degrades to escaped plain text — never raw/unescaped output. `Mdv2Delivery`:

- **Normal case**: `sendMessage`/`editMessageText` with `parse_mode=MarkdownV2`.
- **Telegram 400 (bad entities)**: falls back to a plain-text resend/edit — Warning-logged,
  counted (`alitabot_mdv2_fallback_total`).
- **MDV2 form exceeds 4096 chars**: escalates to `Req.SendRichMessage` (the `markdown` field
  of `InputRichMessage`) instead of a formatted `sendMessage`; a chat that rejects THAT falls
  back to plain multipart sends (`BotHelpers.sendTextReply` per ≤4096-char chunk), memoized
  per chat from then on — same probe-and-fallback shape as `DraftRenderer`'s per-chat memo. An
  over-length final **edit** (streaming path) skips the rich-message escalation entirely and
  just edits plain — an edit can't turn into multiple messages the way a fresh send can.

Command outputs (`/usage`, `/help`, etc.) are unaffected — they still go through
`BotHelpers.sendTextReply` directly, no MDV2 formatting.

### Rewriter pass (`REWRITER_ENABLED`, off by default)

A second, cheap **non-stream** LLM call that rewrites `ResponderService`'s final reply text —
`REWRITER_PROMPT` bot_setting ("перепиши как живой человек в чате: убери ассистентские
обороты, сократи, сохрани смысл и факты") — before it's rendered. **Documented tradeoff**: the
main LLM call is also forced non-stream while this is on, so there's no incremental streaming
UX at all in that mode (`ResponderService.respondWithRewriter`) — the default
(`REWRITER_ENABLED=false`) path is completely untouched: streaming + the three renderers above
behave exactly as before. Both LLM calls are usage-recorded (`kind='chat'`) the same as every
other `IChatCompletion.Complete` call. Skipped entirely for command outputs (`/summary`, `/ask`,
etc.) — only `ResponderService.Respond`'s "llm" branch ever calls it. Failure policy mirrors
`PlainRenderer`: `ContentFiltered` on the main call → fixed RU reply; any other failure/empty
text on the main call → silence; a failed/empty **rewrite** falls back to sending the
unrewritten text rather than dropping the reply.

### Outcome router (`OUTCOME_WEIGHTS`, `Services/OutcomeRouter.fs`)

A TRIGGERED non-command message doesn't always get a text reply — `OUTCOME_WEIGHTS` bot_setting
(`{"reply":100,"silence":0,"emoji":0}` by default, keeping the pre-S6 always-reply behavior)
rolls a weighted outcome in `BotService.handleTriggerableMessage`:

- **reply** — the normal path, unchanged (`ResponderService.Respond`).
- **silence** — says nothing at all; no LLM call, `outcome=silence` (`alitabot_messages_total`).
- **emoji** — a tiny non-stream LLM call picks ONE emoji from Telegram's allowed reaction set
  (👍❤️🔥😁🤔🤯😱🤬😢🎉🤩💩🤡🥱 — the prompt lists exactly these and instructs "output only
  it"), then `Req.SetMessageReaction` reacts to the triggering message — no text reply,
  `outcome=emoji`. A malformed/unlisted model answer falls back to the set's first emoji rather
  than refusing to react; a failed LLM call or a rejected `SetMessageReaction` is Warning-logged
  and simply skipped.

`OutcomeRouter.pick` is a pure, deterministic function over `(weights, roll: float in [0,1))` —
the actual `Random.Shared.NextDouble()` draw lives at the call site. Non-positive/all-zero
weights degrade to "reply" (a trigger is never silently dropped by a misconfigured setting).

### Persona (`SYSTEM_PROMPT`)

The S1 placeholder ("дружелюбный участник чата, отвечай кратко") is replaced with Алита's real
character prompt (RU, ~170 words, tuned for `gpt-5-mini`): cynical-warm, chat-native for a
~30-person IT chat — sharp and laconic by default, dark humor welcome, zero assistant-isms
("отличный вопрос", emoji spam, disclaimers) banned outright, speaks as a chat member (not a
service), addresses people by the names it sees in the transcript, and leans on dossiers
(Slice 5b recall injection) for what it remembers about them. `src/alita-bot/dev-bot-settings.sql`
carries the dev/test seed value; the **production** value is tuned live via `bot_setting`
(`AGENTS.md`'s "Settings seeds, not migrations") and doesn't need to match the seed exactly.

### Context enrichment

Two additive-only prepends onto the triggering user's turn in the LLM request
(`ResponderService.buildRequest`) — neither is persisted in `message_log`, so both are
recomputed fresh from the live `Message` on every request, not carried by context-window rows:

- **Reply-quote**: when the triggering message replies to another one, that message's author +
  text (falling back to Caption for a photo, same as everywhere else a photo's "text" is
  needed) is quoted as `[в ответ на {author}]: {text}`.
- **Forward attribution**: when the triggering message itself is a forward (`Message.
  ForwardOrigin`), attributes it to a channel post, a user (or one who hid their identity), or
  an anonymous chat/group post.

## Image generation (`/img`, `!img`, Phase-1 Slice 3 + Gemini provider slice)

`/img <prompt>`, `!img <prompt>`, or `/img@{BOT_USERNAME} <prompt>` in a target chat generates
an image and replies with it as a photo; the same command sent **as a reply to a photo
message** switches to img2img (the replied photo is downloaded and passed as the source
image). Command messages are handled entirely by `BotService.tryParseCommand`/
`handleImageCommand` and never reach `ResponderService` — they don't trigger the LLM
responder even if the prompt also happens to contain the bot's name.

**Provider routing (`IMAGE_PROVIDER`, default `"gemini"`):** `/img` dispatches to either
Azure (`AzureFoundryImageGen`, images/generations + images/edits) or Gemini
(`GeminiImageGen`, Nano Banana — see "Gemini provider" below) via `AlitaBot.Llm.
ImageGenRouter`, which reads the hot-reloadable `IMAGE_PROVIDER` bot_setting fresh on every
call — `/reload-settings` flips providers with no restart. Default is `"gemini"`: Azure
image quota is still 0 in this subscription (below), so Gemini is the only backend that can
actually generate anything today; flipping `IMAGE_PROVIDER` back to `"azure"` once quota
lands needs zero code changes.

Behavior: logs the command as `[img-cmd] {prompt}`, sends a "рисую..." placeholder, generates,
deletes the placeholder and sends the photo captioned with the prompt (truncated 100 chars) plus
cost when `LLM_PRICING` has a matching entry for the active provider's model/deployment —
`per_image_<quality>` for Azure (keyed by `IMAGE_DEPLOYMENT`), flat `per_image` for Gemini
(keyed by `GEMINI_IMAGE_MODEL`, no quality tiers) — logs the bot's reply as
`[image] {truncated prompt}`. Empty prompt → RU usage hint, no provider call. `IMAGE_GEN_ENABLED=
false` (bot_setting, default `true`) → RU "выключено" reply, no provider call. Any provider
failure (including an unconfigured/404 deployment, or a missing `GEMINI_API_KEY`) edits the
placeholder into a RU apology instead of crashing or leaving the chat hanging.

`IMAGE_SIZE` (default `1024x1024`) and `IMAGE_QUALITY` (default `medium`) are Azure-only
hot-reloadable bot_settings, passed straight through to Azure's images API (Gemini's Nano
Banana models take neither). **`IMAGE_DEPLOYMENT` is empty by default** — every `gpt-image-*`
model variant (and `dall-e-3`) had 0 quota in this subscription/region at S3 deploy time, so
no real Azure deployment exists yet; see [`docs/TECH-DEBT.md`](docs/TECH-DEBT.md) for the
full story. `tests/AlitaBot.RealTests/ImageGenRealTests.fs` runs against Gemini when
`ALITA_GEMINI_API_KEY` is set (self-skipping only if Gemini's own billing gate blocks it —
see "Gemini provider" below), else self-skips like before until `ALITA_IMAGE_DEPLOYMENT` is
set; the fake-suite tests (`tests/AlitaBot.Tests/ImageGenTests.fs` for Azure,
`GeminiTests.fs` for Gemini + provider switching) exercise the full command/plumbing
behavior against `FakeAzureOcrApi`'s routes in the meantime.

## Commands (Phase-1 Slice 4)

`Services/Commands.fs` grows S3's single-purpose `/img` parsing into a small registry —
name, aliases, description, handler (`AlitaBot.Services.CommandDef`) — that both dispatches
`/cmd`, `!cmd`, and `/cmd@{BOT_USERNAME}` messages (`Commands.tryMatch`) and auto-generates
`/help`'s text (`Commands.helpText`), so the two can never drift out of sync. A command
addressed to a different bot (`/cmd@someOtherBot`) never matches — it falls through to the
normal message/trigger path exactly like an unrecognized command. Command messages never
reach `ResponderService`/the LLM responder, same guarantee `/img` already had.
`alitabot_command_total{command=...}` is incremented once per dispatch, centrally, in
`BotService.OnUpdate` — not by individual handlers.

| Command | Aliases | Description |
|---|---|---|
| `/img <prompt>` | — | Generate an image (see "Image generation" above); reply-to-photo → img2img. Routed to Azure or Gemini by `IMAGE_PROVIDER`. |
| `/model [name]` | — | No arg: show the current model + the model catalog, as real model names (never Azure deployment ids) — see "`/model` and the LLM_MODELS catalog" below. With a catalog model name: switch `LLM_DEPLOYMENT` immediately (in-process, via `BotInfra.ISettingsReloader` — the same path `/reload-settings` uses) and persist it. |
| `/summary [count]` | `/tldr` | Speaker-attributed digest of the last `count` (default 200, capped 500) `message_log` rows for the chat, via a non-stream LLM call with the `SUMMARY_PROMPT` bot_setting. A normal, whole-chat-visible reply — previously sent ephemerally, retired after staging feedback found ephemeral replies invisible in practice, see "Ephemeral message probe [RETIRED]" below. |
| `/usage` | — | Today + last-7-days call counts and USD cost from `llm_usage`, broken out by model and by top-5 user. |
| `/ask <question>` | — | Semantic search over this chat's message history (see "Memory" below): answers grounded in the nearest matching quotes, cited by author and date. Empty question → RU usage hint. |
| `/say [voice] <text>` | — | Synthesize `text` as a voice note (see "`/say`, `/sql`, cost footer" below); reply-to-message with no text of its own → voices ITS text. |
| `/song [(style)] <lyrics or description>` | — | Generate music via Gemini's Lyria (see "Gemini provider" below), delivered as an audio attachment with a title. Optional leading `(style hint)`, e.g. `/song (рок-баллада) текст`. Cooldown `SONG_COOLDOWN_SECONDS` per user. |
| `/sql <question>` | — | Admin-gated (`ADMIN_USER_IDS`) natural-language SQL over Alita's own database, rendered as a table (see "`/say`, `/sql`, cost footer" below). |
| `/help` | `/start` | Auto-generated list of the above, from the registry. |

### `/model` and the `LLM_MODELS` catalog

Staging feedback: `/model` used to display the Azure AI Foundry *deployment id* directly
(e.g. `alita-gpt-5-mini`) — "we don't have our own models". Deployment ids are wire-call
plumbing, not model identity: `AZURE_FOUNDRY_ENDPOINT` (`szer-foundry.cognitiveservices.azure.com`)
is a Foundry resource **shared with other bots in this repo** (CouponHubBot's project/product
agents use the same endpoint — see `src/CouponHubBot/docs/PROJECT-AGENT.md`), so every
AlitaBot deployment is named with an `alita-` prefix purely to avoid collisions in that shared
account's flat deployment list — an operator namespacing convention, not something Azure
requires or something a user should ever see.

`LLM_MODELS` (bot_setting, `JSON_BLOB`) is the model catalog `/model` reads and writes:

```json
[{"model": "gpt-5-mini", "deployment": "alita-gpt-5-mini"}]
```

- **No arg**: looks up the current `LLM_DEPLOYMENT` in the catalog and shows its `model` name,
  plus every `model` name in the catalog. An `LLM_DEPLOYMENT` with no matching catalog entry
  (stale/incomplete `LLM_MODELS`) is shown **verbatim** — no guessing, no partial transformation.
- **With an arg**: matched against catalog `model` names **exactly** — zero string
  transformation (no prefix stripping, no substring matching). A match upserts `LLM_DEPLOYMENT`
  to that entry's `deployment` and reloads live (`BotInfra.ISettingsReloader`, the
  `/reload-settings` path) so the switch takes effect on the very next LLM call. The raw
  `deployment` id is **never** an accepted arg and **never** appears in any `/model` output —
  only `model` names are user-facing, in either direction.

Replaces the earlier `MODEL_ALLOWLIST` (a bare JSON array of deployment ids, which is exactly
what made `/model` show deployment ids in the first place — there was no model-name field to
show instead). See `BotService.handleModelCommand`/`parseLlmModels`/`LlmModelEntry`.

### Usage accounting (`llm_usage`, V2 migration)

Every successful LLM/STT/TTS/image-gen call writes one row to `llm_usage`
(`src/alita-bot/migrations/V2__llm_usage.sql`) from the provider telemetry path
(`AlitaBot.Llm.LlmTelemetry`'s `LlmCall`/`ImageCall`, via the new `IUsageRecorder` —
implemented by `DbService`, injected into `AzureFoundryChat`/`AzureFoundryEmbeddings`/
`AzureFoundrySpeech`/`AzureFoundryImageGen`) — additive to the existing OTel metrics
(`alitabot_llm_tokens_total`/`alitabot_llm_cost_usd_total`), which are unchanged. The write is
fire-and-forget (`BotInfra.Utils.fireAndForget`) so a slow/failed insert never holds up or fails
the actual reply. `chat_id`/`user_id` are threaded through every provider call via a small
`UsageContext` record (`{ ChatId: int64 option; UserId: int64 option }`) — `None` for a call with
no natural chat/user context, stored as `NULL`. `input_tokens`/`output_tokens`/`cost_usd` are also
nullable: STT's wire response carries no usage block at all, and cost is `NULL` whenever
`LLM_PRICING` has no matching entry for that model/deployment (same "no matching entry" case the
existing metrics already tolerate).

### Ephemeral message probe (`/summary`, Bot API 10.2) [RETIRED]

**Status: retired.** `/summary` no longer uses ephemeral delivery — every command reply,
`/summary` included, is now a plain, whole-chat-visible `sendMessage`
(`BotHelpers.sendTextReply`). Decided after staging feedback ("ephemeral messages are not
useful"): they were confirmed accepted by Telegram but never observably delivered (see the
round-2 findings below), so `/summary`'s replies were effectively invisible in practice —
exactly the caveat this section already flagged as unresolved. `BotHelpers
.trySendEphemeralOrReply`/`loggableMessageId` and the standalone `EphemeralProbe.fs`/`make
probe-ephemeral` repro tool have been removed from the codebase; this section is kept
verbatim below as the empirical record of what was tried and found, not as current
behavior.

Bot API 10.2 added ephemeral messages — visible only to one user in a group. Decompiling
`Funogram.Telegram.dll` 10.2.0 (`ilspycmd -t Funogram.Telegram.Req`) confirms the surface exists:
`Req.SendMessage`'s constructor and `Make` factory both carry an optional
`receiverUserId: int64` (wire: `receiver_user_id`); the response `Message` carries a matching
`EphemeralMessageId: int64 option`. There is no separate "SendEphemeralMessage" request type —
it's a normal `sendMessage` call with one extra field.

`BotHelpers.trySendEphemeralOrReply` tries the ephemeral form first (`receiverUserId` = the
`/summary` requester), sends visible only to them; on any Telegram API failure it logs a
Warning, falls back to a normal (whole-chat-visible) reply, and **remembers the chat**
(an in-process `ConcurrentDictionary<int64, byte>`, process-lifetime — mirrors `DraftRenderer`'s
per-chat fallback memo, see `docs/TECH-DEBT.md`) so later `/summary` calls in that chat skip the
ephemeral attempt entirely instead of re-probing it every time. **Caveat confirmed below: even
when Telegram accepts the ephemeral call, this harness could not confirm the requester's own
Telegram client actually receives the message** — `/summary` still degrades gracefully (a
non-2xx from Telegram is the only trigger for the fallback), but "ephemeral" should currently be
read as "best-effort, unverified delivery" rather than a guaranteed answer.

**Empirical findings, round 1** (`tests/AlitaBot.RealTests/CommandRealTests.fs`, bot a plain
member of the basic-group test chat):

| Chat type | `receiver_user_id` on `sendMessage` | Result |
|---|---|---|
| Basic group, bot a plain member | `400 Bad Request: BOT_NOT_ADMIN` — rejected outright, same call shape as `TEXTDRAFT_PEER_INVALID` for drafts (both fail before Telegram fans anything out) | `trySendEphemeralOrReply` falls back to a normal reply automatically; the requester still gets an answer, just visible to the whole chat |

Real log line from that run (`test-artifacts/AlitaBot.RealTests/bot.log`):

```
Ephemeral send rejected for chat -5236484897 — falling back to a normal reply and
remembering for the rest of this process
BotInfra.TelegramApiException: Telegram API error 400: Bad Request: BOT_NOT_ADMIN
```

**Empirical findings, round 2 — bot promoted to GROUP ADMIN** (`make probe-ephemeral` /
`EphemeralProbe.fs`, plus a re-run of `CommandRealTests`'s `/summary` test):

**Side-finding first, because it broke every real test until accounted for:** promoting the test
bots to admin in both test chats caused Telegram to silently migrate both from basic groups to
supergroups — the dialog list now shows the SAME chat title twice, once under the old
basic-group id and once under a new `-100…` supergroup id (`ALITA_TEST_CHAT_ID`/
`ALITA_CI_CHAT_ID` had to be updated to the new ids; the old ids now 400 with `PEER_ID_INVALID`
on send). Not something this slice set out to test, but a real consequence of the admin
promotion worth recording — a future re-probe starting from a basic group should expect the same
migration.

With that fixed, the ephemeral call itself:

| Chat type | `receiver_user_id` on `sendMessage` | Result |
|---|---|---|
| Supergroup, bot **admin** (post-migration test chat) | `200 OK` — accepted. `result.message_id` is always `0`; the real per-send id is `result.ephemeral_message_id` (e.g. `13947911`, `93205090`, `95764773` — a large, non-sequential id space, nothing like the chat's own small sequential message ids) | Accepted server-side, but **never observed by the receiving account's own MTProto client** — no `UpdateNewMessage`/`UpdateEditMessage`/any other update arrived within 20s of the accepted call, and the message never appeared in `Messages_GetHistory` before or after (a plain control `sendMessage` sent moments later, no `receiver_user_id`, arrived via push in well under a second and appeared in history immediately — so the update pump and peer resolution are not the problem) |

Raw HTTP response from `probe-ephemeral` (verbatim):

```
HTTP sendMessage {"chat_id":-1004443216370,"receiver_user_id":8818233083,"text":"ephemeral reply to <marker>"}
-> {"ok":true,"result":{"message_id":0,"ephemeral_message_id":13947911,
   "from":{"id":8864978376,"is_bot":true,"first_name":"Alita Test","username":"alita_llm_chat_test_bot"},
   "chat":{"id":-1004443216370,"title":"Alita Test Chat","type":"supergroup"},
   "date":1784705614,
   "receiver_user":{"id":8818233083,"is_bot":false,"first_name":"Ayrat","last_name":"Ru","language_code":"en"},
   "text":"ephemeral reply to <marker>"}}
```

The receiving account here is the SAME MTProto account that sent `/summary` (this harness's
single-account limitation — see `TgUserClient`'s doc comments), which if anything should make
delivery *easier* to observe than a genuine third party would; it still saw nothing, in either
direction (live push or history pull).

**Two concrete bugs this surfaced, both fixed in this slice:**

1. **`message_log` silently dropped every ephemeral reply after the first one per chat.**
   `handleSummaryCommand` logged `sent.MessageId` (Bot-API field) verbatim; since that's always
   `0` for an accepted ephemeral send, and `message_log` has `UNIQUE(chat_id, message_id)` with
   `ON CONFLICT DO NOTHING`, only the very first ephemeral `/summary` reply in a chat's
   process-lifetime ever got persisted — every later one silently no-opped, no exception, no log
   line, just a missing row (found by directly querying `message_log` after a real-test run: one
   row with `message_id = 0` per chat, no matter how many `/summary` calls had actually run).
   Fixed by `BotHelpers.loggableMessageId`: logs `EphemeralMessageId` instead of `0` when that's
   what an accepted ephemeral send actually returned.
2. Real test's `/summary` case previously used `AwaitReplyTo` (a live-update wait) to observe the
   reply — now proven to hang forever once the bot is admin, since the message never arrives that
   way. `CommandRealTests` now confirms the ephemeral send purely from `message_log` (via
   `BotHelpers.loggableMessageId`'s value being EphemeralMessageId-shaped, i.e. large) and
   separately asserts the reply never shows up in `Messages_GetHistory` — see the test for the
   exact assertions.

**Decision, round 2: `/usage` and `/help` were NOT switched to ephemeral delivery.** The task
motivating this probe was "does ephemeral now work, so more chat-spam-reducing commands can use
it" — given the accepted-but-unobservable-delivery finding above, defaulting `/usage`/`/help` to
a delivery mechanism this harness cannot confirm reaches anyone would risk turning "always get an
answer, visible to the chat" into "sometimes get nothing at all", for commands used far more
casually than `/summary`. `/summary` itself was initially left as-is (already shipped, already
falls back on any Telegram-side rejection) rather than rolled back — see
`docs/TECH-DEBT.md` for that reasoning at the time.

**Final decision (staging feedback): retired, not extended.** The "action, still open" below was
never resolved — nobody confirmed genuine delivery — and staging surfaced exactly the predicted
failure mode: `/summary`'s replies were invisible to users. Rather than keep chasing delivery
confirmation, ephemeral sending was removed entirely; see the "[RETIRED]" note at the top of this
section. The unresolved confirmation question (a second Telegram account watching live, or an
official client observing a reply in an admin-bot chat) is moot now that nothing in this repo
sends ephemeral messages — `EphemeralProbe.fs`/`make probe-ephemeral`, the from-scratch repro
tool, was removed along with the feature. DM/private-chat `receiver_user_id` behavior remains
(and will now stay) unprobed.

## Memory: per-message embeddings + `/ask` (Phase-1 Slice 5a)

pgvector-backed semantic search over a chat's own history — the first slice of the
"memory" initiative, on top of `message_log` rather than replacing it.

### Storage (`message_embedding`, V3 migration)

`src/alita-bot/migrations/V3__message_embedding.sql` adds `CREATE EXTENSION IF NOT EXISTS
vector;` plus a `message_embedding` table: `message_log_id BIGINT PRIMARY KEY REFERENCES
message_log(id) ON DELETE CASCADE`, `embedding vector(1536) NOT NULL`, `embedded_at
TIMESTAMPTZ`, and an HNSW index (`vector_cosine_ops`) matching the `<=>` (cosine distance)
operator `/ask`'s search uses. The `vector` extension is **not** marked `trusted` in its
control file (`pgvector/pgvector:pg17`'s `vector.control` has no `trusted = true` line),
so `CREATE EXTENSION vector` needs a superuser even though Flyway's `admin` role already
owns the database — confirmed empirically (`permission denied to create extension
"vector" ... Must be superuser`). `src/alita-bot/init.sql`'s `admin` role is granted
`SUPERUSER` for exactly this (dev/test only, same "no need for prod DB" posture as the
rest of that file — a real deployment's Postgres role setup is out of this repo's scope).

Postgres image is pgvector's build of postgres:17 (`pgvector/pgvector:pg17`) everywhere
AlitaBot's tests/dev/CI provision their own Postgres — `src/alita-bot/docker-compose.dev.yml`,
`.github/k8s/alita-test/postgres.yaml`, and the fake-suite's `BotContainerConfig.PostgresImage`
(`tests/AlitaBot.Tests/ContainerTestBase.fs`). `BotContainerConfig.PostgresImage` is an
additive field on the shared `BotTestInfra.BotContainerBase` (default `"postgres:17.10"`)
so vahter/coupon's test containers are untouched.

### Inline embedding pipeline

Every successful `message_log` insert — sender messages **and** the bot's own replies
alike — is embedded in the background and written to `message_embedding`
(`BotService`'s `tryEmbed`/`logAndEmbed`, a drop-in wrapper around every `db.LogMessage`
call site). Entirely best-effort: `fireAndForget` (`BotInfra.Utils`) plus explicit
`LlmError` handling means an embedding failure is Warning-logged and counted
(`alitabot_embedding_failures_total`) but **never** affects the reply path — the Telegram
reply has already been sent by the time embedding runs. Skipped entirely (no Azure call
at all) when:
- `EMBED_MESSAGES=false` (bot_setting, `FEATURE_FLAG`, default `true`),
- the text is shorter than `EMBEDDING_MIN_CHARS` (bot_setting, default `3`), or
- the text looks like a pure command invocation — bare `/xxx`/`!xxx`, or the `"[xxx-cmd]
  ..."` tagging convention `handleSimpleCommand`/`handleImageCommand`/`handleSummaryCommand`/
  `handleAskCommand` already use to log command messages (`isPureCommandText`) — neither
  carries conversational content worth indexing.

Embeddings are produced via the existing `IEmbeddings`/`AzureFoundryEmbeddings`
(`EMBEDDING_DEPLOYMENT` bot_setting) in batches of 1, threaded through `IUsageRecorder`
the same way every other LLM call type is (`llm_usage.kind = 'embedding'`).

### `/ask <question>`

1. Embeds the question (same `IEmbeddings` call as the pipeline above).
2. `DbService.SemanticSearch` pulls the `ASK_TOP_K` (default `8`) nearest
   `message_embedding` rows for *this chat* by cosine distance (pgvector `<=>`, served by
   the HNSW index), then filters to `ASK_SIM_FLOOR` (default `0.5`) cosine similarity —
   two-stage on purpose: filtering by similarity first would force a full scan, since
   pgvector can't push a similarity `WHERE` into the index, so the index serves the
   `LIMIT` and the floor is applied to that candidate set afterward.
3. No candidates above the floor → a fixed RU "ничего подходящего не нашла" reply,
   deterministic, no LLM call at all (`ask_no_matches` outcome).
4. Otherwise builds a context block (author, date, quoted text — oldest first) and
   answers via a non-stream `IChatCompletion.Complete` call with the `ASK_PROMPT`
   bot_setting as the system message, replied normally (not ephemeral — S4's ephemeral
   pattern was judged not worth it here: unlike `/summary`, `/ask` is naturally a
   question-and-answer exchange other chat members plausibly want to see too).

Empty question → RU usage hint, no embedding/LLM call at all.

### Fake-suite testing (`tests/AlitaBot.Tests/MemoryTests.fs`)

`FakeAzureOcrApi` gained an `/openai/deployments/{deployment}/embeddings` route
(`Handlers.handleEmbeddings`) returning **deterministic hash-of-text vectors**
(`FakeAzureOcrApi.Embedding.embed`) rather than fixed/scripted ones: tokenize (Unicode
letter/digit runs — handles Cyrillic), hash each token to one of 1536 dimensions
(FNV-1a mod 1536), weight by occurrence count, L2-normalize. Two texts sharing
vocabulary land close together (nonzero cosine similarity on the shared dimensions);
texts with disjoint vocabulary land near-orthogonal (~0 similarity, modulo rare hash
collisions). That's what lets the fake suite assert *real* semantic separation — "the
relevant seeded message is in `/ask`'s LLM context, the irrelevant one isn't" — without a
real embedding model. A `SetAzureEmbeddingsScript`/`GetAzureEmbeddingsCalls` pair
(mirroring the other `Azure*Script` members on `BotContainerBase`) is also available for
tests that need to inject a scripted/failing response instead.

## Dossiers: per-person memory + nightly fact extraction (Phase-1 Slice 5b)

Builds a per-person, cross-chat dossier on top of Slice 5a's memory foundation: a nightly
job extracts short facts from what each active person said, dedups them against what's
already known, and folds them into a cumulative RU summary — which ResponderService then
recalls (summary + matching facts) into the system prompt whenever that person triggers
the bot.

### Storage (V4 migration)

- **`scheduled_job`** — distributed job-lease locking (`job_name` PK,
  `last_completed_at`/`locked_until`/`locked_by`), seeded with one row:
  `dossier_nightly_update`.
- **`interaction_memory`** — one row per extracted fact: `user_id`, `content` (short RU
  text), `embedding vector(1536)`, `valid_from`/`valid_to` (nothing sets `valid_to` yet —
  see `docs/TECH-DEBT.md`), `created_at`. HNSW index (`vector_cosine_ops`, not ivfflat —
  ivfflat needs pre-existing rows to pick sane list/probe counts and degrades to a full
  scan on a small/empty table, one of the old design's flaws this fixes) plus a partial
  index on `user_id WHERE valid_to IS NULL` ("active" facts) and a
  `(user_id, created_at DESC)` index for `/dossier`'s newest-facts listing.
- **`person_dossier`** — one row per person: `user_id` PK, `display_name`, `summary`
  (LLM-merged, RU, max 250 words), `updated_at`.
- **`memory_opt_out`** — `/forget-me`: `user_id` PK, `opted_out_at`.

### `ScheduledJobs.fs` — local, not `BotInfra`

Adapted from the old (pre-Funogram) `feature/alita-bot` branch's `src/BotInfra/
ScheduledJobs.fs` (`git show origin/feature/alita-bot:src/BotInfra/ScheduledJobs.fs`) —
same `UPDATE ... RETURNING` atomic lease-acquire pattern — but living inside
`AlitaBot.Services` instead of `BotInfra`: a `BotInfra` change rebuilds and redeploys
VahterBanBot/CouponHubBot too, and nothing outside AlitaBot needs a scheduler yet (see
`docs/TECH-DEBT.md`). `SchedulerHostedService` (`BackgroundService`) ticks every 10
minutes, tries to acquire the `dossier_nightly_update` lease for 02:00 UTC, and on success
runs `DossierService.RunNightlyUpdate()` then releases it. `POST /test/run-job?name=
dossier_nightly_update` (TEST_MODE-only, 404 otherwise) starts the job immediately,
bypassing the lease/schedule check entirely — used by both the fake suite
(`DossierTests.fs`) and the real suite (`DossierRealTests.fs`, reachable because
`DevDb.applyRealSettingsAsync` now forces `TEST_MODE=true` on every real-test run).
**Fire-and-forget** (`BotInfra.Utils.fireAndForget`): the endpoint returns as soon as the
job is kicked off, not once it's finished — the job can run for tens of seconds (two
sequential real LLM calls against Azure AI Foundry), and awaiting it inline held the HTTP
response open long enough to exceed the CI real-test AKS gateway's request timeout
(`504: upstream request timeout`). Callers poll the database (or the fake suite's captured
LLM calls) for the job's effect instead of reading it off the HTTP response.

### Nightly job (`DossierService.RunNightlyUpdate`)

For every `user_id` with at least one non-bot `message_log` row in the last 24h and **not**
in `memory_opt_out`:

1. **Extract** — a non-stream LLM call (`EXTRACT_PROMPT` bot_setting as system message,
   the person's existing summary + their own last-24h messages as user content) that must
   answer with a JSON array of short RU fact strings (`[]` if nothing new). Malformed
   JSON/non-array/non-string elements are silently dropped, never a crash.
2. **Embed + dedup + insert** — each candidate fact is embedded (`IEmbeddings`,
   `EMBEDDING_DEPLOYMENT`), then checked against the cosine similarity of the person's
   nearest ACTIVE fact (`DbService.NearestActiveFactSimilarity`, HNSW-served): **>= 0.90
   cosine → skip** (near-duplicate, e.g. the same fact re-extracted on a later night),
   otherwise inserted as a new active `interaction_memory` row.
3. **Merge** — only when at least one fact was actually new: a second non-stream LLM call
   (`MERGE_PROMPT` bot_setting, RU, max 250 words) folds the existing summary + newly
   inserted facts into an updated summary, upserted into `person_dossier`.

Both LLM calls go through the existing `IChatCompletion`/`IUsageRecorder` plumbing — usage
rows (`kind='chat'`) and telemetry spans (`dossier.extract`, `dossier.merge`) are automatic,
same as every other LLM call in the bot. Per-user failures (LLM/embedding errors, or an
unexpected exception) are caught and logged; one bad user can't abort the whole run.

**Fixes over the old design** (`git show
origin/feature/alita-bot:src/AlitaBot/Services/DossierService.fs`): no similarity floor on
recall (fixed — see below), no fact dedup (fixed — step 2 above), ivfflat-on-an-empty-table
(fixed — HNSW).

### Recall injection (`ResponderService`)

When `DOSSIER_ENABLED` (bot_setting, default `true`) and a triggering message's author has
a `person_dossier` row and isn't opted out: the triggering message's own text is embedded,
the author's `DOSSIER_RECALL_K` (default `5`) nearest ACTIVE facts above `DOSSIER_SIM_FLOOR`
(default `0.60`) cosine similarity are pulled (`DbService.NearestActiveFacts`, same
two-stage index-then-floor shape as `/ask`'s `SemanticSearch`), and both the summary and any
matching facts are appended to the system prompt:

```
Досье автора:
<summary>

Известные факты об авторе:
- <fact>
- <fact>
```

Additive only — no dossier, an opted-out author, an embed failure, or no facts above the
floor all just mean nothing gets appended; the rest of the request build is unaffected.

### Commands

| Command | Description |
|---|---|
| `/dossier [@username]` | No arg: your own dossier. `@username` (leading `@` optional): another chat member's, resolved from their most recent `message_log.username`. Renders the summary plus the newest 5 active facts, or a fixed "пусто, я тебя ещё не изучила" when there's nothing yet (unknown username and "known user, no dossier yet" are deliberately indistinguishable — telling them apart would leak whether a given username has ever posted in the chat). |
| `/forget-me` | Opts the requester out of memory (`memory_opt_out`) and hard-deletes their `interaction_memory`/`person_dossier`/`message_embedding` rows (`DbService.PurgeUserMemory`). `message_log` itself is untouched — it's the shared chat record, not personal memory. From this point on: excluded from the nightly job, the inline embedding pipeline (`BotService.tryEmbed`), and recall injection. |

### Fake-suite testing (`tests/AlitaBot.Tests/DossierTests.fs`)

Same deterministic hash-of-text embeddings as `MemoryTests.fs` — real cosine-similarity
behavior (shared vocabulary → high similarity) without a real embedding model, enough to
assert dedup (an identical fact scripted across two nightly runs yields one active row) and
recall (a fact whose vocabulary overlaps the triggering message shows up in the LLM request
body for that author, never for a different, dossier-less author). Truncates
`message_log`/the dossier tables before every fact (`fixture.TruncateMemoryTables()`) — the
nightly job scans *all* of `message_log` for "active users", and this fixture's frozen
`FakeTimeProvider` means every message ever seeded in the whole assembly run (across every
test class) shares one `sent_at`, so without truncating, unrelated users from earlier test
classes would show up as "active" too.

## Social engine: /roast, /awards, /quote, /karma (Phase-1 Slice 7)

Command-only fun features for a small (~30-person), zero-censorship, dark-humor-normal
IT chat — a private consenting friend group. The bot only roasts on explicit command;
nothing here runs unsolicited. `ROAST_PROMPT`/`AWARDS_PROMPT`/`QUOTE_PROMPT` are
deliberately written sharp, not corporate-soft (`src/alita-bot/dev-bot-settings.sql`
carries the dev/test seed, tuned live in prod exactly like `SYSTEM_PROMPT` — see
AGENTS.md's "Settings seeds, not migrations").

| Command | Description |
|---|---|
| `/roast [@username]` | Roasts a target: an explicit `@username` arg, otherwise the author of the message being replied to, otherwise the invoker themselves. Ammunition: the target's `person_dossier` summary + up to 8 newest active `interaction_memory` facts (no similarity filter — same "just take the newest" posture as `/dossier`) + up to 50 of their own recent `message_log` texts. An opted-out (`/forget-me`) target is roasted ONLY from their recent messages — dossier/facts are never read for them. `ROAST_COOLDOWN_SECONDS` (bot_setting, default 300) per target; a fresh cooldown stamp is only written after an actual successful roast (never on a "no data"/cooldown/failed attempt). No ammunition at all → "этого кадра я ещё не изучила"; still cooling down → "этого уже жарили, дай остыть". Delivered via `Mdv2Delivery.sendFinal` (non-stream LLM call → MarkdownV2 render → reply), same pipeline the LLM responder's own renderers use — the roast text is free-form LLM output that may legitimately contain markdown. |
| `/awards` | Over the last 7 days of this chat's `message_log` (human messages only, capped ~800 rows — `DbService.HumanMessagesSince`), a JSON-mode-style LLM call (`AWARDS_PROMPT`) returns a strict JSON array of 3-5 `{title, user, evidence_quote}` awards, rendered as one `🏆 title — user: „quote"` line each and written to the new `karma` table (`user_id` resolved from `message_log.username` when the LLM's `user` field is a `@handle` it can match — kept unresolved, not dropped, otherwise). Malformed JSON retries once (`completeJsonWithRetry`); still malformed → a fixed RU failure reply, no karma rows written, never a crash. |
| `/quote` | The single most absurd/quotable line from the last 24h of this chat's human, non-command `message_log` rows (capped ~500) — `QUOTE_PROMPT` LLM call returns strict JSON `{author, quote, comment}`, rendered `💬 Цитата дня: „quote" — author. comment`. No messages in the window → a fixed RU reply, no LLM call. |
| `/karma [@user]` | Self (no arg) or another chat member's totals from `karma`: a count plus the newest 3 titles. An unresolvable `@username` or a known user with zero karma rows both render the same fixed "no awards yet" reply (same "don't leak who's ever been seen" posture as `/dossier`'s `NoDossierText`). |

### Storage (V5 migration)

- **`karma`** — one row per awarded title (`/awards`): `id`, `user_id` (nullable — best-
  effort resolution, see above), `username` (the LLM's raw `user` field, always kept),
  `title`, `evidence`, `awarded_at`.
- **`roast_cooldown`** — one row per person who has ever been roasted: `target_user_id`
  PK, `last_roasted_at`. Stamped via the app's own `TimeProvider` (`BotService`'s `now`,
  passed into `DbService.RecordRoast`), not SQL `NOW()` — consistent with the rest of the
  codebase (e.g. `logRow`'s `sent_at`) and required for the cooldown check to behave
  correctly under `TEST_MODE`'s `FakeTimeProvider`, which never agrees with the
  database's own wall clock.

### JSON contracts

Neither `/awards` nor `/quote` uses a server-side "JSON mode" parameter — `ChatRequest`
has no `response_format` field (Azure Foundry's chat-completions plumbing here is a thin
wrapper, see `Llm/AzureFoundryProvider.fs`) — instead the prompt itself instructs the
model to answer with ONLY the JSON, and the response is parsed leniently-but-strictly:
`/awards`' `parseAwardsJson` requires every array element to be a well-formed
`{title, user, evidence_quote}` object (unlike `DossierService.parseFactsJson`, which
silently drops individual malformed entries, a single malformed award entry fails the
WHOLE parse — the plan's "malformed JSON after 1 retry → graceful failure" is a property
of the response as a whole). `completeJsonWithRetry` (`BotService.fs`) is the shared
one-retry-then-give-up helper both commands use.

### Fake-suite testing (`tests/AlitaBot.Tests/SocialTests.fs`)

Same idioms as `DossierTests.fs`: the nightly dossier job seeds a real
`person_dossier`/`interaction_memory` fact (via scripted extraction/merge LLM calls)
before the `/roast` tests exercise it, so `fixture.TruncateMemoryTables()` runs first for
the same "clean active-users slate" reason `DossierTests.fs` needs it. A new
`fixture.TruncateSocialTables()` (`karma`/`roast_cooldown`) keeps `/awards`/`/karma`
row-count assertions deterministic across the shared, `DisableTestParallelization=true`
assembly fixture.

## Proactive behavior: morning digest, interjections, meme reactions (Phase-1 Slice 8)

The bot's default posture is still fully reactive — it only ever speaks when triggered or
commanded. Slice 8 adds three OPT-IN proactive features, each gated by its own
`bot_setting` and **defaulting OFF/0.0** so the bot stays exactly as polite as before until
someone deliberately turns one on live in prod (`AGENTS.md`'s "Settings seeds, not
migrations" — none of this is seeded on by a migration). Every action any of the three
takes is counted by `alitabot_proactive_total{kind=...}` (`docs/OBSERVABILITY.md`).

### Morning digest (`digest_daily`, `DIGEST_ENABLED`)

A `SchedulerHostedService`-driven job (`Services/DigestService.fs`), same lease-acquire
pattern as `dossier_nightly_update` (`Services/ScheduledJobs.fs`) — scheduled daily at
`DIGEST_UTC_HOUR` UTC (default `7`), `POST /test/run-job?name=digest_daily` for TEST_MODE-
only manual triggering. Unlike the dossier job, `DIGEST_ENABLED` (default `false`) gates
the whole thing inside `DigestService.RunDailyDigest` itself — the lease is still acquired
and `last_completed_at` stamped on schedule either way, it just sends nothing while
disabled.

For each `TARGET_CHAT_IDS` chat with at least `DIGEST_MIN_MESSAGES` (default `30`) human,
non-command `message_log` rows in the last 24h (`DbService.HumanMessagesSince` — the same
query `/awards`/`/quote` already use): builds a speaker-attributed transcript, runs it
through a non-stream `DIGEST_PROMPT` LLM call ("утренний дайджест вчерашнего срача — по
темам, с лёгким сарказмом, кто что задвигал, 6-10 строк, без воды"), MDV2-renders the
result, and sends it as a **fresh, non-reply message** to that chat
(`Mdv2Delivery.sendFinalToChat`, a Slice 8 addition to `Services/ReplyRenderer.fs` — same
MDV2-with-Telegram-400-fallback/over-length-escalation policy as `sendFinal`, just without
`reply_parameters` anywhere on the wire, since a scheduled job has no triggering message to
reply to). The sent digest is logged to `message_log` like any other bot message. A chat
below the threshold is silently skipped (span-tagged `below_min_messages`, no LLM call).

### Willingness-gated interjections (`INTERJECT_PROBABILITY`)

On every NON-triggered, non-command text message in a target chat (`BotService.
tryInterject`, fired fire-and-forget from `handleTriggerableMessage`'s "logged" branch —
**after** the message is already logged, never delaying the normal path), three gates must
ALL hold, cheapest first:

1. **Roll** — `INTERJECT_PROBABILITY` (default `0.0`) against `Random.Shared.NextDouble()`.
   No DB round trip at all when this fails.
2. **Burst** — at least `BURST_MSGS` (default `8`) messages from at least `BURST_SPEAKERS`
   (default `3`) distinct authors in the last `BURST_WINDOW_MINUTES` (default `5`),
   `DbService.BurstStats` — a plain `message_log` query, no new table.
3. **Cooldown** — no bot message (`is_bot=TRUE` — a reply OR a previous interjection, both
   logged identically) in this chat in the last `INTERJECT_COOLDOWN_MINUTES` (default `30`),
   `DbService.HasBotMessageSince`. A fired interjection naturally self-cools the chat for
   the next one, since it's logged the same as any reply.

Only once all three hold does a recent-context (`CONTEXT_WINDOW_MESSAGES` rows,
speaker-attributed transcript — same shape `/summary` builds) `INTERJECT_PROMPT` LLM call
fire ("можешь вставить ОДНУ меткую реплику в этот разговор, или ответь ровно PASS если
нечего добавить"). A `"PASS"` response (trimmed, case-insensitive) stays silent
(`interject_pass`); anything else goes out as a **plain (non-reply) message**
(`BotHelpers.sendMessage`, no MDV2 — a deliberately lighter-weight delivery than the
digest's) and is logged like any other bot reply (`interject`).

The whole hook runs through `withChatLock` (the same per-chat `SemaphoreSlim` normal
message handling uses) — since it fires fire-and-forget from inside a callback the lock is
already held by, and that callback finishes (releasing the lock) before the spawned task's
own `WaitAsync()` ever completes, there's no deadlock; the interjection simply queues
behind whatever touches the chat's lock next, same serialization guarantee triggered
messages get.

### Meme reactions (`MEME_REACT_PROBABILITY`)

On every NON-triggered photo message in a target chat (`BotService.tryMemeReact`, same
fire-and-forget/`withChatLock` shape as interjections): `MEME_REACT_PROBABILITY` (default
`0.0`) roll, then a vision LLM call (the photo as an `image_url` content part + caption,
`MEME_REACT_PROMPT`) that must answer strict JSON `{"action":"react|comment|pass",
"emoji":"...","text":"..."}`:

- **`react`** — sets a message reaction (`Req.SetMessageReaction`) using ONE emoji from the
  same Telegram-allowed set the S6 outcome router's emoji outcome uses
  (`allowedReactionEmoji`). An emoji outside that set is treated as a no-op (Warning-logged),
  never sent to Telegram unchecked.
- **`comment`** — sends a one-liner reply (`BotHelpers.sendTextReply`, logged normally).
  Blank text is a no-op.
- **`pass`** — does nothing.
- **Malformed JSON** (or a failed LLM call, or an unrecognized `action`) — treated the same
  as `pass`, Warning-logged.

### Enabling in prod

Every setting above is a normal `bot_setting` row — flip it live, then `POST
/reload-settings` (or wait for the next natural reload). Start conservative:

```sql
UPDATE bot_setting SET value = 'true' WHERE key = 'DIGEST_ENABLED';
UPDATE bot_setting SET value = '0.02' WHERE key = 'INTERJECT_PROBABILITY';  -- ~1-in-50 eligible burst
UPDATE bot_setting SET value = '0.05' WHERE key = 'MEME_REACT_PROBABILITY'; -- ~1-in-20 photo
```

## `/say`, `/sql`, cost footer (Phase-1 Slice 9, stretch)

Three small, independent stretch features on top of the LLM foundation above: a TTS
voice-reply command, an admin-gated natural-language SQL console, and an optional
per-reply cost footer.

### `/say [voice] <text>`

Synthesizes `text` via `ISpeech.Synthesize` (the same `alita-tts` deployment S1 wired up)
and sends it as a voice note (`Req.SendVoice`, `BotHelpers.sendVoiceReply`). `voice` is
optional — the first whitespace-separated token of the args is read as an explicit voice
selector in two cases: it's followed by more text (`/say nova привет` → voice `nova`, text
"привет"), or it's the ONLY token and the command replies to another message (that
message's text supplies what gets spoken — `/say nova` replying to "привет" → voice
`nova`, text "привет"). A lone token that isn't a recognized voice name is left as plain
text UNLESS it's that reply-only case, where there's no other plausible reading — that's
reported as an invalid-voice refusal (`BotService.parseSayArgs`). No voice arg at all falls
back to `TTS_DEFAULT_VOICE` (bot_setting, default `"alloy"`). Recognized voices
(`BotService.validTtsVoices`): `alloy, ash, ballad, coral, echo, fable, nova, onyx, sage,
shimmer, verse` — the OpenAI/Azure `gpt-4o-mini-tts` roster.

Text is capped at `SAY_MAX_CHARS` (bot_setting, default `500`) — over that, a RU refusal
and no TTS call at all. Azure's `audio/speech` with `response_format=opus` normally
already returns a proper Ogg/Opus container (confirmed against the real `alita-tts`
deployment, see `tests/AlitaBot.RealTests/VoiceRealTests.fs`'s curl-equivalent
verification) — `BotService.isOggContainer` checks the 4-byte `"OggS"` magic and sends
straight through as a voice note in that case. For the rare voice/model combination that
doesn't, `BotService.tryConvertToOggOpus` shells out to `ffmpeg` (if it's on `PATH`) to
re-encode; if `ffmpeg` is missing or the conversion fails, the raw bytes go out as a
regular audio attachment (`Req.SendAudio`) instead of a voice note. The bot's own
`message_log` row is tagged `"[voice] {text}"` — the same convention S1's voice-
transcription reply already uses, so a `/say` reply reads identically to a transcribed
voice message in later LLM context. `llm_usage` records it under `kind='tts'` (the
existing `ISpeech.Synthesize` telemetry path, unchanged).

**Found in the process:** `AzureFoundryProvider.fs`'s `AzureWire.speechUri` had been using
the same shared `api-version=2024-10-21` as chat/embeddings/transcriptions since S1 — which
404s against the real `alita-tts` deployment's `audio/speech` route. Nothing had ever
called `ISpeech.Synthesize` end-to-end against real Azure before `/say` (S1's own
verification bypassed the app entirely with a hand-rolled curl-equivalent call using
`api-version=2024-08-01-preview`); `/say`'s real-test caught the latent bug, fixed by
giving `audio/speech` its own `SpeechApiVersion = "2024-08-01-preview"`.

### `/sql <question>` — admin-gated natural-language SQL

Gated by `ADMIN_USER_IDS` (bot_setting, `JSON_BLOB` array of Telegram user ids, seeded
`[]` — nobody is admin until hand-seeded, see AGENTS.md's "Settings seeds, not
migrations"). A non-admin gets a flat, Алита-styled troll refusal
(`"Куда лезёшь? SQL-консоль не для тебя."`) with **no LLM call at all**.

For an admin: `SQL_PROMPT` (bot_setting) inlines a compact description of Alita's own
schema (`message_log`, `message_embedding`, `interaction_memory`, `person_dossier`,
`llm_usage`, `karma`, `bot_setting`, `scheduled_job`) and asks the model for a single JSON
object `{"sql": "..."}` containing one read-only `SELECT`/`WITH` statement — same
free-text-JSON-with-one-retry contract (`completeJsonWithRetry`) `/awards`/`/quote` use,
not a server-side JSON-mode parameter.

Layered safety, belt and braces:
1. **`AlitaBot.Services.SqlGuard.validate`** (text-level, before the query ever reaches
   Postgres): must start with `SELECT`/`WITH`, at most one trailing semicolon and none
   elsewhere, and none of `INSERT`/`UPDATE`/`DELETE`/`DROP`/`ALTER`/`CREATE`/`GRANT`
   appearing outside a quoted string literal (single-quoted string contents are blanked
   out first, so a legitimate `WHERE text = '...update...'` literal never trips the guard).
2. **`DbService.ExecuteReadOnlySelect`** opens a FRESH connection, runs `SET
   default_transaction_read_only = on`, sets a 5-second command timeout, and wraps the
   validated statement in an outer `SELECT * FROM (...) AS sql_limited LIMIT 50` — so even
   a query that somehow slipped past step 1 could still only `SELECT`, and only up to 50
   rows.

A rejected or failed query shows the generated SQL plus a short RU reason; a successful one
renders as an MDV2 code-block table (`BotService.renderSqlTable`, column values capped at
30 chars each), delivered via `Mdv2Delivery.sendFinal` — same pipeline `/roast`/`/awards`
use, since the SQL text itself needs a fenced code block, not plain-text escaping.

### Cost footer (`COST_FOOTER_ENABLED`)

When on (bot_setting, `FEATURE_FLAG`, default `false`), every LLM **responder** reply
(never a command reply) gets an appended `⛽ $0.0021`-shaped line showing that call's USD
cost, computed the same way `/img`'s caption cost line and `llm_usage.cost_usd` are
(`AlitaBot.Llm.LlmPricing.tryCost` against `LLM_PRICING`, keyed by the `LLM_DEPLOYMENT`
name since the responder layer never sees the raw response `model` field a provider-level
`LlmCall` does). The footer is delivered as **one extra edit after the normal final
send/edit** (`ResponderService.maybeAppendCostFooter`, called from both the streaming-
renderer path and the `REWRITER_ENABLED` non-stream path) — works uniformly regardless of
`STREAM_MODE`. Critically, the footer is applied ONLY to what goes out over the wire: the
text returned to `BotService` for `message_log`/the embedding pipeline is always the
unfootered original, so the model never sees its own cost in later conversational context
(Альфа's trick — users see it, the model's context never does). `RenderResult` (Services/
ReplyRenderer.fs) grew a `Usage: TokenUsage option` field for exactly this — none of the
three renderers change their actual rendering behavior, they just also thread the
completed call's token usage back out.

### Fake-suite testing (`tests/AlitaBot.Tests/StretchTests.fs`)

`FakeAzureOcrApi` gained an `/openai/deployments/{deployment}/audio/speech` route
(`Handlers.handleAudioSpeech`) — unlike the JSON-bodied endpoints elsewhere in that file, a
scripted 200's `body` is BASE64-ENCODED audio bytes, written back as the raw binary
response `AzureFoundrySpeech.Synthesize`'s `sendBinaryWithRetry` expects; the default
(unscripted) fallback is a tiny `"OggS"`-prefixed buffer so `/say` tests exercise the
`sendVoice` fast path without needing `ffmpeg` in the fake-test container. `FakeTgApi`
gained explicit `sendVoice`/`sendAudio` handlers (Funogram's response parser needs a real
`Types.Message`-shaped result, not the dispatcher's generic bare-`true` fallback).

## Gemini provider: Nano Banana images + Lyria music (`/song`)

Second `IImageGen` backend (Google's Generative Language API, `Llm/GeminiProvider.fs`) plus
the first `IMusicGen` implementation, wired to the new `/song` command.

### Model discovery

`GET /v1beta/models` against the real API (with `x-goog-api-key`) returned this key's full
roster. Image-capable ("Nano Banana") tiers, oldest → newest:

| Model | Display name |
|---|---|
| `gemini-2.5-flash-image` | Nano Banana |
| `gemini-3-pro-image[-preview]` | Nano Banana Pro |
| `gemini-3.1-flash-image[-preview]` | Nano Banana 2 |
| `gemini-3.1-flash-lite-image` | Nano Banana 2 Lite |

`GEMINI_IMAGE_MODEL` defaults to `gemini-3.1-flash-image` — the newest non-preview tier.
Music: `lyria-3-pro-preview` (full track) and `lyria-3-clip-preview` (short clip), both
`generateContent`-capable on this key — no separate streaming "Lyria RealTime" model was
listed. `GEMINI_MUSIC_MODEL` defaults to `lyria-3-pro-preview`.

### Wire format

One endpoint for everything — `POST {GEMINI_BASE_URL}/v1beta/models/{model}:generateContent`,
auth via the `x-goog-api-key` header (not a bearer token) — unlike Azure, there's no separate
"edits" route: img2img adds an `inline_data` (base64 PNG) part alongside the text prompt on
the SAME endpoint. Image generation additionally sets `generationConfig.responseModalities =
["IMAGE","TEXT"]`. A successful response's `candidates[0].content.parts[]` carries the
generated bytes as an `inlineData` part (`{mimeType, data}`, base64) — `GeminiWire.
tryParseGenerateContentResponse` collects every such part.

**What was empirically verified vs. best-effort:** every request shape above (text-to-image,
img2img, music) was confirmed **schema-valid** against the real API — a deliberately
malformed body 400s ("Unknown name ... Cannot find field"), while these all reach the quota
gate and 429 ("`RESOURCE_EXHAUSTED`, `free_tier_requests, limit: 0`"). That 429 is a genuine
Google Cloud **billing gate specific to this key's project** — confirmed NOT a transient rate
limit (a malformed request still 400s before quota is even consulted; text-only chat models
on the same key 200 fine). **No real successful image/music response was ever observed**, so
the response-parsing shapes above (camelCase `inlineData`/`mimeType`, `usageMetadata`) are
best-effort from Google's public API docs — exactly the same posture Azure's `gpt-image-1`
wire format has in `AzureFoundryProvider.fs` (unverified request→response round trip, quota
denied). Lyria's audio container format (likely WAV/PCM) is unverified for the same reason —
`/song` re-encodes via `ffmpeg` the same way `/say` does, degrading gracefully (raw bytes
sent as-is) if `ffmpeg` is missing or the real container needs no conversion.

Gemini 429s don't carry a standard `Retry-After` HTTP header (confirmed via `curl -D`), but
DO carry a `details[].retryDelay` field in the JSON body (e.g. `"retryDelay": "30s"`) —
`GeminiWire.retryDelayFromBody` parses that the way `AzureWire.retryAfterOf` reads the header.

### Config

`GEMINI_API_KEY` is a secret env var (like `AZURE_FOUNDRY_KEY` — never a bot_setting).
Everything else is a hot-reloadable bot_setting: `GEMINI_BASE_URL` (default the real Google
endpoint — overridden in the fake-suite fixture to point at `FakeAzureOcrApi`'s additive
`/gemini/*` routes), `GEMINI_IMAGE_MODEL`, `GEMINI_MUSIC_MODEL`, `IMAGE_PROVIDER` (`"azure"`
| `"gemini"`, default `"gemini"`), `SONG_MAX_CHARS` (default `1000`),
`SONG_COOLDOWN_SECONDS` (default `120` — music generation is slow and pricier than
chat/images). `LLM_PRICING` gained per-item entries: `{"gemini-3.1-flash-image":
{"per_image":0.02},"lyria-3-pro":{"per_track":0.06}}` — both **estimates**, since no real
call was ever successfully billed (see above); update once a real invoice confirms actual
cost.

### `/song [(style)] <lyrics or description>`

Optional leading `(style hint)` in parens — same inline-flag convention `/say`'s voice
selector uses — folded into the prompt as `Style: {style}\n\n{lyrics}` when present
(`BotService.parseSongArgs`). Logs `[song-cmd] {args}` first (webhook-redelivery dedup),
then: empty prompt → RU usage hint; over `SONG_MAX_CHARS` → RU refusal; on cooldown
(`SONG_COOLDOWN_SECONDS`, **per-invoking-user** — `song_cooldown` table, mirrors `/roast`'s
`roast_cooldown` but keyed by the caller since `/song` has no target concept) → RU cooldown
reply; otherwise sends a "сочиняю..." placeholder, generates, re-encodes to mp3 via `ffmpeg`
when possible (`BotService.tryConvertToMp3` — falls back to the raw bytes otherwise),
delivers via `Req.SendAudio` with a title (truncated lyrics/description, 60 chars) and
performer "Алита", and logs the bot's reply as `[song] {prompt}`. `llm_usage` records it
under `kind='music'` (added by `V7__gemini_music.sql`'s `CHECK` constraint update).

### Fake-suite testing (`tests/AlitaBot.Tests/GeminiTests.fs`, `SongTests.fs`)

`FakeAzureOcrApi` gained additive `/gemini/v1beta/models/{model}:generateContent` routes
(`Handlers.handleGeminiGenerateContent`) — one handler for both image and music calls
(no separate edits route, matching the real API), routing to a scripted image-vs-music
queue by whether the model name contains `"image"` (true of both the real discovered names
and the test fixture's `gemini-test-image`/`lyria-test-music`). Default (unscripted)
responses are a tiny valid PNG (reusing the existing `tinyPngBase64`) and a tiny valid WAV.
Control endpoints: `/test/mock/gemini-image-script`, `/test/mock/gemini-music-script` — new
`BotContainerBase` methods `SetGeminiImageScript`/`SetGeminiMusicScript`/`GetGeminiCalls`.
The fake-suite fixture's baseline `IMAGE_PROVIDER` stays `"azure"` (`ContainerTestBase.fs`)
so the existing `ImageGenTests.fs` assertions are undisturbed — `GeminiTests.fs` flips it to
`"gemini"` per-test.

## Natural-language tool-calling: `generate_image` + `web_search` (S10 PR1)

The LLM responder can now call tools mid-conversation instead of only reacting to explicit
`/img` etc. commands — "Алита, нарисуй рыжего кота" or "Алита, найди в интернете..." works
without a slash command. Gated by `NL_TOOLS_ENABLED` (default `false` in prod — flip live via
`/reload-settings` after staging validation; dev seed defaults it `true`).

### Architecture

`Services/AgentToolLoop.fs` reuses the existing `IReplyRenderer.Render` UNMODIFIED, once per
round: attach the offered `ChatRequest.Tools`, `chat.CompleteStream`, hand the chunk stream to
the same renderer the non-tool path already uses. A pure tool-call round has empty streamed
text, so the renderer's existing "nothing to send" branch does the right thing (no Telegram
message that round) — this required one small renderer change: the "empty text" Warning is
now guarded to not fire when `FinishReason.ToolCalls` is the (expected) shape. The loop then
executes the requested tools (`Services/ToolExecutor.fs`), appends an `Assistant` message
(with `ToolCalls`) plus one `Tool`-role message per result, and starts the next round — until
either the model stops requesting tools or `NL_TOOLS_MAX_ITERATIONS` (default `4`) is hit, at
which point `Tools` is stripped from the next request to force a clean final text answer.

`Services/MediaActions.fs` is the shared generate+caption+send core for image generation —
used by BOTH the `/img` command AND the `generate_image` NL tool, so a caption fix benefits
both call sites identically. The caption is no longer the (truncated) raw prompt: it's a
cheap non-stream LLM call (`MediaActions.composeCaption`, `MEDIA_CAPTION_PROMPT` bot_setting)
that reacts to having just made the image, explicitly forbidden from describing/repeating the
prompt — `message_log`'s bot row for `/img`/`generate_image` now logs `[image] {caption}`
(previously `[image] {truncated prompt}`).

`Services/ToolRegistry.fs` is pure data — the tool catalog (`generate_image`, `web_search` in
PR1) filtered by `WEB_SEARCH_ENABLED` (per-tool kill switch) and admin status (`AdminOnly`
tools, none shipped yet — PR2's `sql_query` will be the first). `Services/Admin.fs` and
`Services/MessageLog.fs` are prerequisite extractions out of `BotService.fs` so the tool
executor can reuse admin-gating and `message_log` bookkeeping without a dependency on
`BotService` itself.

### `web_search` — Azure Responses API

`Llm/AzureResponsesProvider.fs` is a self-contained sub-call to Azure's Responses API
(`POST {AZURE_RESPONSES_ENDPOINT}/openai/v1/responses`) using its server-managed `web_search`
tool — a DIFFERENT API surface from `AzureFoundryProvider.fs`'s chat completions (different
request/response shape, and even a different base host on the same Azure AI Foundry resource:
`https://szer-foundry.openai.azure.com`, not `AZURE_FOUNDRY_ENDPOINT`'s
`https://szer-foundry.cognitiveservices.azure.com`).

**DISCOVERY** (curl probe against the real API, run before writing the provider — see
`AzureResponsesWire`'s doc comment for the full writeup): the unified endpoint worked on the
first attempt, no `?api-version=` needed; auth is the same `api-key` header the chat-
completions endpoint uses; the `alita-gpt-5-mini` deployment name worked directly as the
`model` field. Response shape: a top-level `output[]` array mixing opaque `reasoning`/
`web_search_call` items with exactly one `type="message"` item whose `content[]` holds
`output_text` parts (`text` + `annotations[]` of `type="url_citation"` with `title`/`url`).
Usage uses the Responses API's own field names (`input_tokens`/`output_tokens`, not chat
completions' `prompt_tokens`/`completion_tokens`). The probed query ran a 3-round internal
search (two `web_search_call`s plus reasoning) before answering — all invisible to the parser,
which only reads the final `message` item.

`WEB_SEARCH_MODEL` (default `""`, meaning unconfigured — fails gracefully like an empty
`IMAGE_DEPLOYMENT`) is the model value sent; dev seed uses the probe-verified
`alita-gpt-5-mini`. No new rate-limit table: `DbService.ToolCallCountSince` queries `llm_usage`
directly (`V8__web_search_usage_kind.sql` added `'web_search'` to its `kind` CHECK).

### Fake-suite testing (`tests/AlitaBot.Tests/NlToolLoopTests.fs`)

`FakeAzureOcrApi` gained a `POST /openai/v1/responses` route
(`Handlers.handleResponsesApi`/`Store.azureResponsesScript`, control endpoint
`/test/mock/responses-script`, `BotContainerBase.SetAzureResponsesScript`/
`GetAzureResponsesCalls`) and its existing chat-completions SSE fake
(`respondChatCompletionSse`) now also rebuilds a scripted `message.tool_calls` array into the
`delta.tool_calls` SSE shape `AzureFoundryChat.CompleteStream`'s parser expects (one complete
fragment per call, not split char-by-char). The fixture's baseline `NL_TOOLS_ENABLED` stays
`false` (mirrors prod's conservative default) so every other `RESPONDER_MODE=llm` test suite
keeps its pre-S10 request/response shape undisturbed — `NlToolLoopTests.fs` flips it on
per-test.

## Empirical draft-semantics findings (M5)

Bot API 10.x's `sendMessageDraft` / `sendRichMessageDraft` are undocumented in our codebase
before this milestone. Probed against real Telegram with `make probe-draft`
(`tests/AlitaBot.RealTests/DraftProbe.fs`): a logged-in MTProto user client (WTelegramClient)
watching raw updates while the Bot API is called directly over HTTP (bypassing Funogram, so raw
`ok`/`error_code`/`description` are visible verbatim).

**What a draft actually is.** `sendMessageDraft` is not a message at all — under the hood it's the
same `sendMessageAction` (typing-indicator) mechanism Telegram already has, carrying the composed
text. The peer's client receives it as `UpdateUserTyping` (private chats) or
`UpdateChatUserTyping` (groups) wrapping a `SendMessageTextDraftAction`/`SendMessageRichMessageDraftAction`,
**not** `UpdateNewMessage`/`UpdateEditMessage`. It never gets a `message_id`, is never returned by
history reads, and is indistinguishable on the wire from a "drafting a message…" typing bubble.

**What we found, per chat type:**

| Chat type | `sendMessageDraft` | What the peer saw | Draft visible in history? |
|---|---|---|---|
| Private chat (DM) | `ok: true`, both calls | Two `UpdateUserTyping` "drafting…" bubbles carrying the live text, then a normal `UpdateNewMessage` when the final `sendMessage` landed | No — `Messages_GetHistory` before/after only ever showed the final real message |
| Basic group | `400 Bad Request: TEXTDRAFT_PEER_INVALID` on the very first call | Nothing (rejected before Telegram even attempted to fan it out); the following plain `sendMessage` worked normally | N/A |
| Supergroup | **Not probed** — the test harness only has a basic-group test chat | — | — |

The `draft_id` we sent is not the same value peers see on the wire (Telegram assigns its own
`random_id` to the underlying `SendMessageTextDraftAction`, stable across our two calls to the
same `draft_id`) — a wire-level detail, not something callers need to track.

`TEXTDRAFT_PEER_INVALID` reads as "not a private chat", which would suggest supergroups fail the
same way as basic groups — but that is an inference from the error shape, not a probed fact.
Treat supergroup support as genuinely unknown.

## Chosen default: `STREAM_MODE=edit`

The bot's primary surface is group chats (`TARGET_CHAT_IDS`), and `sendMessageDraft` is
confirmed broken there (basic groups) and unverified-at-best (supergroups). `edit` is the only
mode proven to work everywhere the bot actually operates, so it stays the default
(`src/alita-bot/dev-bot-settings.sql`, `bot_setting.STREAM_MODE`).

`draft` is still fully implemented and selectable per-deployment (or once supergroup semantics
are confirmed) — it's real streaming for private-chat use cases, not a stub: it accumulates via
throttled `sendMessageDraft` calls and only ever touches `sendMessage` once, at the end. Every
chat it fails in falls back to `edit` automatically and permanently (memoized per `chatId` for
the process's lifetime), so misconfiguring `STREAM_MODE=draft` against a group degrades to
`edit`-equivalent behavior rather than breaking replies — confirmed by running the real-Telegram
smoke suite under both `STREAM_MODE=edit` and `STREAM_MODE=draft` (5/5 green either way; see
`tests/AlitaBot.RealTests/SmokeTests.fs`, `` `streamed reply settles into a final text` ``).

## One-time setup

Everything below is done once per machine/account, before the loop (`make real-test`) or the
containerized smoke check (`make smoke`) will work. All values end up in `~/.alita-test/env`
(`KEY=VALUE`, gitignored) — never in the repo.

1. **ngrok.** You need a paid ngrok plan with a **reserved domain** (a free/ephemeral ngrok URL
   changes on every restart, which breaks `setWebhook`). Ngrok has two *different* credentials —
   easy to conflate:
   - **Authtoken** (`ALITA_NGROK_AUTHTOKEN`) — identifies your ngrok account to the `ngrok` agent
     binary itself (`ngrok config add-authtoken ...` or the `NGROK_AUTHTOKEN` env var). This is
     the one that actually opens the tunnel; `NgrokTunnel.fs` sets it as the child process's
     `NGROK_AUTHTOKEN`.
   - **API key** (`ALITA_NGROK_API_KEY`) — a separate credential for ngrok's *Cloud REST API*
     (`api.ngrok.com`, e.g. managing reserved domains/edges programmatically). The harness
     currently only *captures* this in `RealEnv.NgrokApiKey` — nothing in `tests/AlitaBot.RealTests`
     calls the Cloud API with it yet, so treat it as reserved for future tooling, not something
     `make real-test`/`make smoke` needs today.
   - `ALITA_NGROK_DOMAIN` is the reserved domain itself (e.g. `your-name.ngrok-free.app` or a
     custom domain on a paid plan), no `https://` prefix.
2. **BotFather test bot.** Create a dedicated bot with @BotFather — **do not reuse a production
   bot**. Save the token as `ALITA_TEST_BOT_TOKEN` and its `@username` (no `@`) as
   `ALITA_TEST_BOT_USERNAME`. Then `/setprivacy` → **Disable** for that bot — with privacy mode
   on (the default), group chats only deliver `/commands` and reply-to-bot messages to the
   webhook; mention/name-trigger detection (`BotService.mentionsBot`/`nameTriggerRegex`) needs to
   see *every* group message, which requires privacy mode off.
3. **MTProto test user.** Register an app at <https://my.telegram.org> (any phone number you
   control, ideally not the bot's) to get `ALITA_TG_API_ID` and `ALITA_TG_API_HASH`; set
   `ALITA_TG_API_PHONE` to that number's `+`-prefixed international form. This is a *real user
   account* (via WTelegramClient) that plays the human side of the conversation in
   `tests/AlitaBot.RealTests` — Bot API tokens can't send messages to other users/bots.
4. **`make tg-login`** — one-time *interactive* run (asks for the Telegram login code on stdin,
   and the 2FA password if you have one set) that creates `~/.alita-test/tg.session`. Re-run only
   if the session is deleted/invalidated; every other Makefile target reuses the saved session
   non-interactively (and fails fast with "run `make tg-login` first" if it's missing).
5. **Test group + chat id.** Create a private Telegram group containing the test bot, the test
   user, and you. Get its Bot-API-shaped chat id with `make tg-chats` (lists every dialog the
   test user can see, one line each, id already in Bot API convention) rather than reading it off
   Telegram's UI. **Basic group vs. supergroup matters for the id shape**: a plain "group"
   (not yet converted to a supergroup) has a small negative Bot API id (`-chat_id`); once a group
   is upgraded to a *supergroup* (or created as a channel-backed group), its Bot API id gets a
   `-100` prefix (`-1000000000000 - channel_id`). `TgUserClient.peerKey` strips these to get back
   the underlying MTProto id, and gets it wrong if you paste an id in the wrong shape — always
   get it from `make tg-chats`, not by hand. This repo's test chat is a **basic group**, which
   also means `sendMessageDraft` is unverified for supergroups (see the draft-semantics findings
   below).
6. **MTProto-vs-Bot-API message-id domain warning.** WTelegramClient (`TgUserClient`) and the Bot
   API (what the bot itself sees and what `message_log` stores) number messages in **different,
   independently-incrementing domains** for the same physical chat — empirically observed as a
   small, drifting constant offset between the two for identical messages, not a fixed formula.
   Never compare an MTProto `message.id` (from `TgUserClient`) directly against a Postgres
   `message_log.message_id` — correlate by marker text and `reply_to_message_id` chaining
   instead (see the `SmokeTests.fs` header comment for the full story; this bit the M3 harness
   once already).
7. **Azure AI Foundry** (only needed for `RESPONDER_MODE=llm`): `AZURE_FOUNDRY_ENDPOINT`,
   `AZURE_FOUNDRY_KEY`, `ALITA_LLM_DEPLOYMENT`.
8. **Webhook secret.** `ALITA_WEBHOOK_SECRET` isn't issued by anything — it's any string you make
   up yourself. It becomes both `BOT_AUTH_TOKEN` (what the bot process checks incoming webhook
   calls against, `WebhookHost.validateApiKey`) and the `secret_token` passed to `setWebhook`;
   the harness/Makefile always uses the same value for both ends, so it never needs to be "known"
   by Telegram in advance — just consistent between the two.

## Running the loop

Credentials live in `~/.alita-test/env` (`KEY=VALUE`, gitignored), never in the repo. Required
for the full loop: `ALITA_NGROK_DOMAIN`, `ALITA_NGROK_API_KEY`, `ALITA_NGROK_AUTHTOKEN`,
`ALITA_TEST_BOT_TOKEN`, `ALITA_TEST_BOT_USERNAME`, `ALITA_WEBHOOK_SECRET`, `ALITA_TEST_CHAT_ID`
(core — webhook/db plumbing tests only need these); plus `ALITA_TG_API_ID`, `ALITA_TG_API_HASH`,
`ALITA_TG_API_PHONE` (and a session from `make tg-login`) for the MTProto user-client tests; plus
`AZURE_FOUNDRY_ENDPOINT`, `AZURE_FOUNDRY_KEY`, `ALITA_LLM_DEPLOYMENT` for `RESPONDER_MODE=llm`.
Optional overrides: `RESPONDER_MODE` (default `echo`), `STREAM_MODE` (default `edit`) — both are
upserted into `bot_setting` by `DevDb.applyRealSettingsAsync` on every run, so
`STREAM_MODE=draft make real-test` actually flips the DB setting the spawned bot reads at startup.

Makefile targets (repo root):

| Target | What it does |
|---|---|
| `make tg-login` | One-time interactive MTProto login for the test user account; saves the session file. |
| `make tg-chats` | Lists all dialogs with their Bot API chat ids — use to find `ALITA_TEST_CHAT_ID`. |
| `make alita-db` | Brings up local Postgres + Flyway + `bot_setting` seed (`src/alita-bot/docker-compose.dev.yml`). Never torn down between runs — idempotent. |
| `make alita-build` | `dotnet build src/AlitaBot -c Release`. |
| `make selfcheck` | Plumbing-only check (no MTProto): db → build → ngrok tunnel → bot process → webhook → public `/healthz` → teardown. |
| `make probe-draft` | The M5 empirical probe — standalone, no tunnel/webhook/bot process needed; calls the Bot API directly and logs everything the MTProto user client observes. |
| `make real-test` | **The agent loop.** `alita-db` + `alita-build` + `dotnet test tests/AlitaBot.RealTests -c Release` — full real-Telegram smoke suite (`SmokeTests.fs`). |
| `make alita-logs` | Tails `bot.log`/`ngrok.log` from the last `real-test`/`selfcheck` run. |
| `make alita-clean` | `docker compose down -v` + `deleteWebhook?drop_pending_updates=true`. Run when done for the session. |

`make real-test` spins up: dev DB → ngrok tunnel (`NgrokTunnel.fs`) → `AlitaBot.dll` as a child
process on `127.0.0.1:5010` (`BotProcess.fs`) → `setWebhook` pointed at the tunnel
(`Webhook.fs`) → (if credentials + session exist) a logged-in MTProto user client
(`TgUserClient.fs`) that plays the human side of the chat. Every real-test run tears down webhook,
tunnel, and bot process afterward; the dev DB is left running for the next run. Container CLI is
auto-detected (`podman compose` here since `docker` is a shell alias, invisible to `make`;
override with `DOCKER=docker make ...`).

## Containerized smoke test (`make smoke`)

`make real-test` hosts the bot as a bare `dotnet AlitaBot.dll` child process (`BotProcess.fs`) —
fastest iteration, direct stdout capture, per plan decision D9. `make smoke` is the *parity*
check for that decision: it runs the same bot, same DB schema/seed, but **containerized** —
built from `src/Dockerfile.bot` (`BOT_PROJECT=AlitaBot`) exactly as CI/prod would build it —
to catch anything the bare-process path could hide (missing runtime deps, `ASPNETCORE_URLS`
binding, container networking, SELinux-labeled bind mounts).

```
cp src/alita-bot/.env.local.example src/alita-bot/.env.local   # fill in BOT_TELEGRAM_TOKEN etc.
make smoke                                                      # podman compose --profile smoke up -d --build
curl http://127.0.0.1:5010/healthz                              # -> OK
make alita-clean                                                # tear down + deleteWebhook
```

What `--profile smoke` adds on top of the default `postgres` + `flyway` + `seed-bot-settings`
stack: a `bot` service built from `src/Dockerfile.bot`, on the compose network (so it reaches
Postgres at hostname `postgres:5432`, not `localhost:15433`), with `DATABASE_URL` and
`ASPNETCORE_URLS` force-set by compose (do **not** put those two in `.env.local` — compose's
`environment:` block overrides `env_file:` for exactly this reason) and everything else
(`BOT_TELEGRAM_TOKEN`, `BOT_AUTH_TOKEN`, `AZURE_FOUNDRY_KEY`, `TARGET_CHAT_IDS`, `BOT_USERNAME`)
sourced from `./.env.local` (gitignored; `git check-ignore -v src/alita-bot/.env.local` to
confirm before ever running `git add`). Bind mounts (`init.sql`, `migrations/`,
`dev-bot-settings.sql`) use the `:z` SELinux relabel flag, required for rootless podman on
enforcing hosts (Fedora) and harmless under Docker.

`.env.local` values are the same secrets as `~/.alita-test/env`, just renamed/reshaped for the
bot's own env-var names:

| `~/.alita-test/env` | `src/alita-bot/.env.local` |
|---|---|
| `ALITA_TEST_BOT_TOKEN` | `BOT_TELEGRAM_TOKEN` |
| `ALITA_WEBHOOK_SECRET` | `BOT_AUTH_TOKEN` |
| `AZURE_FOUNDRY_KEY` | `AZURE_FOUNDRY_KEY` |
| `ALITA_TEST_CHAT_ID` | `TARGET_CHAT_IDS` (see gotcha below) |
| `ALITA_TEST_BOT_USERNAME` | `BOT_USERNAME` (see gotcha below) |

**Gotcha: `bot_setting` DB rows win over `.env.local`, even empty ones.** `TargetChatIds` and
`BotUsername` are read via `getSettingOr "KEY" (getEnvOr "KEY" "")` (`Program.fs`) — the
`bot_setting` row wins whenever it *exists*, regardless of its value, and `.env.local`/env-var
values are only a fallback for when the row is missing entirely. `dev-bot-settings.sql` seeds
`TARGET_CHAT_IDS` as `''` (empty, on purpose — "fill in your test group id") with
`ON CONFLICT DO NOTHING`, so out of the box the container ignores whatever `TARGET_CHAT_IDS` you
put in `.env.local` and answers no chat at all. To point the containerized bot at a real (or
synthetic) chat id, update the DB row directly and reload:

```
podman exec -e PGPASSWORD=admin alita-bot-postgres-dev \
  psql -h 127.0.0.1 -U admin -d alita_bot -c \
  "UPDATE bot_setting SET value = '<chat id>' WHERE key = 'TARGET_CHAT_IDS';"
curl -X POST http://127.0.0.1:5010/reload-settings \
  -H "X-Telegram-Bot-Api-Secret-Token: <BOT_AUTH_TOKEN>"
```

(Consistent with the "settings are seeded by hand, not by migration" convention — see AGENTS.md.)

**Verified parity evidence (M6):** with the above DB update, `podman compose --profile smoke up
-d --build` produced a healthy `alita-bot-dev` container reachable at `/healthz` (`200 OK`) on
the mapped port; a synthetic webhook POST to `/bot` (correct `X-Telegram-Bot-Api-Secret-Token`)
returned `200`, and the incoming message showed up in `message_log` seconds later, queried via
`podman exec ... psql` against the compose-network Postgres — confirming the containerized bot
parses webhooks, reaches Postgres over the compose network by service name, and (from the
follow-on `TelegramApiException: 400 chat not found` in `podman logs alita-bot-dev`, expected
since the synthetic chat id doesn't exist) reaches the real Telegram API from inside the
container too. A full real-Telegram conversation against the *containerized* bot was not run —
`make real-test`'s bare-process path already covers that; this check exists to prove the
container itself is wired correctly, not to duplicate the conversational test suite.

## CI real-test flow (M7): `ALITA_REAL_MODE=remote`

`make real-test`/`make smoke` (above) are the local loops — bare-process or containerized,
always against a tunnel and a dev-machine Postgres. `.github/workflows/alita-real-test.yml` runs
the *same* `tests/AlitaBot.RealTests` suite in CI, but against a real deployment: it builds and
pushes `ghcr.io/szer/alita-bot:test-<sha>`, `kubectl apply`s Postgres + the bot into the
`alita-test` AKS namespace (persistent namespace/cert/gateway-listener/RBAC from `my-infra` PR
\#83/\#84; the bot/Postgres/Service/HTTPRoute themselves are transient — applied and deleted by
this workflow every run, **not** GitOps), waits for `https://alita-test.szer.dev/healthz`, then
runs `dotnet test tests/AlitaBot.RealTests` with `ALITA_REAL_MODE=remote`.

`RealAssemblyFixture` branches on `env.IsRemote` (`RealEnv.fs`): in remote mode it skips
`DevDb.upAsync()` (the local compose stack), `NgrokTunnel`, and `BotProcess` entirely — Postgres
is already reachable at `localhost:15433` via a `kubectl port-forward` the *workflow* starts
(not the fixture), and the bot is already running in the cluster. What it still does, unchanged:
`DevDb.applyRealSettingsAsync` (same upsert, same connection string, works identically against
the port-forward), a `/healthz` poll (against `ALITA_WEBHOOK_PUBLIC_URL`'s host instead of
`127.0.0.1:5010`), and `setWebhook`/`deleteWebhook` (URL is `ALITA_WEBHOOK_PUBLIC_URL` verbatim
instead of `https://{ngrok domain}/bot`). The local path (`ALITA_REAL_MODE` unset or `local`) is
byte-identical to before this milestone — every new branch is `if env.IsRemote then ... else
<the exact previous code>`.

**Env contract (remote mode only):** `ALITA_REAL_MODE=remote`, `ALITA_WEBHOOK_PUBLIC_URL` (e.g.
`https://alita-test.szer.dev/bot`) replaces `ALITA_NGROK_DOMAIN` as the webhook-URL source;
everything else (`ALITA_TEST_BOT_TOKEN`, `ALITA_TEST_BOT_USERNAME`, `ALITA_WEBHOOK_SECRET`,
`ALITA_TEST_CHAT_ID`, `ALITA_TG_API_*`, `AZURE_FOUNDRY_*`, `ALITA_*_DEPLOYMENT`) is the same
variable name the local flow already reads — CI just sources the *values* from GH secrets scoped
to a dedicated CI bot/chat/session (`ALITA_CI_BOT_TOKEN`/`ALITA_CI_BOT_USERNAME`/
`ALITA_CI_CHAT_ID`/`ALITA_CI_TG_SESSION_B64`, gzip-then-base64-encoded), never the dev-machine's
`ALITA_TEST_*`/`ALITA_TG_SESSION_B64` — so a developer's `make real-test` and a CI run can never
fight over `setWebhook` (one bot account = one active webhook) or the MTProto session file.

**Singleton queueing contract.** The workflow's `concurrency` block is
`group: alita-aks-real-test, cancel-in-progress: false` — the `alita-test` namespace and its
transient workload are a single shared resource, so a second PR's run queues behind the first
rather than racing it for the same Postgres/bot/HTTPRoute. Expect PRs to serialize through this
gate; it is not parallelized per-PR.

**No-ArgoCD-labels rule.** Everything the workflow applies under `.github/k8s/alita-test/` is
labeled `alita-ci: transient` and **never** `app.kubernetes.io/instance` — that's the label
ArgoCD's own `alita-test` Application (persistent namespace/cert/RBAC, GitOps-managed) uses for
pruning, and if the transient bot/Postgres carried it, ArgoCD's `selfHeal`/`prune` could delete
them mid-run. Teardown (`always()` in the workflow) deletes exactly `-l alita-ci=transient`,
never anything ArgoCD-tracked.

**Node capacity is tight — the cluster is a single ARM node.** Measured via `kubectl describe
node` on 2026-07-21: `Allocatable` 1900m CPU / 12039300Ki (~11.5Gi) memory; steady-state
workloads (everything outside `alita-test`) already request ~1472m CPU / ~7212Mi memory, leaving
**~428m CPU / ~4.4Gi memory free**. `postgres`/`bot` request CPU at 20m each (matching what the
production bots actually use on this node, not a guess) and modest memory (256Mi/192Mi) —
40m/448Mi total, a ~90% CPU margin and ~90% memory margin against measured free capacity; 500m
CPU / 512Mi memory limits are just safety caps (limits don't count against scheduling quota, only
requests do). Flyway/the `bot_setting` seed run as `docker run --network host` **on the GitHub
Actions runner itself**, not as in-cluster Jobs, so they consume zero AKS node capacity (the
`jobs` RBAC permission is provisioned for future use, unused today). Both Deployments also carry
a `kubectl wait --for=condition=Ready pod -l alita-ci=transient --timeout=180s` fast-fail right
after apply (dumps `kubectl describe pod`/`kubectl get events` on timeout) so an unschedulable
pod fails the run in under 3 minutes instead of riding out the 30-minute job timeout and blocking
the singleton queue behind it. Teardown (`-l alita-ci=transient`, `always()`) runs regardless of
where a run failed, so a half-applied stack — including a stuck `Pending` pod that would
otherwise permanently eat into this already-thin quota — never survives past one run.

## Further docs

- [`docs/TESTING.md`](docs/TESTING.md) — fake vs. real test suites, `make` targets, required env vars.
- [`docs/OBSERVABILITY.md`](docs/OBSERVABILITY.md) — spans, metrics, logging conventions specific to AlitaBot.
- [`docs/TECH-DEBT.md`](docs/TECH-DEBT.md) — known gaps and deferrals.
