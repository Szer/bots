# Agent Flows — Unification Proposal

Status: **proposal, not implemented**. Written 2026-07-27 from a four-way audit of the
workflows, the issues they produced, Grafana/Loki, and all three bot databases.

---

## 1. What is actually wrong

Findings, each independently verified. These are not style complaints — every one of them
is a reason a real anomaly cannot reach you.

### 1.1 The SRE agent has never run. Not once.

`sre.yml` triggers on `issues: [labeled]`. The `deploy-failure` issue and its label are
created by `_bot-deploy.yml` using `secrets.GITHUB_TOKEN`. **GitHub Actions does not
trigger workflows from events authored by the default `GITHUB_TOKEN`** (anti-recursion).

Evidence: `gh run list --workflow=sre.yml` → 6 runs ever, **all `conclusion: skipped`**,
latest 2026-06-23, every one of them fired by a human hand-labeling an unrelated issue.
Meanwhile 9 real `deploy-failure` issues were filed (#80, #156, #167, #173, #219, #220,
#238, #243, #251). Seven of them, all since 2026-06-27, were closed with **zero comments
from anyone**.

The SRE prompt is 13KB of careful runbook that has never executed a single line.

### 1.2 Vahter — the highest-traffic bot — has no product or project coverage

`project.yml:57-58` hardcodes `ARGOCD_APP_NAME: coupon-bot` and `CONTAINER_NAME: coupon-bot`.
`product.yml:68-69` hardcodes `CONTAINER_NAME: coupon-bot` and a `coupon_hub_bot` DSN.

`.github/prompts/project.md` nevertheless contains a "Bots in this repo" table listing
VahterBanBot, and its summary template asks the agent to fill in
`Pods healthy: vahter-bot yes/no, coupon-bot yes/no`. The agent is asked a question its
evidence cannot answer, so it answers optimistically. Vahter reads as healthy because it
is never measured.

Alita appears in no agent workflow at all, and is not mentioned anywhere in `AGENTS.md`.

### 1.3 "Source unreachable" is indistinguishable from "everything is fine"

`scripts/gather-metrics.sh:28-31` swallows every curl failure into
`{"data":{"result":[]}}`. Dead Prometheus therefore renders as *zero restarts, zero errors,
zero 5xx* — and `project.md` instructs the agent that a clean snapshot means "create
nothing, an empty backlog day is a success."

This is the same bug that blinded the product agent for 21 consecutive runs
(2026-05-15 → 2026-07-25, fixed in #264). `gather-product-data.sh` now aborts loudly.
Its sibling `gather-metrics.sh` still does the opposite. The class of bug is not fixed,
only one instance of it.

### 1.4 There is no baseline, so no anomaly is computable

Nothing persists between runs. `gather-metrics.sh` emits current values only. The single
trend field in the entire system is `INTERACTIONS_PREV` in `gather-product-data.sh`.

The thresholds in `project.md` are absolute constants:

- `Log volume above 10,000 lines/day` — vahter runs at 4,000–8,900/day, permanently just
  under it. A 5× step-change in its own volume is invisible.
- `Memory above 256 MB` — produced #271, a threshold-crossing with 0 restarts and 0 errors,
  i.e. probably just GC behaviour.
- `Any 5xx errors` — **structurally impossible**. The `/bot` webhook returns 200 to Telegram
  regardless of internal exceptions, to avoid Telegram retry storms. Zero 5xx exist for any
  bot, ever. Both `project.md` and `sre.md` use 5xx rate as a primary health gate. Real
  failure signal lives only in Loki `level="Error"`.

### 1.5 No change correlation — so both error directions are guaranteed

Vahter's log volume stepped ~5× on 2026-07-18 and stayed there. That was **intentional**:
`cb7f346` / PR #213 added `RawUpdate` nested-JSON logging, merged 2026-07-16 21:52 UTC.

A naive threshold detector would have filed a noise issue about your own deliberate change.
The current system filed nothing because it wasn't looking. Neither is correct. Detecting a
shift is worthless without the ability to ask "did something ship right before this?", and
there are **no deploy annotations in Grafana** to make that a query.

### 1.6 Dedup was root-cause blind; 78% of findings were duplicates

The project agent filed **27 issues between 2026-04-30 and 2026-06-25 that collapse into
6 root causes**:

| Root cause | Filed | Verdict |
|---|---|---|
| `:> Task` "invalid runtime cast" | 6× | Hallucination — valid idiomatic F# upcast |
| Blocking `.Result` at startup | 7× | By design — no `SynchronizationContext` in ASP.NET Core |
| `DateTime.MinValue` sentinel | 4× | **Genuine** — fixed in PR #144 |
| Migrations missing GRANT | 4× | False positive — grants consolidated by design |
| `.Result` in test fakes | 3× | By design — test-only code |
| Dev docker-compose passwords | 3× | By design — throwaway local creds |

You cleared all of it in one sitting on 2026-06-23/26. The 2026-06-25 refocus
(`abd9e47`, "runtime signals, not static code review") visibly helped — but the suppression
rule ("clean day, file nothing") is doing most of the work, not better findings.

The only durable memory of a rejected finding is the `NOTE(project-agent):` source-comment
hack from PR #144 — a workaround bolted into `.fs` files because the agent has no
machine-readable suppression list.

### 1.7 The findings that worked share one property

The two clear wins both **transcribed** evidence rather than inferring it:

- **#158** → PR #162. Quoted a real Loki line with timestamp, level, exception text and
  TraceId `aec5609d`.
- **#266** → PR #267, shipped same day. Quoted verbatim community chat messages with
  timestamps.

Every finding built by inference over source code or thin metrics was wrong or
unfalsifiable. #222 is the clearest case: it argues coupon-bot is "Degraded" while its own
evidence block shows 0 restarts, 0 errors, 0 5xx, and a healthy pod, then punts to the SRE
agent — which, per §1.1, does not exist.

### 1.8 Assorted

- `.github/prompts/review.md` is invoked by **no workflow**. Orphaned.
- Grafana alert rules `BotNotAvailable` and `ErrorLogsDetected` both scope to
  `namespace=~"vahter-bot|coupon-bot"` — **alita is excluded from all alerting**.
- No dashboards exist for coupon-bot or alita-bot. One exists for vahter.
- `query_loki_patterns` returns 404 on this Loki — pattern mining must be built by hand
  (group by `SourceContext` + message prefix).
- Vahter's legacy tables (`message`, `user`, `banned`, `banned_by_bot`, `callback`,
  `vahter_actions`, `llm_triage`) froze at **2026-04-02** during the event-sourcing cutover
  (Flyway V23). Any agent querying them gets four-month-stale data that looks live.
  `DB.fs:866 GetVahterStats` is dead code; `DB.fs:896 GetVahterActionStats` reading `event`
  is the live path.
- Nothing said in the vahters' private moderator channel (`ADMIN_CHANNEL_ID
  = -1001170325774`) is persisted. `Bot.fs:1533-1550` — recognized `/vahter` commands are
  dispatched without `InsertMessage`; free-form discussion falls through the
  `ChatsToMonitor` gate into a silent no-op. The richest qualitative feedback source in the
  fleet exists only inside Telegram.

---

## 2. Signal inventory — what each bot can actually support

| Signal | vahter | coupon | alita |
|---|---|---|---|
| Chat text | `event` (`MessageReceived`) — **not** legacy `message` | `chat_message` (sparse, bursty) | `message_log` (13 rows ever) |
| Feedback command | none | `user_feedback` (4 rows, last 2026-05-10) | none |
| Moderator discussion | **not persisted** (Telegram only) | n/a | n/a |
| Domain event log | `event` (~2.2M rows, live) | `coupon_event` (4,987) | none |
| ML/LLM verdicts | `MlScoredMessage`, `LlmClassified` | none | none |
| Cost metering | none | none | `llm_usage` |
| Business metrics | `vahter_messages_processed_total{chat_id}`, `_deleted_total`, `_users_banned_total` | `couponhubbot_batch_*`, `_command_total`, `_callback_total`, `_button_click_total` | `alitabot_llm_cost_usd_total`, `_llm_latency_ms`, `_command_total`, `_tool_call_total` |
| Log volume | 4,000–8,900 lines/day | 36–162 lines/day | 0 lines on 8 of 14 days |
| Errors (7d) | 7 | **0** | 3 |
| Enough for weekly product review | **yes** | **yes** (via `coupon_event`, not chat) | **no** |

Verified per-bot daily series with tested SQL exist for all three; they are collected in
§4.3 rather than repeated here.

Two things to note:

- **coupon-bot logged zero Warning and zero Error lines in 7 days.** Plausible for a small
  bot, but it should be confirmed by one source read that exceptions aren't being swallowed
  or logged at Information, before it is trusted as a baseline.
- **Users complain in the community chat, not via `/feedback`** — 4 feedback rows all-time,
  none since May, against a live community chat. Chat mining is the primary product signal;
  the `user-feedback` label triage path is near-dead weight and should stop being the
  headline flow in `product.md`.

---

## 3. Proposed shape

Four roles, not three. The current `project` agent is doing two incompatible jobs — runtime
anomaly watch and repo tech-debt review — on one daily cadence with one evidence bundle.
Split them.

| Role | Cadence | Scope | Owns |
|---|---|---|---|
| **monitor** *(new)* | every 4h, per bot | one bot | Runtime anomalies vs baseline, with change correlation |
| **product** | 2×/week, per bot with signal | one bot | Chat mining, domain funnel trends, feature/bug findings |
| **project** | daily, repo-wide | whole repo | Tech debt, stale docs, config drift. **No runtime responsibility** |
| **sre** | event-driven | one bot | Deploy failures + monitor P1 escalations |

`monitor` is the answer to "all bots should have monitoring". It is deliberately a separate
role from `project` because it needs a different cadence (hours, not days), different
evidence (time series + baselines, not source files), and a different failure mode (a missed
regression, not a missed refactor).

### 3.1 Bot registry — `.github/bots.yml`

Single source of truth. Every workflow, gatherer and prompt reads from it. Adding a fourth
bot is one entry, not a workflow edit.

```yaml
bots:
  vahter:
    display_name: VahterBanBot
    argocd_app: vahter-bot
    container: vahter-bot
    namespace: vahter-bot
    metric_prefix: vahter_
    source_dir: src/VahterBanBot/
    migrations_dir: src/vahter-bot/migrations/
    db_secret_prefix: DB_VAHTER          # DB_VAHTER_HOST / _USERNAME / _PASSWORD
    db_name: vahter_bot
    product_vision: null                  # no vision doc yet
    roles: [monitor, product, sre]
    traffic_class: high                   # drives baseline windows + alert sensitivity
    query_set: vahter                     # -> scripts/queries/vahter/*.sql
    notes: |
      Legacy tables (message, user, banned, banned_by_bot, callback, vahter_actions,
      llm_triage) FROZEN at 2026-04-02 (event-sourcing cutover, Flyway V23).
      Query `event` / `snapshot_message` / `snapshot_user` only.
  coupon:
    display_name: CouponHubBot
    argocd_app: coupon-bot
    container: coupon-bot
    namespace: coupon-bot
    metric_prefix: couponhubbot_
    source_dir: src/CouponHubBot/
    migrations_dir: src/coupon-hub-bot/migrations/
    db_secret_prefix: DB_COUPON
    db_name: coupon_hub_bot
    product_vision: src/CouponHubBot/docs/PRODUCT-VISION.md
    roles: [monitor, product, sre]
    traffic_class: low
    query_set: coupon
  alita:
    display_name: AlitaBot
    argocd_app: alita-bot
    container: alita-bot
    namespace: alita-bot
    metric_prefix: alitabot_
    source_dir: src/AlitaBot/
    migrations_dir: src/alita-bot/migrations/
    db_secret_prefix: DB_ALITA
    db_name: alita_bot
    product_vision: null
    roles: [monitor, sre]                 # product skipped: insufficient signal
    traffic_class: dormant                # liveness-based detection, never volume-based
    query_set: alita
```

`traffic_class` is load-bearing. `dormant` must switch the monitor from
"volume dropped → investigate" (which would fire on 8 of 14 days for alita) to
"pod not ready / restarted / errored → investigate".

### 3.2 Evidence layer that cannot lie

Replace the two gatherers with `scripts/gather/{runtime,product}.sh <bot>`, both reading
`bots.yml`. Hard requirements:

1. **Fail loud.** No `|| echo '{"data":{"result":[]}}'`. Every source is probed; the result
   is a manifest:

   ```json
   { "bot": "vahter", "generated_at": "...",
     "sources": { "prometheus": "ok", "loki": "ok", "argocd": "ok", "postgres": "unreachable" } }
   ```

2. **The workflow refuses to invoke the agent** when a required source is down. It bumps a
   single `evidence-pipeline-degraded` issue instead. An LLM must never be handed a bundle
   of zeros and asked whether things look fine. This permanently kills the 21-run failure
   class.

3. **Emit comparisons, not raw numbers.** For every series: current, trailing 7-day median,
   trailing 28-day median, ratio, and z-score. The model should never be asked to do
   arithmetic over history it cannot see.

4. **Include change context** — merges to `main` touching this bot's `source_dir`, ArgoCD
   deploy history, and Grafana annotations, for the preceding 72h.

### 3.3 Baseline state

Persist per-bot daily rollups to an orphan `agent-state` branch (durable, diffable,
reviewable, survives cache eviction — unlike Actions cache):

```
agent-state/
  vahter/2026-07.jsonl      # one line per run: series -> value
  coupon/2026-07.jsonl
  alita/2026-07.jsonl
  suppressions.json         # fingerprints closed as invalid/wontfix/by-design
```

Retention in Loki is at least ~90 days, so the first ~4 weeks can be **backfilled** from
Loki/Prometheus rather than waiting a month for baselines to warm up.

### 3.4 Deploy annotations

Add one step to `_bot-deploy.yml`: POST a Grafana annotation on every successful sync,
tagged `deploy`, `bot:<name>`, `sha:<short>`. Cost: one curl. Payoff: change correlation
becomes a query instead of a manual `git log`, and every dashboard gets deploy markers.

### 3.5 Fix the SRE trigger

Drop the `issues: [labeled]` trigger entirely. Convert `sre.yml` into a **reusable
workflow** called directly from `_bot-deploy.yml`:

```yaml
sre:
  needs: [deploy, verify]
  if: failure()
  uses: ./.github/workflows/_sre-agent.yml
  with:
    bot: ${{ inputs.bot }}
    run_url: ${{ ... }}
    commit: ${{ github.sha }}
  secrets: inherit
```

No token games, no anti-recursion rule, no dependence on label propagation. Keep filing the
`deploy-failure` issue for the record; the agent comments on it by number passed as an input.

Second entry point: `workflow_dispatch` plus a `repository_dispatch`-style call from the
monitor role, so a P1 detected between deploys can also reach the SRE agent.

### 3.6 Deterministic dedup

Move dedup out of LLM judgment. Every finding issue carries a fingerprint in an HTML comment:

```html
<!-- agent-fingerprint: vahter/monitor/error-burst/AzureBotOcr -->
```

The gatherer pre-fetches all open **and closed** fingerprints plus `suppressions.json` and
hands both to the agent. Rules become mechanical:

- fingerprint exists and open → **comment on it**, never a new issue
- fingerprint in suppressions → **silent, not even mentioned**
- new fingerprint → allowed, and the summary must say why it is not a variant of an existing one

This replaces the `NOTE(project-agent):` source-comment hack, which should then be removed
from the `.fs` files.

### 3.7 Prompt rules that would have prevented most bad issues

Add to every analytical prompt, verbatim:

> **Every finding must quote a verbatim artifact from the evidence bundle** — a log line
> with its timestamp and TraceId, a SQL result row, a chat message with timestamp, or a
> metric value with its baseline. A finding you cannot support with a quoted artifact must
> not be filed.
>
> **Never assert a runtime consequence you have not observed.** "This will throw at
> runtime", "this may leak", "this could fail" are forbidden unless a log line, restart
> count or error metric in the bundle shows it already happened.
>
> **Every flagged shift must be checked against the change context** before filing. If a
> deploy or merge in the preceding 72h plausibly explains it, report it as expected and
> attributed — do not file.

And delete the dead thresholds: `Any 5xx errors` (impossible by design), and the absolute
`10,000 lines/day` (replaced by baseline ratios).

### 3.8 Per-bot applicability

- **alita**: `roles: [monitor, sre]`. No product agent — 13 messages ever. Monitor runs in
  `dormant` mode: liveness, restarts, error lines, LLM cost. Also **add alita to the
  `BotNotAvailable` and `ErrorLogsDetected` alert rules** — it is currently excluded from
  both, so a crash-loop pages nobody.
- **vahter**: product agent works from `event` domain series + `MessageReceived` text.
- **coupon**: unchanged in substance, but chat mining promoted over `/feedback` triage.

---

## 4. Rollout

### Phase 0 — stop the lying (small, do first)

1. `gather-metrics.sh`: remove the failure-swallowing stub; abort on unreachable source.
2. Fix the SRE trigger (§3.5). Seven ignored deploy failures are the standing cost.
3. Add alita to both Grafana alert rules.
4. Add AlitaBot to `AGENTS.md` (currently documents a two-bot repo).
5. Delete or wire `review.md`.

### Phase 1 — parameterize

6. `.github/bots.yml`; rewrite gatherers to take `<bot>`.
7. `product.yml` / `project.yml` loop over bots **in a single job** — the `aks-vpn`
   concurrency group is single-occupancy, so a `strategy.matrix` would serialize behind
   itself and re-establish the tunnel per bot. One VPN session, gather all bots, then one
   agent invocation per bot.
8. Per-bot labels (`bot:vahter`) on every issue.

### Phase 2 — make anomalies computable

9. `agent-state` branch + backfill 4 weeks from Loki/Prometheus.
10. Deploy annotations in `_bot-deploy.yml`.
11. New `monitor` role, 4×/day, baseline-relative with change correlation.
12. Strip runtime responsibility out of `project.md`.

### Phase 3 — quality

13. Fingerprint dedup + `suppressions.json`; remove `NOTE(project-agent):` comments.
14. Rewrite prompts against §3.7; delete dead thresholds.
15. Wire the MCP servers (`grafana`, `*-db`) into the agent invocation so it can **drill**
    rather than read a frozen blob. The two findings that worked were both cases where the
    needed evidence happened to already be in the bundle; with MCP that stops being luck.

### Phase 4 — optional, unlocks a new signal class

16. Persist vahter admin-channel messages (`Bot.fs:1549`). This is the only place where a
    small code change opens an entirely new feedback source — constant moderator commentary
    that today exists only inside Telegram.

---

## 5. Open decisions

1. **Model.** Currently `gpt-5-mini`, `effort: low` on the daily project sweep. The monitor
   role wants higher effort and probably a stronger model; the repo-wide sweep does not.
   Worth deciding per role rather than per workflow.
2. **Vahter product vision.** `product.md` treats `PRODUCT-VISION.md` as law, and only
   coupon has one. Either write one for vahter or make the product prompt degrade cleanly
   without it.
3. **coupon zero-error baseline.** One source read to confirm exceptions aren't swallowed
   before trusting `0 errors/7d` as the baseline.
4. **Monitor cadence.** 4×/day is a starting point. Vahter's traffic could justify hourly;
   alita monthly would do.

Model selection is resolved in §10.

---

## 6. Role specifications

Each role gets: a cadence, an evidence bundle it is guaranteed to receive, an output
contract, and a refusal condition. The refusal condition is the important part — it is what
makes "I couldn't tell" an available answer instead of a forced verdict.

### 6.1 `monitor` — runtime anomaly watch

**Cadence** every 4h per bot (`traffic_class: dormant` → daily).
**Scope** exactly one bot per invocation. Never repo-wide.

**Evidence bundle** (`scripts/gather/runtime.sh <bot>`):

| Block | Content |
|---|---|
| `sources` | Reachability manifest — prometheus / loki / argocd / postgres, each `ok` or the exact error |
| `series` | For each tracked series: current window, 7d median, 28d median, ratio, z-score |
| `errors` | Every Loki `level="Error"` line in the window, verbatim, with `@t`, `SourceContext`, `@tr`, exception head. Grouped by `SourceContext` + message prefix (Loki's `/patterns` API 404s here) |
| `warnings` | Same, `level="Warning"`, counts + one sample per group |
| `pods` | ArgoCD sync/health, replica counts, restart counts, waiting reasons, memory/CPU |
| `change_context` | Merges to `main` touching `source_dir`, ArgoCD deploy history, Grafana `deploy` annotations — preceding 72h |
| `known` | Open + closed finding fingerprints for this bot, plus `suppressions.json` |

**Tracked series per bot** — the concrete ones, all verified against live data:

*vahter* (from `event`, **never** the legacy tables):
`MessageReceived`/day, unique `userId`/day, `UserBanned`/day, `VahterActed`/day,
`LlmClassified` by verdict/day, `MlScoredMessage` where `isSpam`/day, `CallbackCreated` vs
`CallbackResolved`/day, `MessageMarkedHam`+`MessageMarkedSpam`/day (ML correction rate),
Loki lines/day, Loki `level="Error"`/day.
Recent 7d actuals for calibration: messages 2002/2593/2947/2349/2145/1323/1424, bans
46/70/50/54/34/28/26, LLM SPAM verdicts 22/28/32/29/18/11/14.

*coupon* (from `coupon_event`, the dense signal — **not** `chat_message`, which is bursty
and has zero-days):
`added`/`taken`/`used`/`returned`/`voided` per day, unique `user_id`/day,
`chat_message`/day, `user_feedback`/week, new `user` rows/30d.
Recent 7d actuals: added 78, taken 71, used 47, returned 24, voided 6, feedback **0**.

*alita* (`dormant` — liveness, not volume): pod ready, restarts, `level="Error"` count,
`llm_usage` cost/day, `message_log` rows/day *reported without judgement* (0 is normal).

**Detection rules.** Fire only on a baseline-relative condition, never an absolute constant:

- any `level="Error"` group whose count exceeds its 28d median by ≥3σ, **or** any error group
  with a `SourceContext` not seen in 28 days (new failure mode — the highest-value signal)
- any series at ratio ≤0.4 or ≥2.5 vs 7d median, sustained across ≥2 consecutive windows
- restarts > 0, or replicas available < desired, or ArgoCD health ≠ Healthy
- `dormant` bots: **only** the pod/restart/error rules. Volume rules are disabled — alita
  logs 0 lines on 8 of 14 days and would otherwise fire constantly.

**Change correlation is mandatory.** For every candidate finding, the agent must check
`change_context` and classify: `attributed` (a deploy/merge plausibly explains it — report
in summary, do not file) or `unexplained` (file). The vahter PR #213 log-volume step is the
canonical `attributed` case.

**Refusal condition.** If `sources` shows any required source not `ok`, the workflow does not
invoke the agent at all. If the agent finds itself reasoning about a series with fewer than
14 days of baseline, it must label the finding `low-confidence: insufficient baseline` rather
than filing it.

**Output contract.** At most one issue per fingerprint. Labels: `monitor`, `bot:<name>`,
`anomaly`, plus a priority. Body must contain a verbatim artifact and the baseline comparison
that triggered it. A P1 (no healthy replicas, or sustained error burst) additionally
dispatches the `sre` role.

### 6.2 `product` — user signal

**Cadence** 2×/week per bot where `product` is in `roles`. Currently coupon and vahter.
**Scope** one bot per invocation.

**Evidence bundle** (`scripts/gather/product.sh <bot>`):
last-14-day user-authored text with timestamps/chat/user, domain funnel series with 7d/28d
baselines, feature-usage counters from Prometheus, open `feature-request`/`bug` fingerprints,
and the bot's `product_vision` file if it has one.

**Per-bot text source** — this is where the current agent would silently read stale data:

- vahter → `event` where `event_type='MessageReceived'`, text at `data->>'text'`.
  **The legacy `message` table froze on 2026-04-02.**
- coupon → `chat_message.text` (primary) + `user_feedback.feedback_text` (rare).
- alita → not run.

**Emphasis change.** Chat mining is the primary path; `/feedback` triage is secondary and
must degrade to a no-op when empty rather than being the opening step. Rationale: 4 feedback
rows all-time, none since 2026-05-10, against a live community chat where users actually
complain. The single best product finding in the corpus (#266 → PR #267, shipped same day)
came from quoting chat verbatim.

**Refusal condition.** If the bot has `< 20` user-authored messages in the window, emit
"insufficient signal" and file nothing. This is what keeps a future dormant bot from
generating filler.

**Vahter-specific gap.** Vahter has no `product_vision` and no persisted moderator chat, so
its product agent works from domain series only (ban rates, ML/LLM verdict mix, correction
rate) plus monitored-chat text. Both gaps are addressable — §3.8 and Phase 4.

### 6.3 `project` — repo health

**Cadence** daily, repo-wide, one invocation.
**Scope** the monorepo. **No runtime responsibility** — that moved to `monitor`.

Owns: stale docs contradicting code, dead code, config/`bot_setting` drift, `TODO`/`FIXME`
in shipped code, missing tests for paths that actually failed at runtime (cross-referenced
from `monitor` findings), and dependency/migration hygiene.

Keeps the existing "Do NOT file" blocklist and the clean-day rule. Drops every metrics
threshold, the Loki query section, and the pod-health summary template — those are
`monitor`'s job now and their presence is what produced #222 and #271.

`effort: low` and the cheap model remain correct here.

### 6.4 `sre` — incident response

**Trigger** called directly by `_bot-deploy.yml` on failure (§3.5), plus `workflow_dispatch`,
plus dispatch from a `monitor` P1.
**Scope** one bot, one incident.

The existing runbook in `sre.md` is good and mostly survives. Required edits:

- **Delete the 5xx health gate.** `sum(rate(http_server_request_duration_seconds_count{...
  status_code=~"5.."}))` is always 0 — the webhook returns 200 regardless of internal
  exceptions. Replace with Loki `level="Error"` rate as the primary "is it actually broken"
  signal.
- Replace `APP_NAME`/`CONTAINER`/`IMAGE_NAME` placeholder substitution with values passed as
  workflow inputs from `bots.yml`.
- Add a guaranteed cleanup step: if the agent disabled ArgoCD auto-sync and the run ends for
  any reason, an `if: always()` step re-enables it. Today a killed run leaves auto-sync off
  with only a prose reminder.
- Remove the hardcoded `Szer` GHCR owner and `Szer/my-infra` strings; read from config.

---

## 7. File layout

```
.github/
  bots.yml                        NEW — the registry
  AGENT-FLOWS-REDESIGN.md         this document
  workflows/
    _agent-runner.yml             NEW — reusable: VPN up, gather, guard, invoke, close
    _sre-agent.yml                NEW — reusable, called by _bot-deploy.yml on failure
    monitor.yml                   NEW — schedule, loops bots
    product.yml                   REWRITTEN — loops bots with role `product`
    project.yml                   REWRITTEN — repo-wide, runtime sections removed
    sre.yml                       DELETED — replaced by _sre-agent.yml
  prompts/
    monitor.md                    NEW
    product.md                    REWRITTEN
    project.md                    TRIMMED
    sre.md                        EDITED per §6.4
    review.md                     WIRE OR DELETE — currently orphaned
    _shared/
      non-interactive.md          NEW — the boilerplate currently copy-pasted verbatim
      evidence-discipline.md      NEW — the §3.7 rules
      issue-hygiene.md            NEW — fingerprints, dedup, labels
scripts/
  gather/
    lib.sh                        NEW — probe(), fail-loud helpers, manifest emitter
    runtime.sh                    NEW — replaces gather-metrics.sh
    product.sh                    NEW — replaces gather-product-data.sh
    baseline.sh                   NEW — reads/writes agent-state, computes ratios + z-scores
  queries/
    vahter/*.sql   coupon/*.sql   alita/*.sql        NEW — per-bot query sets
```

Prompt composition: each role prompt is `cat _shared/*.md role.md` at build time, so the
boilerplate that is currently duplicated byte-for-byte across `product.md` and `project.md`
lives in one place.

---

## 8. Workflow sketch

`_agent-runner.yml`, the reusable core all scheduled roles call:

```yaml
on:
  workflow_call:
    inputs:
      role:  { type: string, required: true }   # monitor | product | project
      bots:  { type: string, required: true }   # JSON array, or ["_repo"] for project
      model: { type: string, required: true }
      effort:{ type: string, default: medium }

concurrency:
  group: aks-vpn            # single-occupancy peer — one job, never a matrix
  cancel-in-progress: false

jobs:
  run:
    runs-on: ubuntu-latest
    permissions: { contents: read, issues: write }
    steps:
      - uses: actions/checkout@v4
      - run: scripts/setup-vpn.sh              # once, for all bots
      - id: state
        run: git fetch origin agent-state && git worktree add /tmp/state origin/agent-state

      - id: gather                              # loop, not matrix — see §4 note
        run: |
          set -euo pipefail
          for bot in $(echo '${{ inputs.bots }}' | jq -r '.[]'); do
            scripts/gather/${{ inputs.role }}.sh "$bot" > "/tmp/evidence-$bot.json"
          done

      - id: guard                               # THE anti-blindness gate
        run: |
          set -euo pipefail
          for f in /tmp/evidence-*.json; do
            bad=$(jq -r '.sources | to_entries[] | select(.value != "ok") | .key' "$f")
            if [ -n "$bad" ]; then
              echo "::error::unreachable sources in $f: $bad"
              scripts/report-degraded.sh "$f"   # bumps one issue, does NOT invoke the agent
              exit 1
            fi
          done

      - id: agent
        run: |
          for bot in $(echo '${{ inputs.bots }}' | jq -r '.[]'); do
            scripts/run-agent.sh "${{ inputs.role }}" "$bot" "${{ inputs.model }}" "${{ inputs.effort }}"
          done

      - if: always()
        run: |
          scripts/gather/baseline.sh --commit    # append today's rollups to agent-state
          sudo wg-quick down wg0 || true
```

Two deliberate choices:

- **`for` loop, not `strategy.matrix`.** The `aks-vpn` concurrency group is single-occupancy;
  matrix jobs would queue behind each other and tear down/re-establish the tunnel per bot.
- **The guard step exits non-zero and never reaches the agent.** A red workflow run is the
  correct outcome of a degraded evidence pipeline. Today it is a green run with an empty
  report and a "clean day" verdict.

---

## 9. What gets deleted

Being explicit, because dead logic is what produced the bad issues:

| Deleted | Why |
|---|---|
| `Any 5xx errors` threshold (project.md, sre.md) | Structurally impossible — webhook always returns 200 |
| `Log volume above 10,000 lines/day` | Absolute; vahter sits permanently just under it |
| `Memory above 256 MB` as a *filing* trigger | Produced #271 with 0 restarts/0 errors; becomes a series with a baseline instead |
| `|| echo '{"data":{"result":[]}}'` in gather-metrics.sh | The blindness bug |
| `NOTE(project-agent):` comments in `.fs` files | Replaced by `suppressions.json` |
| `on: issues: [labeled]` in sre.yml | Never fires — `GITHUB_TOKEN` anti-recursion |
| Metrics/Loki/pod sections of project.md | Moved to `monitor` |
| `user-feedback` triage as the opening step of product.md | Demoted — 0 rows since 2026-05-10 |
| Hardcoded community-member hashes in product.md | Coupon-specific trivia in a shared prompt; move to `bots.yml` or drop |

---

## 10. Model & Azure Foundry plan

### 10.1 The constraint is narrower than it looked

The premise "Sweden Central has no capacity for better models, East US 2 needs a quota
application" is true **for the models it was tested against** — and those were the hardest
possible cases. `terraform/alita-foundry-eus2.tf:5-11` records the denials, and they were for
`gpt-image-2`, `gpt-image-1-mini`, and `gpt-5.6-sol`: two image-generation models and a
model released the same week. Those are exactly the SKUs where regional GPU capacity is
genuinely scarce.

A mid-tier reasoning model is a different request. Per Microsoft's region-availability
matrix (`models-sold-directly-by-azure-region-availability`, page dated 2026-07-24),
`gpt-5.2` and `gpt-5.4` are both listed **GlobalStandard in Sweden Central** — the region
`szer-foundry` already lives in.

Two things also need separating, because conflating them is what makes this look harder
than it is:

- **Quota** is subscription-level, per region, per model, tier-based, and granted
  automatically. `gpt-5.2` and `gpt-5.4` both carry **10,000 RPM / 1,000,000 TPM at Tier 1**.
- **Capacity** is Azure's physical ability to accept a new deployment in a region right now.
  That is what the image-model requests hit.

For a monitor role running ~18 invocations/day, Tier 1 default quota is roughly three orders
of magnitude more than needed. **No quota-increase application is likely required at all.**

### 10.2 GlobalStandard already routes globally

Every deployment in `my-infra` is already `GlobalStandard`. Per
`foundry-models/concepts/deployment-types`, GlobalStandard "provides the highest default
quota and eliminates the need to load balance across multiple resources" and dynamically
routes inference to whichever datacenter has availability. The resource must live in *a*
region, but inference is not pinned to it.

This means the second-region plan from §6 of the infra map — new account, new outputs, new
GitHub secret names, workflow edits — is **probably unnecessary**.

### 10.3 Recommended change: one Terraform block

Add a `gpt-5.2` (or `gpt-5.4`) GlobalStandard deployment to the **existing** `szer-foundry`
account, pattern-copied from `terraform/foundry.tf:76-99`:

```hcl
resource "azapi_resource" "gpt_5_2_agent" {
  provider                  = azapi.credits50
  type                      = "Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview"
  name                      = "gpt-5-2-agent"
  parent_id                 = azapi_resource.foundry.id
  schema_validation_enabled = false
  depends_on                = [azapi_resource.alita_tts]   # keep the serialization chain

  body = {
    sku = { name = "GlobalStandard", capacity = 100 }
    properties = {
      model                = { format = "OpenAI", name = "gpt-5.2", version = "2025-12-11" }
      versionUpgradeOption = "OnceNewDefaultVersionAvailable"
      raiPolicyName        = azapi_resource.agent_rai_policy.name
    }
  }
}
```

Consequences:

- **No new account, no new region, no new secrets.** Same endpoint, same key — the existing
  `AZURE_OPENAI_BASE_URL` / `AZURE_OPENAI_API_KEY` continue to work untouched.
- The only change in this repo is `model: gpt-5-2-agent` on the monitor/SRE steps.
  `project` and `product` stay on `gpt-5-mini`.
- A distinct deployment name means its own carved-out capacity — the monitor role never
  contends with the existing `gpt-5-mini` deployment, which is the established pattern in
  `foundry.tf` / `mafia-ai-foundry.tf` / `alita-foundry.tf` and the reason
  `mafia-gpt-5-mini` was split off in the first place.

**This is a cheap experiment, not a commitment.** If Sweden Central refuses the deployment,
`terraform apply` fails with `InsufficientQuota` and nothing else is affected. The my-infra
repo already establishes empirical testing as the way to learn real limits — see the TTS
note at `alita-foundry.tf:184-191`: *"the model-list `maxCapacity: 9999` field does not
reflect the actual per-subscription RPM quota — verified empirically, not from listing."*

Fallback ladder if it does refuse:

1. Try `gpt-5.4` instead (also GlobalStandard-listed in Sweden Central).
2. Deploy to the existing `szer-foundry-eus2` account in East US 2 — it already exists;
   this then needs the new-outputs + new-secret-names work from the infra map's §6.
3. Only then file a quota-increase request.

### 10.4 Models to avoid

- **`gpt-5.5`** — default quota is **0 RPM / 0 TPM below Tier 5**. It would force exactly the
  application friction you want to avoid.
- **`gpt-5.6-sol/terra/luna`** — released 2026-07-09, and the family already denied in Sweden
  Central per your own Terraform comment. Regional capacity for a three-week-old model is the
  worst case, not the best.
- **`gpt-5-pro`** — much lower quota, higher price, `reasoning_effort` locked to `high`.

### 10.5 Claude on Foundry — GA, but not drop-in

Anthropic's models went **GA in Microsoft Foundry on 2026-07-24** (Opus 5, Sonnet 5,
Haiku 4.5), GlobalStandard in Sweden Central and East US 2 among others.

They are **not usable with the current setup without adapter work.** They are served via the
native Anthropic Messages API (`POST /anthropic/v1/messages` on
`<resource>.services.ai.azure.com`), not the OpenAI Responses API that
`openai/codex-action@v1` speaks. Billing is a Claude Consumption Unit meter rather than a
token line item — still drawn from Azure credits.

Same applies to Grok, DeepSeek, Llama, Mistral, Cohere: all available on Foundry, none
expose a Responses API. **Azure OpenAI's gpt-5.x/o-series is the only Responses-compatible
family on Foundry today.** Moving off it means forking the action or running a translation
proxy — that is a separate project, not part of this one.

### 10.6 Per-role model assignment

| Role | Model | Effort | Rationale |
|---|---|---|---|
| `monitor` | `gpt-5.2` (new deployment) | `high` | Judgement calls: is this error group a new failure mode or a known variant; does a deploy in the 72h window explain this shift |
| `sre` | `gpt-5.2` (same deployment) | `high` | Already `effort: high`; incident response with write access deserves the better model |
| `product` | `gpt-5-mini` | `medium` | Reading chat text and quoting it — mini handles this; #266 proves it |
| `project` | `gpt-5-mini` | `low` | Repo sweep, unchanged |

### 10.7 Sequencing — do this last, not first

**The model is not the bottleneck and the audit says so.** `gpt-5-mini` could not have
detected a baseline shift it was never given a baseline for, could not have seen vahter when
the gatherer only queried coupon, and could not have run at all when the workflow never
triggered. Phases 0–2 fix all three without touching a model.

Recommended order: ship Phases 0–2 on `gpt-5-mini`, let baselines warm for two weeks, then
look at what the monitor role still gets wrong. If the residual misses are reasoning-shaped,
add the `gpt-5.2` deployment — one HCL block, reversible. If they are evidence-shaped, spend
the effort on wiring the MCP servers (Phase 3, item 15) so the agent can drill into Grafana
and Postgres rather than reading a frozen blob. My expectation is the latter matters more,
but two weeks of real monitor output will settle it rather than either of us guessing.

### 10.8 Unverified

- **Pricing for `gpt-5.1` and later is not published** anywhere retrievable — the Azure
  pricing page renders JS placeholders. `gpt-5` is ~$1.25/$10.00 per 1M in/out vs
  `gpt-5-mini` at ~$0.25/$2.00 (third-party source). Check the Foundry portal's Pricing tab
  before deploying. At ~18 monitor runs/day this is small either way, but it should be a
  known number, not an assumed one.
- **Real-time Sweden Central capacity for `gpt-5.2`** is only knowable from the Foundry
  portal's Quota page or the Model Capacities API. The Terraform apply is itself the test.
- Quota-increase turnaround has no published SLA.

---

## 11. What I need from you

1. **Approve Phase 0** (5 small fixes — un-swallow `gather-metrics.sh`, rewire the SRE
   trigger, add alita to the two alert rules, add AlitaBot to `AGENTS.md`, wire-or-delete
   `review.md`). Independently useful whatever happens to the rest.
2. **Confirm the four-role split** (§3) before I build `bots.yml` — everything downstream
   assumes it.
3. **Decide on Phase 4** (persisting vahter's moderator-channel messages). It is the only
   item requiring a bot code change, and it unlocks the only qualitative feedback source
   vahter has.
4. **Model: defer.** §10.7 — revisit after two weeks of real monitor output.

Open, lower-priority: whether vahter gets a `PRODUCT-VISION.md`, and a one-time source read
to confirm coupon's zero-error baseline is real rather than swallowed exceptions.
