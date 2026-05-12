# Agentic Workflows

This monorepo runs three "agentic" GitHub Actions that delegate decisions to an LLM agent. They use [`openai/codex-action@v1`](https://github.com/openai/codex-action) against **`gpt-5-mini`** deployed on a Microsoft Foundry (AIServices) resource. The model talks the OpenAI v1 Responses API and is configured per workflow with a distinct `reasoning_effort`.

| Agent | Workflow | Trigger | Scope | Effort | Sandbox |
|-------|----------|---------|-------|--------|---------|
| **SRE** | `.github/workflows/sre.yml` | `issues: labeled` (`deploy-failure`) | All bots in this monorepo | `high` | `workspace-write` |
| **Project** | `.github/workflows/project.yml` | Daily cron `37 4 * * *` + manual | CouponHubBot only | `minimal` | `read-only` |
| **Product** | `.github/workflows/product.yml` | Cron `15 10 * * 2,5` + manual | CouponHubBot only | `medium` | `read-only` |

Prompts live in `.github/prompts/{sre,project,product}.md`. Each is loaded verbatim into the agent's system prompt at run time alongside any inline data (metrics snapshot, product data report, issue body reference).

## SRE — incident response (monorepo-wide)

Wakes on any issue labelled `deploy-failure`. Every bot that uses the shared `_bot-deploy.yml` reusable workflow opens such an issue automatically when `verify-deploy.sh` fails — adding a new bot needs no agent changes, only a deploy workflow that calls `_bot-deploy.yml`.

Opt-out per bot: pass `sre-enabled: false` to `_bot-deploy.yml`.

The failure issue body always carries explicit bot identity (ArgoCD app, container label, GHCR image, commit, run URL). The SRE prompt reads those fields and templates the runbook against the failing bot — no hardcoded bot tables.

The agent can rollback ArgoCD apps (disabling auto-sync first), trigger syncs, delete pods, escalate complex bugs into new `priority-high` issues, and open one-liner fix PRs. Bash sandboxing is coarse — `workspace-write` lets it create branches but also exposes the runner's shell; rely on:
- `permissions:` scoping `GITHUB_TOKEN` to `issues / pull-requests / contents`.
- The ArgoCD token gating real prod changes.
- Prompt-level guidance to stick to `gh / curl / jq / git`.

## Project — daily backlog maintenance (coupon-only)

Each morning at 04:37 UTC the cleanup job closes yesterday's stale orchestration issue, then `gather-metrics.sh` snapshots Prometheus + Loki + ArgoCD for the `coupon-bot` deployment. The snapshot is embedded in a new `project`-labelled orchestration issue, the agent is invoked with the snapshot inlined, and it creates / bumps / closes backlog issues.

Issue lifecycle: `project` issues are left **unassigned** for human triage. Priority is capped at `priority-medium` — only humans set `priority-high`.

## Product — feedback triage (coupon-only)

Runs Tue/Fri at 10:15 UTC. `gather-product-data.sh` collects bot usage from Prometheus, chat-message themes from the `chat_message` table, and pending `user-feedback` issues from GitHub. The agent triages each open feedback issue into one of: not actionable (close), bug (new `bug` issue, close original), or feature request (new `feature-request` issue, close original).

`PRODUCT-VISION.md` (in this same directory) is the agent's first read on every run — only the repository owner may edit it.

## Azure / Foundry configuration

The agents authenticate with a single API key from a Microsoft Foundry AIServices account (Codex does not support Entra ID OIDC). Required GitHub items:

| Item | Value |
|---|---|
| Secret `AZURE_OPENAI_API_KEY` | primary access key of the Foundry resource |
| Variable `AZURE_OPENAI_BASE_URL` | `https://<resource>.cognitiveservices.azure.com/openai/v1` — **no `/responses` suffix** (Codex appends it via `wire_api = "responses"`) |

Each workflow writes a `~/.codex/config.toml` at runtime pointing at the Foundry endpoint, then calls `openai/codex-action@v1`. The model deployment name in Foundry must match `gpt-5-mini` exactly.

## Labels used by the agents

| Label | Used by |
|-------|---------|
| `deploy-failure` | failure-notify → SRE |
| `project`, `infra` | project agent backlog |
| `product` | product orchestration issues |
| `bug`, `feature-request` | product agent outputs |
| `user-feedback` | feedback intake (created by users / external flow) |
| `priority-high` / `priority-medium` / `priority-low` | priority across all agents |

## Cleanup mechanism

Each workflow has a `cleanup` job at the top that closes stale orchestration issues from prior runs whose dated titles match the workflow's regex. This survives the agent timing out, network errors, or any case where the agent fails to close its own orchestration issue.

## Related files

| Path | Purpose |
|------|---------|
| `.github/workflows/_bot-deploy.yml` | Reusable deploy workflow; opens deploy-failure issue on failure |
| `.github/workflows/sre.yml` | SRE agent runner |
| `.github/workflows/project.yml` | Project agent runner |
| `.github/workflows/product.yml` | Product agent runner |
| `.github/prompts/{sre,project,product}.md` | Per-agent system prompts |
| `scripts/gather-metrics.sh` | Project agent's metrics collector (Prom/Loki/ArgoCD) |
| `scripts/gather-product-data.sh` | Product agent's data collector (Prom/Postgres/GitHub issues) |
| `scripts/verify-deploy.sh` | Post-deploy verification; failure here triggers the SRE chain |
