#!/usr/bin/env bash
# product.sh <bot> — per-bot product-signal evidence gatherer.
# Replaces the old (coupon-only) gather-product-data.sh; reads bot identity
# (metric_prefix, db_name, query_set, product_vision, community_hashes) from
# .github/bots.yml instead of hardcoding it.
#
# Usage: scripts/gather/product.sh <bot>     # bot key from .github/bots.yml
# Only meaningful for bots whose `roles` include `product` (see bots.yml) —
# the caller (product.yml) is responsible for only invoking this for those.
#
# Required env vars:
#   PROMETHEUS_URL        e.g. http://prometheus.internal:9090
#   DB_PROD_HOST / DB_PROD_USERNAME / DB_PROD_PASSWORD
#                         shared Postgres credentials — the fleet uses ONE
#                         set of secrets across all three bot databases and
#                         varies only the database name (bots.yml db_name).
#   GITHUB_REPOSITORY     e.g. Szer/bots (set by GitHub Actions)
#
# Optional env vars:
#   LOKI_URL              e.g. http://loki.internal (for error context)
#
# Output: the manifest JSON line FIRST, then a structured markdown report.
#
# Failure behavior: postgres and prometheus are required sources, each
# probed non-fatally up front via probe() so the manifest can always be
# emitted; loki is optional (same as the Phase-0 gather-product-data.sh) and
# only degrades one report cell with a loud WARNING rather than failing the
# run. If postgres or prometheus is unreachable, the manifest is printed and
# the script exits non-zero — "source unreachable" must never render as
# "everything is fine" (AGENT-FLOWS-REDESIGN.md §1.3).

set -euo pipefail

BOT="${1:?usage: product.sh <bot>  (bot key from .github/bots.yml)}"

# shellcheck source=scripts/gather/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

: "${PROMETHEUS_URL:?PROMETHEUS_URL is required}"
: "${DB_PROD_HOST:?DB_PROD_HOST is required}"
: "${DB_PROD_USERNAME:?DB_PROD_USERNAME is required}"
: "${DB_PROD_PASSWORD:?DB_PROD_PASSWORD is required}"

if ! bot_exists "$BOT"; then
    log "ERROR: bot '${BOT}' is not present in ${BOTS_YML}"
    exit 1
fi

METRIC_PREFIX="$(bot_field "$BOT" '.metric_prefix')"
DB_NAME="$(bot_field "$BOT" '.db_name')"
QUERY_SET="$(bot_field "$BOT" '.query_set')"
PRODUCT_VISION="$(bot_field "$BOT" '.product_vision')"
[ "$PRODUCT_VISION" = "null" ] && PRODUCT_VISION=""

DATABASE_URL="postgresql://${DB_PROD_USERNAME}:${DB_PROD_PASSWORD}@${DB_PROD_HOST}/${DB_NAME}"
REPO="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"

QUERY_DIR="${GATHER_LIB_DIR}/../queries/${QUERY_SET}"
if [ ! -d "$QUERY_DIR" ]; then
    log "ERROR: no query set directory for bot=${BOT} (query_set=${QUERY_SET}, expected ${QUERY_DIR})"
    exit 1
fi

# db_query SQL — runs one statement via psql, tab-separated, unaligned.
# A failed query is fatal (bad SQL / connection dropped mid-run); a
# successful query with zero rows is not, and is returned as-is (empty
# stdout) — that distinction is the whole point of this rewrite.
db_query() {
    local out
    out=$(PGCONNECT_TIMEOUT="${PGCONNECT_TIMEOUT:-5}" \
        psql "${DATABASE_URL}" -v ON_ERROR_STOP=1 -t -A -F $'\t' -f "$1") || {
        log "ERROR: psql query failed (see error above): $1"
        exit 1
    }
    echo "$out"
}

prom_query() {
    retry_curl "Prometheus" -G "${PROMETHEUS_URL}/api/v1/query" --data-urlencode "query=$1"
}

# ─── Preflight connectivity check (non-fatal — builds the manifest) ──────

log "Verifying connectivity for bot=${BOT} (db=${DB_NAME}, query_set=${QUERY_SET})..."

