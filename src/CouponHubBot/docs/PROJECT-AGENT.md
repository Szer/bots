# Agentic Workflows

This monorepo runs "agentic" GitHub Actions that delegate decisions to an LLM agent. They use
[`openai/codex-action@v1`](https://github.com/openai/codex-action) against **`gpt-5-mini`**
deployed on a Microsoft Foundry (AIServices) resource. The model talks the OpenAI v1 Responses
API and is configured per workflow with a distinct `reasoning_effort`.

`.github/bots.yml` is the single source of truth for bot identity (ArgoCD app, container,
namespace, metric prefix, DB name, product vision doc, roles, query set, known community-member
hashes) — every workflow, gatherer and prompt below reads it instead of hardcoding a bot name.
See `.github/AGENT-FLOWS-REDESIGN.md` for the full redesign rationale (this doc describes the
Phase 1 state: registry + multi-bot product/project coverage; the `monitor` role in that
document is Phase 2 and does not exist yet).

| Agent | Workflow | Trigger | Scope | Effort | Sandbox |
|-------|----------|---------|-------|--------|---------|
| **SRE** | `.github/workflows/_sre-agent.yml` (reusable) | Called directly by `_bot-deploy.yml` on deploy/verify failure, or `sre-manual.yml` (`workflow_dispatch`) | One bot per invocation, any bot on `_bot-deploy.yml` | `high` | `workspace-write` |
| **Project** | `.github/workflows/project.yml` | Daily cron `37 4 * * *` + manual | Repo-wide, one invocation for all bots | `low` | `workspace-write` |
| **Product** | `.github/workflows/product.yml` | Cron `15 10 * * 2,5` + manual | Every bot whose `bots.yml` `roles` include `product` (currently vahter + coupon; **not** alita) | `medium` | `workspace-write` |

All three agents use `workspace-write` even though project/product never modify the repo. Codex's `read-only` sandbox **disables network access**, which would kill the agents' ability to call `gh issue ...` and `curl http://*.internal/...`. **`workspace-write` also defaults network to off**, so every workflow passes `codex-args: '--config sandbox_workspace_write.network_access=true'` to flip it on. The blast radius is bounded by each workflow's `permissions:` block — for project/product, `contents: read` blocks any push to the repo even though the agent can write inside `$GITHUB_WORKSPACE`.

Prompts live in `.github/prompts/{sre,project,product}.md`. Each is loaded verbatim into the agent's system prompt at run time alongside any inline data (evidence report, issue body reference).

## SRE — incident response (any bot, reusable workflow)

`_sre-agent.yml` is a **reusable workflow** (`workflow_call`), not triggered by `issues: labeled`
— that trigger never fired (GitHub Actions does not run workflows off events authored by the
default `GITHUB_TOKEN`, so 6/6 historical runs were `skipped`; see
`AGENT-FLOWS-REDESIGN.md` §1.1). It is instead called directly:

- from `_bot-deploy.yml`'s failure path (`if: failure()`) — bot identity (`bot`, `argocd-app-name`,
  `container-name`, `docker-image`, `commit`, `run-url`) is passed in as workflow inputs, not read
  from an issue body;
- from `sre-manual.yml` (`workflow_dispatch`), a thin wrapper giving the same agent a
  hand-triggered entry point for incidents outside a deploy (`workflow_call` workflows can't be
  dispatched directly).

`verify-deploy.sh` failing still opens a `deploy-failure` issue as the incident record; its
number is passed to the SRE agent (when available) so it can comment on/close it. Opt-out per
bot: pass `sre-enabled: false` to `_bot-deploy.yml` (e.g. `alita-deploy.yml` does this until the
bot has settled in prod).

A called reusable workflow cannot escalate permissions beyond its caller — every workflow calling
`_sre-agent.yml` (`vahter-deploy.yml`, `coupon-deploy.yml`, `alita-deploy.yml`, `sre-manual.yml`)
grants at least `contents: write, pull-requests: write, issues: write` for this reason; an
under-granted caller fails the whole run at load time with a jobless `startup_failure`.

