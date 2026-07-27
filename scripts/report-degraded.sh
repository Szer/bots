#!/usr/bin/env bash
# report-degraded.sh <evidence-json-file>
#
# Called from the workflow's guard step when a gatherer's manifest shows a
# non-"ok" source. Bumps (or creates) a single `evidence-pipeline-degraded`
# issue recording which bot/source is unreachable, INSTEAD of ever invoking
# an agent on a degraded evidence bundle — an LLM must never be handed a
# bundle of zeros and asked whether things look fine
# (AGENT-FLOWS-REDESIGN.md §3.2, §8).
#
# Required env vars:
#   GITHUB_REPOSITORY    e.g. Szer/bots
#   GH_TOKEN              gh CLI auth
set -euo pipefail

FILE="${1:?usage: report-degraded.sh <evidence-json-file>}"
REPO="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"

log() { echo "[$(date -u +%H:%M:%S)] $*" >&2; }

BOT=$(jq -r '.bot // "unknown"' "$FILE")
BAD=$(jq -r '.sources | to_entries[] | select(.value != "ok") | "\(.key): \(.value)"' "$FILE")

log "Reporting degraded evidence pipeline for bot=${BOT}: ${BAD}"

# Idempotent — a no-op if the label already exists.
gh label create "evidence-pipeline-degraded" --repo "$REPO" --color "b60205" \
    --description "Evidence gatherer could not reach a required source; the agent was not invoked for this run." \
    --force >/dev/null 2>&1 || true

RUN_URL="${GITHUB_SERVER_URL:-https://github.com}/${REPO}/actions/runs/${GITHUB_RUN_ID:-unknown}"

MANIFEST_RAW=$(cat "$FILE")

BODY_FILE="$(mktemp)"
trap 'rm -f "$BODY_FILE"' EXIT
cat > "$BODY_FILE" <<BODY
**Bot:** ${BOT}
**Run:** ${RUN_URL}

**Unreachable source(s):**
\`\`\`
${BAD}
\`\`\`

**Manifest (verbatim):**
\`\`\`json
${MANIFEST_RAW}
\`\`\`

The agent was **not invoked for this bot** this run — an LLM must never be
handed a bundle of zeros and asked whether things look fine. Other bots in
the same run are evaluated independently and are unaffected by this one's
degraded evidence.
BODY

EXISTING=$(gh issue list --repo "$REPO" --label "evidence-pipeline-degraded" --state open \
    --json number --jq '.[0].number // empty')

if [ -n "$EXISTING" ]; then
    log "Bumping existing evidence-pipeline-degraded issue #${EXISTING}"
    gh issue comment "$EXISTING" --repo "$REPO" --body-file "$BODY_FILE"
else
    log "Creating new evidence-pipeline-degraded issue"
    gh issue create --repo "$REPO" --title "Evidence pipeline degraded" \
        --label "evidence-pipeline-degraded" --body-file "$BODY_FILE"
fi
