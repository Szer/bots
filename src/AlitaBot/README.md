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

## Tech debt

See [`docs/TECH-DEBT.md`](docs/TECH-DEBT.md).