The agent can rollback ArgoCD apps (disabling auto-sync first), trigger syncs, delete pods,
escalate complex bugs into new `priority-high` issues, and open one-liner fix PRs. Bash sandboxing
is coarse — `workspace-write` lets it create branches but also exposes the runner's shell; rely on:
- `permissions:` scoping `GITHUB_TOKEN` to `issues / pull-requests / contents`.
- The ArgoCD token gating real prod changes.
- Prompt-level guidance to stick to `gh / curl / jq / git`.
- An `if: always()` cleanup step in `_sre-agent.yml` that re-enables ArgoCD auto-sync even if the
  agent run is killed mid-investigation.

## Project — daily repo-wide backlog maintenance (no runtime responsibility)

Runs once a day, repo-wide, covering every bot — **not** looped per bot. The cleanup job closes
yesterday's stale orchestration issue, a fresh `project`-labelled orchestration issue is created,
and the agent runs directly against the repo checkout + GitHub issues API.

This role dropped ALL runtime/metrics evidence (memory thresholds, log-volume threshold, 5xx,
Loki queries, pod-health summary) — there is no VPN step and no gatherer invocation in
`project.yml` anymore. Those signals move to the future `monitor` role (`AGENT-FLOWS-REDESIGN.md`
§6.1, Phase 2, not yet built): baseline-relative anomaly detection needs a cadence and evidence
bundle this daily repo-wide sweep was never suited to (see §1.4/§1.8 of the redesign doc for why
the old absolute thresholds — "5xx errors", "log volume above 10,000 lines/day" — were dead
logic).

The agent's mandate is now **demonstrable tech debt only**: dead/unreachable code, stale docs,
`bot_setting`/config drift, `TODO`/`FIXME` in shipped code, dependency/migration hygiene, and
tests missing for a path that *actually failed* per an existing SRE/monitor finding it can cite.
It does **not** perform static code review or bug-hunting, and does **not** attempt to reconstruct
runtime signals itself (no working Prometheus/Loki/ArgoCD credentials in this job). Findings use
**stable, undated titles** so a recurring problem bumps its existing issue instead of spawning
duplicates. A clean day where nothing is filed is a valid outcome.

Issue lifecycle: `project` issues are left **unassigned** for human triage. Priority is capped at
`priority-medium` — only humans set `priority-high`. Bot-specific findings additionally carry that
bot's `bot:<name>` label; shared-infra findings (`src/BotInfra/`, `tests/`, CI config) need none.

## Product — chat-mined signal, per bot with a `product` role

Runs Tue/Fri at 10:15 UTC, once per bot whose `bots.yml` entry lists `product` in `roles`
(currently `vahter` and `coupon` — **not** `alita`, which has 13 chat messages ever and no
product coverage by design). One VPN session serves every bot in the run (a `strategy.matrix`
would queue behind itself on the single-occupancy `aks-vpn` concurrency group and re-establish
the tunnel per bot); `scripts/gather/product.sh <bot>` runs once per applicable bot in a bash
loop, each producing its own evidence report and its own orchestration issue
(`Product analysis: <DisplayName> <date>`, labelled `product` + `bot:<name>`).

