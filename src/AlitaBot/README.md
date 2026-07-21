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

## Image generation (`/img`, `!img`, Phase-1 Slice 3)

`/img <prompt>`, `!img <prompt>`, or `/img@{BOT_USERNAME} <prompt>` in a target chat generates
an image via Azure's images/generations endpoint and replies with it as a photo; the same
command sent **as a reply to a photo message** switches to images/edits (img2img — the replied
photo is downloaded and passed as the source image). Command messages are handled entirely by
`BotService.tryParseCommand`/`handleImageCommand` and never reach `ResponderService` — they
don't trigger the LLM responder even if the prompt also happens to contain the bot's name.

Behavior: logs the command as `[img-cmd] {prompt}`, sends a "рисую..." placeholder, generates,
deletes the placeholder and sends the photo captioned with the prompt (truncated 100 chars) plus
cost (`$0.04`) when `LLM_PRICING` has a matching `per_image_<quality>` entry for `IMAGE_DEPLOYMENT`
— logs the bot's reply as `[image] {truncated prompt}`. Empty prompt → RU usage hint, no Azure
call. `IMAGE_GEN_ENABLED=false` (bot_setting, default `true`) → RU "выключено" reply, no Azure
call. Any Azure failure (including an unconfigured/404 deployment) edits the placeholder into a
RU apology instead of crashing or leaving the chat hanging.

`IMAGE_SIZE` (default `1024x1024`) and `IMAGE_QUALITY` (default `medium`) are hot-reloadable
bot_settings passed straight through to the images API. **`IMAGE_DEPLOYMENT` is empty by
default** — every `gpt-image-*` model variant (and `dall-e-3`) had 0 quota in this
subscription/region at S3 deploy time, so no real deployment exists yet; see
[`docs/TECH-DEBT.md`](docs/TECH-DEBT.md) for the full story and what to do once quota is
granted. `tests/AlitaBot.RealTests/ImageGenRealTests.fs` self-skips until `ALITA_IMAGE_DEPLOYMENT`
is set; the fake-suite tests (`tests/AlitaBot.Tests/ImageGenTests.fs`) exercise the full
command/plumbing behavior against `FakeAzureOcrApi`'s images/generations + images/edits routes
in the meantime.

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
