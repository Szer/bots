# Observability

AlitaBot follows the same OpenTelemetry + Serilog conventions as VahterBanBot and
CouponHubBot (`BotInfra.Observability`, `BotInfra.JsonLogging`) — see AGENTS.md and those
bots' own `docs/OBSERVABILITY.md` for the shared infra (Loki/Prometheus/Tempo access, VPN).
This file only documents what's specific to AlitaBot.

## Tracing

- ActivitySource: `AlitaBot` (`Telemetry.botActivity`, `src/AlitaBot/Telemetry.fs`).
- Top-level span: **`postUpdate`**, started in `Program.fs`'s webhook handler around every
  `BotService.OnUpdate` call — mirrors VahterBanBot's `postUpdate`/`onUpdate` pattern.
  Tagged via `Telemetry.setUpdateIdentityTags` with `updateType`, `fromUserId`,
  `fromUsername`, `chatId`, `chatUsername`, `messageId` (whichever apply to the update
  variant), plus `update-error` (bool) and an OK/Error `ActivityStatusCode` set once
  `OnUpdate` returns or throws.
- Per-flow child spans (parented via `Activity.Current`, same trace): `handleMessage`
  (text/photo), `handleVoiceMessage`, `handleImageCommand` — tagged `chatId`/`fromId` and an
  `outcome` string (see Metrics below for the full outcome vocabulary).
- LLM call spans: `llm.chat` (non-stream and stream), `llm.stt`, `llm.tts`,
  `llm.embeddings`, `llm.image` (`src/AlitaBot/Llm/LlmTelemetry.fs`) — tagged
  `gen_ai.system`, `gen_ai.request.model`, `gen_ai.usage.{input,output}_tokens`,
  `llm.cost_usd`, `llm.retries`, `error.type` on failure.

## Logging

- Serilog, structured JSON, same sink configuration as the other bots
  (`BotInfra.Observability.configureSerilog`).
- The raw Telegram update JSON is logged **exactly once per update**, at Information, in
  `Program.fs`'s webhook handler, before any fallible processing — via
  `JsonLogging.withRawJsonProperty "RawUpdate"`, which attaches it as a real nested JSON
  property (not an escaped string blob) rather than a plain message. Downstream error logs
  (in `BotService`/`ResponderService`/the LLM provider) never re-log the payload — they join
  the same trace via the ambient `TraceId`, so the full update is one Loki query away
  (`TraceId="..."`) without repeating it on every line.
- LLM/STT/TTS/image-gen errors follow the house Warning/Error split
  (`AzureWire.logLlmError`, `src/AlitaBot/Llm/AzureFoundryProvider.fs`): rate limits
  (`RateLimited`) and content-filter rejections (`ContentFiltered`) are expected operational
  noise → Warning; unexpected API/network errors → Error.

## Metrics

Meter: `AlitaBot.Metrics` (`src/AlitaBot/Telemetry.fs`, `src/AlitaBot/Llm/LlmTelemetry.fs`).

| Metric | Type | Tags | Description |
|---|---|---|---|
| `alitabot_messages_total` | Counter | `outcome` | Every processed message. `outcome` ∈ `logged`, `replied`, `ignored`, `duplicate_update`, `voice_duplicate_update`, `voice_disabled`, `voice_no_filepath`, `voice_transcribe_failed`, `voice_empty_transcript`, `voice_transcribed`, `voice_transcribed_and_triggered`, `image_empty_prompt`, `image_disabled`, `image_generated`, `image_edited`, `image_failed`. `duplicate_update`/`voice_duplicate_update` count webhook redeliveries of an update already fully handled (see message_log idempotency below) — should stay near zero; a sustained nonzero rate means Telegram is retrying, usually because replies are taking too long. |
| `alitabot_command_total` | Counter | `command` | Explicit command invocations (`img` today). Mirrors CouponHubBot's `commandTotal`; mention/name/reply-to-bot triggers are NOT commands and stay out of this counter. |
| `alitabot_voice_transcribe_total` | Counter | `outcome` | Voice/video-note/audio messages, tagged `disabled`, `no_filepath`, `failed`, `empty_transcript`, `transcribed`. |
| `alitabot_voice_transcribe_duration_ms` | Histogram | — | Telegram file download + STT call wall-clock time; only recorded when transcription was actually attempted. |
| `alitabot_llm_tokens_total` | Counter | `direction` (`input`\|`output`) | Token throughput across all LLM call types (chat, embeddings, STT, TTS, image). |
| `alitabot_llm_cost_usd_total` | Counter | — | Estimated USD cost, from the `LLM_PRICING` bot_setting (per-token for chat/embeddings, per-image-per-quality for image gen). Silently 0 for models/deployments with no matching pricing entry (one-time Warning logged instead). |
| `alitabot_llm_latency_ms` | Histogram | — | Latency of every LLM/STT/TTS/image-gen call (span-scoped: for streamed chat, first byte to `[DONE]`). Not broken out by call type via a tag — use the span name (`llm.chat`/`llm.stt`/etc.) in Tempo to filter by operation. |

### Useful PromQL queries

- Reply/log/ignore mix (24h): `sum by (outcome)(increase(alitabot_messages_total[24h]))`
- Duplicate-update rate (should be ~0): `sum(rate(alitabot_messages_total{outcome=~".*duplicate.*"}[1h]))`
- `/img` usage: `sum(increase(alitabot_command_total{command="img"}[24h]))`
- LLM spend (24h): `sum(increase(alitabot_llm_cost_usd_total[24h]))`
- Voice transcription p95 latency: `histogram_quantile(0.95, sum(rate(alitabot_voice_transcribe_duration_ms_bucket[1h])) by (le))`

## Webhook idempotency and per-chat serialization

Telegram redelivers a webhook update it didn't get a timely response for — a real risk here
since a streamed LLM reply, voice transcription, or image generation can run long enough to
approach that window (this bit VahterBanBot's reaction-triage path once; see
`llmReactionTriage`'s fail-fast-on-429 comment in `VahterBanBot/LlmTriage.fs`). Two
mitigations, both in `BotService.fs`:

- **`message_log`'s `UNIQUE(chat_id, message_id)`** is the idempotency source of truth:
  every handler's `DbService.LogMessage` call now returns whether it actually inserted a row.
  A `false` (already logged) means this exact update was already handled — the handler
  short-circuits instead of calling the LLM/responder again and sending a second reply.
  Voice messages are the one case where this can't prevent redundant work entirely: the STT
  call has to run before the transcript (and therefore the log text) is known, so a retry
  repeats the Azure STT call — but still never sends a second reply.
- **A per-chat `SemaphoreSlim`** (`withChatLock`) serializes all message handling within one
  chat, so a burst of rapid triggers — or a retry racing the original attempt — can't run two
  overlapping LLM streams reading the same `message_log` snapshot and posting interleaved
  replies. Different chats still process fully in parallel. The lock dictionary is keyed by
  `chatId` and bounded by `TARGET_CHAT_IDS`.

What this does **not** do: make the webhook return before the reply is fully sent. AlitaBot
still processes each update inline (same as VahterBanBot/CouponHubBot), so a genuinely slow
LLM/image call still holds the connection open for its full duration; the two mitigations
above stop that from producing a *second* reply, they don't shorten the first one. See
`docs/TECH-DEBT.md` for the deferred fully-async option and why it wasn't done in this pass.
