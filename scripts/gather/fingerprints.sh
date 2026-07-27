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
#     "open":   [ {"number": 123, "fingerprint": "vahter/monitor/..."}, ... ],
#     "closed": [ {"number": 98,  "fingerprint": "vahter/monitor/..."}, ... ],
#     "suppressed": [ "vahter/monitor/...", ... ]
#   }
#
# Mechanical rules for the agent consuming this (see .github/prompts/monitor.md):
#   - fingerprint appears in "open"       -> comment on that issue number, never open a new one
#   - fingerprint appears in "suppressed" -> stay silent, do not file or comment
#   - fingerprint appears in "closed" only, not suppressed -> a new occurrence of a
#     previously-resolved problem; treat as a REGRESSION, safe to re-open/re-file
#   - fingerprint appears nowhere -> new finding, allowed, must justify why it's not a variant
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

log "Fetching open monitor fingerprints for bot=${BOT}..."
OPEN_JSON=$(extract_fingerprints "open" | jq -s '.')

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
