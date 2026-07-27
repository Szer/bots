# agent-state

Orphan branch (no shared history with `main`) holding persisted baseline
history for the `monitor` agentic role. See
`.github/AGENT-FLOWS-REDESIGN.md` §3.3/§3.6/§6.1 and
`scripts/gather/baseline.sh` on `main` for the reader/writer.

## Layout

```
<bot>/<YYYY-MM>.jsonl   one JSON object per line, one line per monitor run
                        (see schema below); backfilled days use
                        "run_id":"backfill" / "origin":"backfill" and are
                        one line per UTC calendar day instead of per run.
suppressions.json       fingerprints permanently silenced (closed as
                         invalid/wontfix/by-design) — see §3.6.
```

## JSONL line schema

```json
{
  "bot": "vahter",
  "ts": "2026-07-27T08:00:00Z",
  "run_id": "1234567890",
  "series": { "messages_received_24h": 1424, "...": 0 },
  "error_groups": { "SourceContext|message prefix": 2 },
  "pods": { "restarts": 0, "ready_replicas": 1, "desired_replicas": 1,
            "argocd_sync": "Synced", "argocd_health": "Healthy" },
  "sources": { "prometheus": "ok", "loki": "ok", "argocd": "ok", "postgres": "ok" }
}
```

Every numeric value under `series` is a **trailing-24h** count as of `ts` —
not a calendar-day bucket — so runs at any time of day are directly
comparable (see `scripts/gather/baseline.sh` header comment). `pods` and
`error_groups` are evaluated directly against DIRECT rules on the live
run (never baseline-relative) — `baseline.sh stats` only computes
trailing-window statistics over `series`.

## suppressions.json

```json
{ "suppressed": ["vahter/monitor/error-burst/SomeStableKey"] }
```

A fingerprint listed here is permanently silenced — the monitor role must
never file or comment about it again (§3.6). Empty on creation.

## Never merge this branch into `main`

It has no shared history — a merge attempt will show every baseline line
as an "addition" to `main`. Only `git worktree add <path> --track
origin/agent-state` / `git fetch` + reading are ever appropriate.
