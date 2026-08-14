#!/usr/bin/env bash
# fingerprints.sh <bot> <state-dir>
#
# Pre-fetches every known monitor-role fingerprint for <bot> — open issues,
# closed issues, and the permanent suppressions list — so the monitor agent
# never has to guess whether a candidate finding is a duplicate. See
# AGENT-FLOWS-REDESIGN.md §3.6 and deliverable 5 of the Phase 2 brief:
# dedup is mechanical, not left to LLM judgment.
#
# Every finding issue this role files carries, verbatim in its body:
#   <!-- agent-fingerprint: <bot>/monitor/<stable-key> -->
# This script greps that HTML comment out of every `monitor` + `bot:<bot>`
# labelled issue (open AND closed — a closed issue's fingerprint still
# means "seen before", see the rules below) and combines it with
# <state-dir>/suppressions.json.
#
# Output (stdout): one JSON object —
#   {
#     "open":   [ {"number": 123, "fingerprint": "vahter/monitor/...",
#                  "mechanical_signal_available": true,
#                  "last_active_at": "2026-08-13T20:34:57Z",
#                  "days_since_last_active": 2,
#                  "consecutive_clean_days": 2}, ... ],
#     "closed": [ {"number": 98,  "fingerprint": "vahter/monitor/..."}, ... ],
#     "suppressed": [ "vahter/monitor/...", ... ]
#   }
#
# Mechanical rules for the agent consuming this (see .github/prompts/monitor.md):
#   - fingerprint appears in "open"       -> update that issue, subject to the commenting
#     throttle in monitor.md (never open a second issue for the same fingerprint, and never
#     comment when nothing changed)
#   - fingerprint appears in "suppressed" -> stay silent, do not file or comment
#   - fingerprint appears in "closed" only, not suppressed -> a new occurrence of a
#     previously-resolved problem; treat as a REGRESSION, safe to re-open/re-file
#   - fingerprint appears nowhere -> new finding, allowed, must justify why it's not a variant
#
# ─── "open" entries also carry mechanically-computed activity fields ────────
#
# 2026-08-14 fix (issues #371/#358 postmortem — the monitor agent commented on
# every fingerprint-matched open issue on EVERY 4-hourly run, even when
# nothing had changed, because it had no mechanical way to tell "still
# active" from "confirmed clean N days ago" and so could never itself verify
# monitor.md's 7-consecutive-clean-day close bar — it just kept saying
# "I cannot confirm 7+ consecutive days back-to-baseline from this run
# alone" forever). These fields close that gap:
#
#   mechanical_signal_available - true once at least one comment (or the
#       issue body itself) on this issue carries an explicit
#       `<!-- agent-status: active|clean|closed -->` marker (see
#       monitor.md's commenting-throttle section — every comment/issue body
#       the agent writes for a fingerprinted finding must carry one). false
#       for an issue that predates this mechanism or has no comments yet —
#       the other three fields are null in that case and the agent must fall
#       back to "insufficient mechanical history", exactly like the old
#       conservative behavior, rather than guess.
#   last_active_at            - timestamp (ISO8601) of the most recent
#       `agent-status: active` marker in this issue's timeline (the issue
#       body itself counts as the first timeline entry, defaulting to
#       "active" if it carries no explicit marker — a finding is by
#       definition active at the moment it is filed).
#   days_since_last_active    - whole days between `last_active_at` and now.
#       This is the mechanical input to monitor.md's "Close stale findings"
#       7-day bar: `days_since_last_active >= 7` is fileable-for-auto-close
#       ONLY when combined with this run's own live evidence also
#       independently confirming clean (see monitor.md — never close on this
#       field alone if today's evidence disagrees).
#   consecutive_clean_days    - count of distinct calendar days (UTC, from
#       marker timestamps) with an `agent-status: clean` marker strictly
#       after `last_active_at`. Informational context for the agent's
#       summary; `days_since_last_active` is the authoritative field.
#
# Derivation: the issue's own comment history is the simplest robust source
# available to a per-issue, per-fingerprint script — the alternative
# considered (the `agent-state` orphan-branch history baseline.sh writes) is
# a per-bot *series* rollup with no per-fingerprint/per-issue axis at all, so
# it cannot answer "when was THIS finding last confirmed active". Comment
# timestamps come straight from the GitHub API (never parsed from LLM free
# text), and the status is read from an explicit, mechanically-written
# marker the agent itself is now required to include — not inferred from
# freeform prose, which varies wildly comment-to-comment (see the real
# #371/#358 comment history sampled while designing this fix: every comment
# quotes the required evidence, in a different format each time). Before any
# `agent-status` markers exist in an issue's history (either because it
# predates this fix, or because no comment has been posted on it yet since
# this fix shipped), the issue's own creation is used as the sole timeline
# anchor — "active" by default (a finding is active when filed) unless the
# issue body itself already carries an explicit marker. This is a
# deliberately safe default: it only ever makes `days_since_last_active`
# UNDERCOUNT (never overcount), so it can never cause a premature auto-close
# — the day count only starts moving once a real `clean` marker is posted.
#
# Required env vars: GITHUB_REPOSITORY, GH_TOKEN (gh CLI auth).

set -euo pipefail

