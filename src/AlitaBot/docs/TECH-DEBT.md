# AlitaBot — tech debt (Phase 0)

Known gaps and deferrals as of Phase 0 / M6. Nothing here blocks Phase 0 exit; each item names
what phase or trigger should pick it up. Infrastructure-side items (model deployments,
log-pipeline config, k8s manifests) are tracked in the private infra repo — this file only
records that they exist and when they bite.

## No real image-gen deployment yet (S3) — quota denied for every candidate model

Slice 3 (`/img` image generation) ships fully implemented and flag-gated (`IMAGE_GEN_ENABLED`,
default true), but with `IMAGE_DEPLOYMENT` seeded empty — there is no working Azure deployment
to point it at. At deploy time, `az cognitiveservices usage list --location swedencentral`
(run against the subscription that actually owns the `szer-foundry` account, not the CLI's
default subscription — the two differ and querying the wrong one shows non-zero quota that
doesn't apply) showed **0 quota** for every `gpt-image-*` variant (`gpt-image-1`,
`gpt-image-1-mini`, `gpt-image-1.5`, `gpt-image-2`) and for `Dalle` in this
subscription/region; `dall-e-3` itself isn't even offered as a deployable model there at all.
A `terraform apply` for `alita_image` (azapi_resource, gpt-image-1, GlobalStandard, capacity 3)
failed with `InsufficientQuota` before creating anything, so nothing was left half-applied —
the terraform change was reverted rather than merged.

With `IMAGE_DEPLOYMENT` empty, `/img` fails gracefully: `AzureFoundryImageGen` gets a 404 from
Azure (unknown deployment name), surfaces it as `Result.Error`, and `BotService` edits the
"рисую..." placeholder into a RU apology instead of crashing. `tests/AlitaBot.Tests/ImageGenTests.fs`
covers the command/plumbing behavior against the fake suite; `tests/AlitaBot.RealTests/ImageGenRealTests.fs`
self-skips (`Assert.Skip`) until `ALITA_IMAGE_DEPLOYMENT` is set.

**Action:** once quota is granted (support ticket / quota-increase request — out of scope for
the pre-authorized additive terraform workflow), re-add the `alita_image` resource to
`terraform/alita-foundry.tf` in the infra repo (chained via `depends_on` after `alita_tts`,
same serial-apply convention as the other `alita_*` deployments), apply, verify with a real
curl generation, and fill in `IMAGE_DEPLOYMENT` (bot_setting) + `ALITA_IMAGE_DEPLOYMENT`
(`~/.alita-test/env`). Also worth re-checking then: the images/generations and images/edits
wire format in `AlitaBot/Llm/AzureFoundryProvider.fs` (`AzureWire.ImagesApiVersion`,
`buildImageGenBody`, `newImageEditRequest`, `tryParseImageResponse`) was written from Azure's
documented images API, not verified against a real response — the fake suite controls its own
response shape so it can't catch a wire-format mismatch the way the STT/TTS/vision real tests
caught issues in earlier slices.

