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
auto-detected (`podman compose` here; override with `ALITA_CONTAINER_CLI`).
