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

## `DraftRenderer` fallback memo is process-lifetime

`Services/ReplyRenderer.fs`'s `DraftRenderer` remembers, per `chatId`, that `sendMessageDraft`
failed and permanently falls back to `EditThrottleRenderer` for that chat — but the memo lives
in an in-process dictionary, not `bot_setting` or the database. A pod/process restart forgets
every chat's fallback state and re-probes `sendMessageDraft` from scratch on the next message.

This is a deliberate simplification, not a bug: worst case is one extra failed
`sendMessageDraft` call per chat per restart (immediately caught and degraded to edit-throttle,
same as the first time), not a user-visible failure. No action needed unless restart frequency
ever gets high enough for the repeated probe calls to matter (unlikely).