**Guard step.** Before any agent is invoked, the workflow checks every gathered bot's manifest
(`{"sources": {...}}`, always the first line of a gatherer's stdout). If any required source
(`postgres`, `prometheus`; `loki` is optional) came back non-`ok`, the workflow calls
`scripts/report-degraded.sh` (bumps/creates a single `evidence-pipeline-degraded` issue) and the
guard step fails — **no agent is invoked for any bot that run.** An LLM must never be handed a
bundle of zeros and asked whether things look fine; this is what silently produced 21 consecutive
empty product reports (2026-05-15 → 2026-07-25) before the gatherer was made fail-loud.

Chat text mining is the **primary** signal (per-bot verbatim chat query in
`scripts/queries/<query_set>/*.sql`); `/feedback`-style triage (`user-feedback` label) is
secondary and must degrade to a no-op when the queue is empty — across the fleet it has had long
zero-submission stretches against a live chat. `PRODUCT-VISION.md` (in this same directory) is
read first when a bot has one configured (`bots.yml` `product_vision`); vahter and alita don't,
and the prompt is written to degrade cleanly rather than treat that as a defect. Known
community-member identity hashes (previously hardcoded in the prompt for coupon only) now live in
`bots.yml`'s per-bot `community_hashes` list.

## Azure / Foundry configuration

The agents authenticate with a single API key from a Microsoft Foundry AIServices account (Codex does not support Entra ID OIDC). Required GitHub items:

| Item | Value |
|---|---|
| Secret `AZURE_OPENAI_API_KEY` | primary access key of the Foundry resource |
| Secret `AZURE_OPENAI_BASE_URL` | full Responses API URL **including the `/responses` suffix**, e.g. `https://szer-foundry.cognitiveservices.azure.com/openai/v1/responses`. Passed verbatim to the action's `responses-api-endpoint` input. (Stored as a secret rather than a variable for consistency with the API key; not actually sensitive — the secret form just keeps it off the workflow log surface.) |

Each workflow calls `openai/codex-action@v1` directly with three inputs that wire it to Foundry: `openai-api-key`, `responses-api-endpoint`, and `model: gpt-5-mini`. The action runs its own local `@openai/codex-responses-api-proxy` and writes its own Codex config — **do not manually write `~/.codex/config.toml`** or set `codex-home` when using the action; that path collides with the proxy's generated config and produces a `duplicate key` TOML error on first run. The deployment name in Foundry must match `gpt-5-mini` exactly.

## Labels used by the agents

| Label | Used by |
|-------|---------|
| `deploy-failure` | failure-notify → SRE |
| `evidence-pipeline-degraded` | gatherer refused to run an agent on a bundle with an unreachable required source (product guard step) |
| `project`, `infra` | project agent backlog |
| `product` | product orchestration issues |
| `bot:vahter` / `bot:coupon` / `bot:alita` | bot-specific findings from any agent, and every product orchestration issue. Created on demand (`gh label create --force`) by product.yml/project.yml from the `.github/bots.yml` key list. |
| `bug`, `feature-request` | product agent outputs |
| `user-feedback` | feedback intake (created by users / external flow) |
| `priority-high` / `priority-medium` / `priority-low` | priority across all agents |

## Cleanup mechanism

Each scheduled workflow has a `cleanup` job at the top that closes stale orchestration issues from prior runs whose dated titles match the workflow's regex. This survives the agent timing out, network errors, or any case where the agent fails to close its own orchestration issue.

## Related files

| Path | Purpose |
|------|---------|
| `.github/bots.yml` | Bot registry — single source of truth read by every workflow/gatherer/prompt below |
| `.github/workflows/_bot-deploy.yml` | Reusable deploy workflow; opens deploy-failure issue on failure, calls `_sre-agent.yml` |
| `.github/workflows/_sre-agent.yml` | SRE agent, reusable (`workflow_call`) |
| `.github/workflows/sre-manual.yml` | Hand-triggered entry point into `_sre-agent.yml` |
| `.github/workflows/project.yml` | Project agent runner (repo-wide, no VPN/gather) |
| `.github/workflows/product.yml` | Product agent runner (per-bot loop, one VPN session) |
| `.github/prompts/{sre,project,product}.md` | Per-agent system prompts |
| `scripts/gather/lib.sh` | Shared gatherer helpers: `probe()`/`retry_curl()` (fail-loud), `bots.yml` reader, manifest emitter |
| `scripts/gather/runtime.sh <bot>` | Per-bot infra/runtime evidence gatherer (replaces `gather-metrics.sh`) |
| `scripts/gather/product.sh <bot>` | Per-bot product-signal evidence gatherer (replaces `gather-product-data.sh`) |
| `scripts/queries/{vahter,coupon,alita}/*.sql` | Per-bot verified query sets used by `product.sh` (and the future `monitor` role) |
| `scripts/report-degraded.sh` | Bumps/creates the single `evidence-pipeline-degraded` issue from the product guard step |
| `scripts/gather-metrics.sh`, `scripts/gather-product-data.sh` | DEPRECATED forwarding shims to the `scripts/gather/*.sh` above — no workflow calls these directly anymore |
| `scripts/verify-deploy.sh` | Post-deploy verification; failure here triggers the SRE chain |