**Update (Gemini provider slice):** `/img` now defaults to Gemini (`IMAGE_PROVIDER=gemini`,
`Llm/GeminiProvider.fs`'s `GeminiImageGen`) instead of Azure, since Azure's quota above is
still 0 — this unblocks real end-to-end `/img` testing without waiting on the Azure quota
ticket. `AzureFoundryImageGen` is untouched and still wired (`ImageGenRouter` dispatches to
either by the `IMAGE_PROVIDER` bot_setting) — flip it back once Azure quota lands. See the
new entry below: Gemini has its OWN billing-gate quota problem, so this isn't a full fix,
just an alternative path that happens to work for THIS key's chat/text models.

## Gemini image/music generation is billing-gated for the discovery-time API key

Discovered while building the Gemini provider slice (`Llm/GeminiProvider.fs`): the
`ALITA_GEMINI_API_KEY` used for development/testing works fine for TEXT `generateContent`
calls (chat models), but every image- or music-capable model (`gemini-*-image`,
`lyria-3-*`) 429s with `RESOURCE_EXHAUSTED`, `free_tier_requests, limit: 0` — a genuine
Google Cloud billing gate specific to this key's project (no billing account attached),
confirmed NOT a transient rate limit: a deliberately malformed request body still 400s
before quota is even consulted, and text models on the SAME key 200 fine. No real
successful image/music generation was ever observed against this key — the request wire
shapes are empirically confirmed schema-valid (see the README's "Gemini provider" section),
but the response-parsing shapes are best-effort from Google's public docs, same posture as
the Azure `gpt-image-1` entry above.

`tests/AlitaBot.RealTests/GeminiProbe.fs` probes this SAME failure mode directly (one real
HTTP call) before `ImageGenRealTests`'/`SongRealTests`' real-Telegram assertions run, and
self-skips with a clear diagnostic when it's hit, rather than hard-failing `make real-test`
forever on an external blocker outside this PR's control.

**Action:** once billing is enabled on the Google Cloud project behind `ALITA_GEMINI_API_KEY`
(or a new key on a billing-enabled project is provisioned), re-run `RESPONDER_MODE=llm make
real-test` — `ImageGenRealTests`/`SongRealTests` will then exercise the full real round trip
instead of skipping, and any response-shape mismatch (the best-effort parsing above) will
surface as a real, actionable test failure. Also worth updating the `LLM_PRICING` estimates
in `dev-bot-settings.sql` (`gemini-3.1-flash-image`/`lyria-3-pro`) once a real invoice shows
actual per-item cost.

## Orphan model deployments from the first Alita attempt

The abandoned first attempt left two unused model deployments provisioned on another bot's
account. Removal is a destructive terraform change in the infra repo and requires explicit
approval; do it as its own plan-reviewed PR there, not folded into an unrelated change.

## Log pipeline not yet aware of Alita

The centralized log pipeline attaches trace/span structured metadata only for the existing
bots' namespaces. Harmless today (Phase 0 runs entirely outside the cluster), but when AlitaBot
gets deployed its namespace must be added in the infra repo, or its logs will land without
trace correlation. Same pass should add an Alita dashboard row alongside the other bots.

## k8s manifests + deploy workflow deferred to AKS phase

Per plan decision D11, Phase 0 ships `alita-build.yml` (PR builds against
`tests/AlitaBot.Tests`) only. There is intentionally no cluster manifest set, no GitOps app,
no gateway listener, and no `alita-deploy.yml` — nothing to deploy yet; the bot only runs
locally (`make real-test` / `make smoke`) and against real Telegram via ngrok. Deployment
secrets are likewise not provisioned yet.

**Action:** at AKS phase, clone the coupon bot's manifest + deploy-workflow wiring, provision
the deployment secrets, and pick up the log-pipeline item above in the same pass.

## `ScheduledJobs.fs` cherry-pick — done (Slice 5b), but living outside `BotInfra`

Plan decision D12 deferred cherry-picking the old `feature/alita-bot` branch's
`src/BotInfra/ScheduledJobs.fs` (distributed job-lease locking) until a proactive/scheduled
feature actually needed it. Slice 5b (per-person dossiers, nightly fact extraction) is that
feature — `src/AlitaBot/Services/ScheduledJobs.fs` now has the same `UPDATE ... RETURNING`
lease-acquire pattern plus a `SchedulerHostedService` (BackgroundService) driving the nightly
02:00 UTC dossier job, and a TEST_MODE-only `POST /test/run-job?name=` endpoint to trigger it
immediately in tests.

**Deliberately NOT promoted to `BotInfra`.** The old design lived in `BotInfra` (shared by
every bot); this one lives inside `AlitaBot.Services` instead — a `BotInfra` change rebuilds
and triggers a prod redeploy of VahterBanBot and CouponHubBot too, and nothing outside
AlitaBot needs a scheduler yet. If/when a second bot needs the same lease-locking primitive,
promoting `ScheduledJobs.fs`'s `tryAcquire`/`complete` functions to `BotInfra` is a deliberate,
reviewed, **daytime** PR of its own (per the guardrail that governs any `BotInfra` change) —
not something to fold into a feature PR for one bot.

## Supergroup draft-mode probing pending

`src/AlitaBot/README.md` ("Empirical draft-semantics findings") documents that
`sendMessageDraft` was probed against a private DM (works) and the basic-group test chat
(`400 TEXTDRAFT_PEER_INVALID`, rejected outright) — but never against a **supergroup**, because
the only test chat available is a basic group. Whether `STREAM_MODE=draft` would actually work
in a supergroup is genuinely unknown, not just untested-by-inference from the basic-group
error shape.

**Action:** once a supergroup test chat is available (either convert the existing test group —
irreversible on Telegram — or stand up a second one), re-run `make probe-draft` against it and
update the README's per-chat-type table with a real result instead of "Not probed".

## Webhook still processes slow LLM/voice/image work inline (deferred: fully-async reply)

A production-readiness pass (2026-07) found that `BotService.OnUpdate` — like
VahterBanBot's and CouponHubBot's — is awaited to completion before the webhook responds
`200 OK`. For those two bots that's fine (spam triage and coupon operations are fast); for
AlitaBot a streamed LLM reply, voice transcription, or (especially) image generation can
run long enough to approach Telegram's webhook retry window, which VahterBanBot's reaction-
triage path already hit once in production (see the `llmReactionTriage` fail-fast-on-429
comment in `VahterBanBot/LlmTriage.fs` and the `reaction-triage-alert-storm` incident). A
retried webhook would previously re-run the whole handler and send a **second**, distinct
Telegram reply.

**What shipped in this pass:** `message_log`'s `UNIQUE(chat_id, message_id)` is now used as
an explicit idempotency gate (`DbService.LogMessage` returns whether it inserted a row; a
`false` short-circuits the handler before calling the LLM/responder again), plus a per-chat
`SemaphoreSlim` (`BotService.withChatLock`) so a retry can't race the original attempt and
produce overlapping replies either. Together these make a retried webhook a no-op instead of
a duplicate reply. See `docs/OBSERVABILITY.md`'s "Webhook idempotency and per-chat
serialization" section.

**What's deferred:** actually returning the webhook `200` *before* the slow work runs (e.g.
acknowledge + `fireAndForget` the LLM/voice/image generation, matching
`BotInfra.Utils.fireAndForget`'s existing use for VahterBanBot's slow side-effect paths).
Not done in this pass because it's a real behavior change, not a bug fix: every fake-suite
test (`tests/AlitaBot.Tests/*.fs`, 19 tests) currently asserts on `FakeTgApi` calls and
`message_log` rows immediately after `fixture.SendUpdate` returns, which relies on the
webhook response only landing after processing finishes — moving to fire-and-forget would
need those assertions rewritten to poll (as `tests/AlitaBot.RealTests` already does for real-
Telegram round trips), plus a decision on whether/how a still-in-flight reply is observable
(no message_id yet) if a *second* real update arrives for the same chat before the first
reply completes. That's a scoped follow-up, not a mid-pass redesign.

**Action:** when this becomes a real problem (observed `duplicate_update`/
`voice_duplicate_update` in `alitabot_messages_total`, or a support report of a doubled
reply), rework the webhook handler to ack fast and move `BotService.OnUpdate`'s body behind
`fireAndForget`, and update the fake-suite fixtures to poll instead of asserting immediately.

## `interaction_memory.valid_to` is never set (Slice 5b) — dedup skips, never supersedes

The V4 migration gives `interaction_memory` a `valid_from`/`valid_to` pair (plus a partial
`WHERE valid_to IS NULL` index) so a fact could later be *superseded* — e.g. "moved to Berlin"
replacing an earlier "lives in Moscow" — without losing history. Nothing sets `valid_to` yet:
`DossierService.ProcessUser`'s dedup check (`NearestActiveFactSimilarity` against a 0.90 cosine
floor) only ever **skips** inserting a near-duplicate candidate fact; it never detects or marks
a genuinely *contradicting* fact as superseding an old one. Two facts that are similar-but-not-
near-duplicate (e.g. 0.75 cosine — related but not the same fact) both end up as separate
active rows forever, even if the newer one quietly contradicts the older one.