POSTGRES_STATUS=$(probe "postgres" env PGCONNECT_TIMEOUT=5 psql "${DATABASE_URL}" -v ON_ERROR_STOP=1 -t -A -c "SELECT 1;") || true
PROM_STATUS=$(probe "prometheus" curl -sSf --connect-timeout 5 --max-time 20 \
    -G "${PROMETHEUS_URL}/api/v1/query" --data-urlencode "query=vector(1)") || true

LOKI_STATUS="not_configured"
if [ -n "${LOKI_URL:-}" ]; then
    LOKI_STATUS=$(probe "loki" curl -sSf --connect-timeout 5 --max-time 20 \
        -G "${LOKI_URL}/loki/api/v1/labels") || true
fi

MANIFEST=$(emit_manifest "$BOT" \
    "postgres=${POSTGRES_STATUS}" \
    "prometheus=${PROM_STATUS}" \
    "loki=${LOKI_STATUS}")

echo "$MANIFEST"

# Loki is documented-optional — only postgres/prometheus gate the run.
REQUIRED_BAD=$(echo "$MANIFEST" | jq -r '
    .sources
    | to_entries[]
    | select(.key != "loki")
    | select(.value != "ok")
    | .key')

if [ -n "$REQUIRED_BAD" ]; then
    log "ERROR: required source(s) unreachable for bot=${BOT}: ${REQUIRED_BAD} — see manifest above. Not building the rest of the report."
    exit 1
fi

log "Postgres: ok, Prometheus: ok, Loki: ${LOKI_STATUS}"

# ─── Bot usage metrics (Prometheus, metric_prefix-driven) ────────────────

log "Querying bot usage metrics..."

CMD_7D_JSON=$(prom_query "sort_desc(sum by (command)(increase(${METRIC_PREFIX}command_total[7d])))")
CMD_7D=$(echo "$CMD_7D_JSON" | jq -r '
    [.data.result[] | {cmd: .metric.command, count: (.value[1] | tonumber | floor)}]
    | sort_by(-.count)
    | .[] | "| \(.cmd) | \(.count) |"
' 2>/dev/null || true)
[ -z "$CMD_7D" ] && CMD_7D="| (no data) | - |"

CB_7D_JSON=$(prom_query "sort_desc(sum by (action)(increase(${METRIC_PREFIX}callback_total[7d])))")
CB_7D=$(echo "$CB_7D_JSON" | jq -r '
    [.data.result[] | {action: .metric.action, count: (.value[1] | tonumber | floor)}]
    | sort_by(-.count)
    | .[] | "| \(.action) | \(.count) |"
' 2>/dev/null || true)
[ -z "$CB_7D" ] && CB_7D="| (no data) | - |"

INTERACTIONS_7D_JSON=$(prom_query "sum(increase(${METRIC_PREFIX}command_total[7d])) + sum(increase(${METRIC_PREFIX}callback_total[7d]))")
INTERACTIONS_7D=$(echo "$INTERACTIONS_7D_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

INTERACTIONS_PREV_JSON=$(prom_query "sum(increase(${METRIC_PREFIX}command_total[14d])) - sum(increase(${METRIC_PREFIX}command_total[7d])) + sum(increase(${METRIC_PREFIX}callback_total[14d])) - sum(increase(${METRIC_PREFIX}callback_total[7d]))")
INTERACTIONS_PREV=$(echo "$INTERACTIONS_PREV_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

# ─── Domain query set (per-bot .sql files — see scripts/queries/<query_set>/) ──

log "Running domain query set '${QUERY_SET}' (${QUERY_DIR})..."

QUERY_RESULTS=""
for sql_file in "${QUERY_DIR}"/*.sql; do
    [ -e "$sql_file" ] || continue
    name="$(basename "$sql_file" .sql)"
    log "  - ${name}"
    result=$(db_query "$sql_file")
    QUERY_RESULTS="${QUERY_RESULTS}
### ${name}

\`\`\`
${result:-(no rows)}
\`\`\`
"
done

# ─── Error context (Loki, optional) ──────────────────────────────────────

if [ "$LOKI_STATUS" = "ok" ]; then
    log "Querying Loki for user-facing errors..."
    CONTAINER="$(bot_field "$BOT" '.container')"
    if ERROR_COUNT_JSON=$(curl -sf -G \
        --connect-timeout 5 --max-time 20 \
        "${LOKI_URL}/loki/api/v1/query" \
        --data-urlencode "query=sum(count_over_time({container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\"[7d]))"); then
        ERROR_COUNT_7D=$(echo "$ERROR_COUNT_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")
    else
        log "WARNING: Loki query failed after preflight probe succeeded — error count omitted."
        ERROR_COUNT_7D="N/A (Loki query failed — see workflow logs)"
    fi
else
    ERROR_COUNT_7D="N/A (Loki not configured/unreachable — optional source, see manifest)"
fi

# ─── GitHub issues context ────────────────────────────────────────────────

log "Querying GitHub issues..."

OPEN_FEEDBACK=$(gh issue list --repo "$REPO" --label "user-feedback" --state open -L 1000 --json number,title,createdAt \
    --jq 'length') || {
    log "ERROR: gh issue list failed (label=user-feedback, see error above)"
    exit 1
}
OPEN_FEATURES=$(gh issue list --repo "$REPO" --label "feature-request" --state open -L 1000 --json number,title \
    --jq 'length') || {
    log "ERROR: gh issue list failed (label=feature-request, see error above)"
    exit 1
}
OPEN_BUGS=$(gh issue list --repo "$REPO" --label "bug" --state open -L 1000 --json number,title \
    --jq 'length') || {
    log "ERROR: gh issue list failed (label=bug, see error above)"
    exit 1
}

# ─── product_vision (optional — degrades cleanly when a bot has none) ────

VISION_BLOCK="_No product_vision configured for ${BOT} in bots.yml — work from domain series and chat text only._"
if [ -n "$PRODUCT_VISION" ] && [ -f "${GATHER_LIB_DIR}/../../${PRODUCT_VISION}" ]; then
    VISION_BLOCK="See \`${PRODUCT_VISION}\` in the repo checkout (read it directly — not inlined here to avoid double-counting agent context)."
elif [ -n "$PRODUCT_VISION" ]; then
    log "WARNING: bots.yml declares product_vision=${PRODUCT_VISION} for ${BOT} but the file was not found in the checkout"
    VISION_BLOCK="_bots.yml declares \`${PRODUCT_VISION}\` but it was not found in the checkout — treat as absent._"
fi

COMMUNITY_HASHES_JSON=$(bot_field "$BOT" '.community_hashes')
COMMUNITY_HASHES_BLOCK=$(echo "$COMMUNITY_HASHES_JSON" | jq -r '
    if length == 0 then "_No known community-member hashes configured for this bot._"
    else (.[] | "- `\(.hash)` — **\(.role)**: \(.note)") end
')

# ─── Output markdown report ───────────────────────────────────────────────

log "Generating product data report for bot=${BOT}."

cat <<EOF
## Product Vision — ${BOT}

${VISION_BLOCK}

## Known Community Members — ${BOT}

${COMMUNITY_HASHES_BLOCK}

## Bot Usage (7-day, Prometheus, prefix=${METRIC_PREFIX})

### Engagement Summary

| Metric | Value |
|--------|-------|
| Total Interactions (7d) | ${INTERACTIONS_7D} |
| Previous Period (7d) | ${INTERACTIONS_PREV} |
| Errors (7d) | ${ERROR_COUNT_7D} |

### Command Usage (7 days)

| Command | Count |
|---------|-------|
${CMD_7D}

### Callback Actions (7 days)

| Action | Count |
|--------|-------|
${CB_7D}

## Domain Query Set (${QUERY_SET}) — scripts/queries/${QUERY_SET}/*.sql
${QUERY_RESULTS}

## Open Issues

| Category | Count |
|----------|-------|
| Unprocessed Feedback | ${OPEN_FEEDBACK} |
| Feature Requests | ${OPEN_FEATURES} |
| Bugs | ${OPEN_BUGS} |
EOF

log "Product data report generated successfully for bot=${BOT}."
