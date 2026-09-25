# Handoff: build the scheduled-workflow canary in Szer/bots

**For:** the agent implementing the bots-side sentinel (this repo only).
**From:** the agent that did the PagerDuty terraform in my-infra.
**Status:** ALL prerequisites are DONE. Start now. Background and my answers to the original work order are in `.github/canary-pagerduty-handoff.md` (sections 1 and 5).

## 1. What already exists (do not redo, do not touch)

- PagerDuty service **"GitHub Actions"** with an Events API v2 integration is live (my-infra PR #146, applied 2026-09-02, run 33629978524 green).
- Repo secret **`PAGERDUTY_ROUTING_KEY`** is set in Szer/bots (2026-09-02 12:30 UTC). Never print, echo, mask-test, or log it. If a run shows it is missing, no-op quietly and print one line saying so; never fail the canary because of a missing secret.
- The service has `auto_resolve_timeout = null`: an incident stays open until YOU send `event_action: resolve`. There is no time-based cleanup. Every trigger path must have a matching resolve path.
- Urgency is **severity_based**. `severity: warning` = low urgency = silent mobile push. `severity: critical` = high urgency = loud page. This mapping is BINDING (owner's notification model: push for "worth knowing", page only for "must act now").
- The my-infra repo is out of scope. Do not open it, do not reference its files.

## 2. What to build

One new workflow, `.github/workflows/workflow-canary.yml` (name it `Workflow Canary`), on its own schedule, that:

1. Lists recent runs of every scheduled workflow in this repo via `gh run list` with `GITHUB_TOKEN` (`permissions: actions: read` is enough; add `contents: read` if you check out the repo).
2. Classifies each workflow as `healthy`, `failed` (latest completed scheduled run concluded `failure`, `timed_out`, or `startup_failure`), or `stalled` (no completed run within its expected cadence + grace).
3. Sends PagerDuty Events API v2 events: `trigger` for failed/stalled, `resolve` when healthy again. Endpoint `https://events.eu.pagerduty.com/v2/enqueue` (EU account; the global host also routes, but prefer EU).
4. Uses one stable `dedup_key` per workflow, e.g. `bots/<workflow-file-name>`, so repeated polls update one incident and recovery closes exactly that incident. Resolving a dedup key that has no open incident is harmless; do it unconditionally on healthy.

Plain `curl` + `gh` + `jq`. No `openai/codex-action`, no LLM, no VPN, no `aks-vpn` concurrency group. The canary must be dumber than the things it watches.

## 3. Workflows to watch (ground truth from `.github/workflows/`, 2026-09-02)

| Workflow file | `name:` | cron (UTC) | Cadence | Suggested stall threshold |
|---|---|---|---|---|
| `monitor.yml` | Monitor | `20 */4 * * *` | 4h | 5h30m |
| `project.yml` | Daily Project Assessment | `37 4 * * *` | 24h | 26h |
| `product.yml` | Product Analysis | `15 10 * * 2,5` | Tue/Fri (up to 4d gap) | 4d + 2h = 98h |
| `vahter-mirror-reconcile.yml` | Vahter: Mirror Reconcile | `10 7 * * *` | 24h | 26h |

Keep this as a static table in the workflow (a small JSON or bash array with file, threshold), not cron parsing. Adding a workflow = adding a row. Grace must be at least 60 min: GitHub delays scheduled runs by minutes to over an hour under load, and schedules are silently disabled after 60 days of repo inactivity (stall detection covers that case, which is why it is mandatory).

Notes per workflow:
- **`vahter-mirror-reconcile.yml` fails BY DESIGN** as its signal (it fails the run when a mirror sync PR is stuck or check-less). A failure there is a real "someone must look" condition and today it notifies nobody. Treat it exactly like the others: warning.
- Monitor, Project, Product share the `aks-vpn` concurrency group, so a run can conclude `cancelled`. Treat `cancelled` as neither failed nor healthy: it does not trigger, and it does not count as a fresh healthy run for stall purposes. Use the latest run whose conclusion is `success`/`failure`/`timed_out`/`startup_failure`.
- Filter to `--event schedule` when judging cadence. A manual `workflow_dispatch` success may be counted as healthy for the failed check but should not reset the stall clock unless you decide otherwise and say so in the PR.
- An unloadable workflow (bad YAML) produces a jobless `startup_failure` with no error text in the API, or no run at all. Both must surface: `startup_failure` via the failed path, no-run via the stalled path.

## 4. Severity rules (BINDING)

- One workflow failed or stalled: `severity: warning` (silent push).
- Two or more of the three agent workflows (Monitor, Project, Product) failed or stalled in the same poll: `severity: critical` (loud page). This is the 2026-08-21 pattern, when a single upstream action change killed all of them at once.
- Send severity per dedup key. When the count drops back to one, the next trigger for that key should carry `warning` again (PagerDuty updates urgency on re-trigger with a new severity).
- Payload: `source` = `Szer/bots/<workflow-file>`, `summary` = one line with workflow name, state, and the run URL or "no run in N hours", `component` = workflow file, `group` = `github-actions`, `custom_details` = latest run id, conclusion, created_at, threshold. These fields show in the PD mobile UI; use them.

## 5. Canary schedule and self-reporting

- Cron: hourly at an odd minute, e.g. `50 * * * *`. Also `workflow_dispatch` with inputs `dry_run` (boolean, default `true`: print every event that would be sent, send nothing) and `test_event` (choice: `none` / `warning-roundtrip`: send one `warning` trigger with dedup key `bots/canary-selftest` and immediately resolve it). Never send `critical` from a test path.
- The canary's own failure is the remaining blind spot. Mitigate cheaply: add `if: failure()` last step that sends a `warning` trigger with dedup key `bots/workflow-canary` (same secret), and resolve that key at the start of every successful run. If PagerDuty itself is unreachable nothing can help; accept it.
- `timeout-minutes: 10`. Runner `ubuntu-latest`. `gh`, `jq`, `curl` are preinstalled.
- On any non-2xx from PagerDuty, print the response body (it never contains the key) and fail the step. Do not retry blindly.

## 6. Repo rules that WILL fail your PR if ignored

- **Comment gate** (`.comment-ratio.conf`, enforced by `.githooks/pre-commit` and `comment-lint.yml`): YAML ratio 0.30 with floor 5, and **no comment block longer than 2 lines anywhere, ever**. Existing workflows have long headers; those predate the gate. Yours cannot. Comments = constraints only, no narrative, no process history. Run `make install-hooks` once so the pre-commit hook checks you locally.
- **actionlint** runs on every workflow via `lint-workflows.yml`. Run `actionlint .github/workflows/workflow-canary.yml` locally before pushing if it is installed; otherwise rely on CI and read the failure.
- Do not add the canary to `.github/bots.yml` (that registry is for bots, not workflows) and do not edit any existing workflow. Optional in-workflow `if: failure()` hooks in the agent workflows are a separate follow-up PR, not this one.
- Do not run any real-Telegram or paid-LLM test workflows (`*-real-test.yml`). Not related, listed because agents keep tripping on it.
- PR title style: `CI: workflow canary → PagerDuty for failed/stalled scheduled workflows`. Keep the PR body factual: what is watched, thresholds, severity rules, how to test, how to add a workflow.

## 7. Test plan (do this, report the evidence)

1. Push the branch, open the PR, confirm `lint-workflows` and `comment-lint` are green.
2. `workflow_dispatch` the canary from the PR branch with `dry_run=true`: paste the classification table it printed for all four workflows into the PR. All four should be healthy today (Monitor and Project were re-verified green on 2026-09-02; check Product and Mirror Reconcile yourself and report what you see).
3. `workflow_dispatch` with `test_event=warning-roundtrip`, `dry_run=false`: report the two HTTP responses (status only, no bodies with keys). The owner confirms a silent push arrived on the phone and that the incident auto-closed. Do not proceed to merge until the owner confirms.
4. Do NOT force a real workflow to fail to test the critical path. Describe in the PR how to test it by hand if the owner wants to (e.g. temporarily set one threshold to 1 minute on a branch dispatch with `dry_run=true`).

## 8. Report back

State: PR number + CI conclusions, the dry-run classification table, the roundtrip HTTP statuses, every deviation from sections 2 to 6 with the reason, and anything in the run history that surprised you (e.g. a workflow that is already stalled today).