BOT="${1:?usage: fingerprints.sh <bot> <state-dir>}"
STATE_DIR="${2:?usage: fingerprints.sh <bot> <state-dir>}"

# shellcheck source=scripts/gather/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

REPO="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"

# gh issue list body search returns the FULL body; extract the fingerprint
# HTML comment (there should be exactly one per issue — issues without one
# predate this mechanism, or aren't monitor findings, and are skipped).
extract_fingerprints() {
    local state="$1"
    gh issue list --repo "$REPO" --label "monitor" --label "bot:${BOT}" --state "$state" \
        -L 500 --json number,body \
        --jq '.[] | {number: .number, body: .body}' \
    | jq -c '
        (.body | capture("<!--\\s*agent-fingerprint:\\s*(?<fp>[^\\s]+)\\s*-->").fp) as $fp
        | {number: .number, fingerprint: $fp}
      ' 2>/dev/null || true
}

# extract_open_fingerprints_with_activity — same fingerprint extraction as
# extract_fingerprints(), but ALSO fetches each open issue's comment history
# (one combined `gh issue list --json ...,comments` call, not N+1 per-issue
# calls) and computes the mechanical activity fields documented above. Kept
# separate from extract_fingerprints() because "closed"/suppressions never
# need these fields (see monitor.md — only known.open feeds the stale-sweep
# 7-day check).
extract_open_fingerprints_with_activity() {
    local now_iso
    now_iso="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

    gh issue list --repo "$REPO" --label "monitor" --label "bot:${BOT}" --state "open" \
        -L 500 --json number,body,createdAt,comments \
    | jq -c --arg now "$now_iso" '
        ($now | fromdateiso8601) as $now_epoch
        | .[]
        | . as $issue
        | (try ($issue.body | capture("<!--\\s*agent-fingerprint:\\s*(?<fp>[^\\s]+)\\s*-->").fp) catch null) as $fp
        | select($fp != null)
        # Explicit status marker on the issue BODY itself, if the "Creating
        # a finding issue" template already wrote one (it does, as of this
        # fix) — null for an issue filed before this fix shipped. The
        # trailing `// null` is load-bearing: `capture` on a non-matching
        # string produces an EMPTY stream (not an error, not `null`), so
        # without it a body with no marker would make this whole `as`
        # binding produce zero outputs and silently drop the entire issue
        # from the result — not just leave the field null.
        | ((try ($issue.body | capture("<!--\\s*agent-status:\\s*(?<s>active|clean|closed)\\s*-->").s) catch null) // null) as $body_status
        # Every comment carrying an explicit marker, in API (chronological)
        # order. Comments without a marker (freeform pre-fix chatter, or a
        # human reply) are simply not part of the mechanical timeline.
        | ([ ($issue.comments // [])[]
              | (try (.body | capture("<!--\\s*agent-status:\\s*(?<s>active|clean|closed)\\s*-->").s) catch null) as $st
              | select($st != null)
              | {ts: .createdAt, status: $st}
           ]) as $comment_markers
        # marker_count counts only EXPLICIT markers (body + comments) — this
        # is what mechanical_signal_available reports on. The synthetic
        # creation-defaults-to-active fallback below is NOT counted here, so
        # an issue with zero explicit markers anywhere correctly reports
        # mechanical_signal_available=false.
        | ((if $body_status != null then 1 else 0 end) + ($comment_markers | length)) as $marker_count
        | ([{ts: $issue.createdAt, status: ($body_status // "active")}] + $comment_markers) as $timeline
        | ([$timeline[] | select(.status == "active")] | max_by(.ts).ts) as $last_active_ts
        | ($marker_count > 0 and $last_active_ts != null) as $available
        | {
            number: $issue.number,
            fingerprint: $fp,
            mechanical_signal_available: $available,
            last_active_at: (if $available then $last_active_ts else null end),
            days_since_last_active: (
                if $available
                then (($now_epoch - ($last_active_ts | fromdateiso8601)) / 86400 | floor)
                else null end
            ),
            consecutive_clean_days: (
                if $available
                then ([$timeline[] | select(.status == "clean" and .ts > $last_active_ts) | .ts[0:10]] | unique | length)
                else null end
            )
          }
      ' 2>/dev/null || true
}

log "Fetching open monitor fingerprints (+ activity signal) for bot=${BOT}..."
OPEN_JSON=$(extract_open_fingerprints_with_activity | jq -s '.')

log "Fetching closed monitor fingerprints for bot=${BOT}..."
CLOSED_JSON=$(extract_fingerprints "closed" | jq -s '.')

SUPPRESSIONS_FILE="${STATE_DIR}/suppressions.json"
if [ -f "$SUPPRESSIONS_FILE" ]; then
    SUPPRESSED_JSON=$(jq -c '.suppressed // []' "$SUPPRESSIONS_FILE" 2>/dev/null || echo '[]')
else
    log "WARNING: no suppressions.json at ${SUPPRESSIONS_FILE} (first-run case) — treating as empty"
    SUPPRESSED_JSON='[]'
fi

jq -n --argjson open "$OPEN_JSON" --argjson closed "$CLOSED_JSON" --argjson suppressed "$SUPPRESSED_JSON" \
    '{open: $open, closed: $closed, suppressed: $suppressed}'
