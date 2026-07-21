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

## `ScheduledJobs.fs` cherry-pick deferred

Plan decision D12: the old `feature/alita-bot` branch (pre-Funogram-migration, doesn't build)
has a `ScheduledJobs.fs` for proactive/scheduled bot behavior. Phase 0 is reactive-only
(mention → reply); nothing in `alita-now-for-real` needs a scheduler yet.

**Action:** when proactive features start, cherry-pick `ScheduledJobs.fs` from
`feature/alita-bot` and adapt it to the current Funogram-based stack — don't restart it from
scratch.

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

## `DraftRenderer` fallback memo is process-lifetime

`Services/ReplyRenderer.fs`'s `DraftRenderer` remembers, per `chatId`, that `sendMessageDraft`
failed and permanently falls back to `EditThrottleRenderer` for that chat — but the memo lives
in an in-process dictionary, not `bot_setting` or the database. A pod/process restart forgets
every chat's fallback state and re-probes `sendMessageDraft` from scratch on the next message.

This is a deliberate simplification, not a bug: worst case is one extra failed
`sendMessageDraft` call per chat per restart (immediately caught and degraded to edit-throttle,
same as the first time), not a user-visible failure. No action needed unless restart frequency
ever gets high enough for the repeated probe calls to matter (unlikely).