**Action:** if/when contradiction-detection is worth building, the schema is already ready for
it (the column, the index, and the "ACTIVE = `valid_to IS NULL`" convention every query already
uses) — no migration needed, just teach `ProcessUser` to `UPDATE ... SET valid_to = now()` on
the old row when a new fact is judged to supersede rather than duplicate it.

## `trySendEphemeralOrReply` fallback memo is process-lifetime

Same shape as the `DraftRenderer` entry below: `BotHelpers.trySendEphemeralOrReply`
(`/summary`'s ephemeral send, Phase-1 Slice 4) remembers per-`chatId`, in an in-process
`ConcurrentDictionary`, that an ephemeral (`receiver_user_id`) `sendMessage` already failed for
that chat — not in `bot_setting`/the database. A pod/process restart forgets every chat's
fallback state and re-probes the ephemeral send from scratch on the next `/summary` in that chat.

Deliberate simplification, not a bug — same reasoning as `DraftRenderer`: worst case is one
extra failed `sendMessage` call per chat per restart, immediately caught and degraded to a
normal reply, not a user-visible failure. No action needed unless restart frequency ever gets
high enough for the repeated probe calls to matter (unlikely).

## Ephemeral `sendMessage` delivery to the receiver is unconfirmed, even as admin

Re-probed with the test bot promoted to group admin (see `src/AlitaBot/README.md`'s "Ephemeral
message probe", round 2): the `BOT_NOT_ADMIN` rejection is gone — Telegram now accepts
`sendMessage(receiver_user_id=...)` (`200 OK`, `ephemeral_message_id` populated) — but the
accepted message was never observed by the receiving account's own MTProto client, neither as a
live update (`UpdateNewMessage`/anything else, 20s watch) nor via `Messages_GetHistory` before or
after, even though that account IS the exact `receiver_user_id`. A control `sendMessage` in the
same chat (no `receiver_user_id`) arrived and appeared in history normally in under a second, so
this isn't a harness/update-pump problem.

This is NOT necessarily proof the feature is broken for real users — an official Telegram client
might render ephemeral messages through some mechanism outside the public MTProto schema this
harness's third-party client (WTelegramClient) can observe, the way inline-mode results and some
other bot-only surfaces work. But from this repo's testing/observability standpoint, it means:
**a `200 OK` from `sendMessage(receiver_user_id=...)` is not proof anyone actually saw the
reply.** `/summary` was left as-is (it already only falls back on an actual Telegram-side
rejection, and shipped before this finding), but `/usage`/`/help` were deliberately NOT switched
to prefer ephemeral delivery for this reason — see README.

**Action:** the only way to confirm real delivery is either a second, independent Telegram
account watching the same chat live, or manually checking an official Telegram client (mobile/
desktop) receives a `/summary` reply in an admin-bot chat — neither is available in this
environment. Until confirmed, treat ephemeral delivery as unverified rather than working; don't
extend it to more commands, and consider whether `/summary` itself should stop preferring it.

## `message_log` silently dropped ephemeral replies after the first one per chat (fixed)

Found alongside the above: `handleSummaryCommand` used to log the Bot-API `Message.MessageId`
verbatim for the sent reply — but an ACCEPTED ephemeral send always reports `MessageId = 0` (the
real id is `EphemeralMessageId`, a different field). `message_log` has
`UNIQUE(chat_id, message_id)` with `ON CONFLICT DO NOTHING`, so only the very first ephemeral
`/summary` reply ever landed in `message_log` for a given chat, process-lifetime — every
subsequent one silently no-opped: no exception, no log line, just a missing row (confirmed by
querying `message_log` directly after a real-test run and finding exactly one `message_id = 0`
row despite multiple `/summary` calls). Fixed by `BotHelpers.loggableMessageId`, which logs
`EphemeralMessageId` instead of the `0` `MessageId` whenever that's what Telegram actually
returned. Any future caller of `trySendEphemeralOrReply` that logs the sent message to
`message_log` must go through `loggableMessageId`, not `sent.MessageId` directly.

## `Req.SendRichMessage` >4096-char escalation is unverified against real Telegram (Slice 6)

`Mdv2Delivery.sendFinal` (`Services/ReplyRenderer.fs`) escalates to `Req.SendRichMessage`
(the `markdown` field of `InputRichMessage`) when a final reply's MDV2 form exceeds
Telegram's 4096-char message limit, falling back to plain multipart sends if THAT is
rejected. The fake suite exercises the fallback path's shape (a rejected `sendMessage`/
`editMessageText` — `FakeTgApi`'s `/test/mock/rejectMdv2`) but nothing forces a real LLM
reply past 4096 chars, so the `SendRichMessage` call itself — its exact wire behavior,
whether Telegram accepts the same MDV2-flavored markdown string `parse_mode=MarkdownV2`
does, and what a real rejection looks like — has never been exercised against real
Telegram. In practice `gpt-5-mini` chat replies essentially never reach 4096 chars, so this
is low-probability-but-unverified, same posture as the images API wire format in the
"No real image-gen deployment yet" entry above.

**Action:** if/when this path is suspected of misbehaving in prod (or before relying on it
for a feature that deliberately produces long replies, e.g. `/summary`-length output routed
through the responder), write a real-test that forces a >4096-char scripted-length reply
(not currently possible against real Azure — would need a deliberately verbose prompt) and
confirm `SendRichMessage`'s actual behavior.

## Rewriter pass disables streaming entirely while `REWRITER_ENABLED=true` (Slice 6)

`ResponderService.respondWithRewriter` forces the main LLM call to non-stream
(`IChatCompletion.Complete`) so a second cheap call can rewrite its text before anything is
rendered — a deliberate, documented tradeoff (README's "Rewriter pass" section), not an
oversight: there is no `IReplyRenderer` in play on this path at all, so `STREAM_MODE` is
silently ignored whenever the rewriter is on. Since `REWRITER_ENABLED` defaults to `false`,
this doesn't affect default behavior, but a user chatting with the rewriter on will see the
reply appear all at once (after two sequential LLM round trips) instead of streaming in.

**Action:** if the UX cost turns out to matter in prod, a future slice could stream the
*main* call as usual and only make the *rewrite* pass non-stream, feeding the rewritten text
into a synthetic single-chunk `IAsyncEnumerable<ChatChunk>` for one of the existing
renderers — deferred here because it adds real complexity for a feature that ships off.

## `DraftRenderer` fallback memo is process-lifetime

`Services/ReplyRenderer.fs`'s `DraftRenderer` remembers, per `chatId`, that `sendMessageDraft`
failed and permanently falls back to `EditThrottleRenderer` for that chat — but the memo lives
in an in-process dictionary, not `bot_setting` or the database. A pod/process restart forgets
every chat's fallback state and re-probes `sendMessageDraft` from scratch on the next message.

This is a deliberate simplification, not a bug: worst case is one extra failed
`sendMessageDraft` call per chat per restart (immediately caught and degraded to edit-throttle,
same as the first time), not a user-visible failure. No action needed unless restart frequency
ever gets high enough for the repeated probe calls to matter (unlikely).
