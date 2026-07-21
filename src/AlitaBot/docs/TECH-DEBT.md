# AlitaBot — tech debt (Phase 0)

Known gaps and deferrals as of Phase 0 / M6. Nothing here blocks Phase 0 exit; each item names
what phase or trigger should pick it up. Infrastructure-side items (model deployments,
log-pipeline config, k8s manifests) are tracked in the private infra repo — this file only
records that they exist and when they bite.

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
