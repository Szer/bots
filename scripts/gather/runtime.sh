#!/usr/bin/env bash
# runtime.sh <bot> — per-bot infrastructure/runtime evidence gatherer.
# Replaces the old (coupon-only) gather-metrics.sh; reads bot identity
# (container, argocd_app) from .github/bots.yml instead of hardcoding it.
#
# Usage: scripts/gather/runtime.sh <bot>     # bot key from .github/bots.yml, e.g. vahter
#
# Required env vars:
#   PROMETHEUS_URL        e.g. http://prometheus.internal:9090
#   LOKI_URL              e.g. http://loki.internal
#   ARGOCD_URL            e.g. http://argo.internal
#   ARGOCD_AUTH_TOKEN     bearer token for ArgoCD API
#
# Output: the manifest JSON line FIRST, then a structured markdown report to
# stdout.
#
# Failure behavior: prometheus/loki/argocd are each probed up front with
# probe() (non-fatal) so the manifest can always be emitted. If ANY of them
# is unreachable, the manifest is printed and the script exits non-zero —
# the caller (workflow guard step) must not invoke an agent on a bundle with
# a degraded source. If all three are reachable, the rest of the report is
# built using the fail-loud retry_curl() — a source that answered the
# preflight probe but then fails mid-report is still a hard error, same as
# the Phase-0 gather-metrics.sh behavior. "Source unreachable" must never
# render as "everything is fine" — see AGENT-FLOWS-REDESIGN.md §1.3.

set -euo pipefail

BOT="${1:?usage: runtime.sh <bot>  (bot key from .github/bots.yml)}"

# shellcheck source=scripts/gather/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

: "${PROMETHEUS_URL:?PROMETHEUS_URL is required}"
: "${LOKI_URL:?LOKI_URL is required}"
: "${ARGOCD_URL:?ARGOCD_URL is required}"
: "${ARGOCD_AUTH_TOKEN:?ARGOCD_AUTH_TOKEN is required}"

if ! bot_exists "$BOT"; then
    log "ERROR: bot '${BOT}' is not present in ${BOTS_YML}"
    exit 1
fi

CONTAINER="$(bot_field "$BOT" '.container')"
APP_NAME="$(bot_field "$BOT" '.argocd_app')"
METRIC_PREFIX="$(bot_field "$BOT" '.metric_prefix')"
AUTH_HEADER="Authorization: Bearer ${ARGOCD_AUTH_TOKEN}"

# ─── Preflight connectivity check (non-fatal — builds the manifest) ────────

log "Verifying connectivity for bot=${BOT} (container=${CONTAINER}, argocd_app=${APP_NAME})..."

PROM_STATUS=$(probe "prometheus" curl -sSf --connect-timeout 5 --max-time 20 \
    -G "${PROMETHEUS_URL}/api/v1/query" --data-urlencode "query=vector(1)") || true
LOKI_STATUS=$(probe "loki" curl -sSf --connect-timeout 5 --max-time 20 \
    -G "${LOKI_URL}/loki/api/v1/labels") || true
ARGOCD_STATUS=$(probe "argocd" curl -sSf --connect-timeout 5 --max-time 20 \
    "${ARGOCD_URL}/api/v1/applications/${APP_NAME}" -H "${AUTH_HEADER}") || true

MANIFEST=$(emit_manifest "$BOT" \
    "prometheus=${PROM_STATUS}" \
    "loki=${LOKI_STATUS}" \
    "argocd=${ARGOCD_STATUS}")

# Manifest is ALWAYS the first line of stdout, whether or not we go on to
# fail — the workflow's guard step reads this even from a truncated report.
echo "$MANIFEST"

DEGRADED=0
if manifest_has_bad_source "$MANIFEST"; then
    DEGRADED=1
fi

if [ "$DEGRADED" -eq 1 ]; then
    log "ERROR: one or more required sources unreachable for bot=${BOT} — see manifest above. Not building the rest of the report."
    exit 1
fi

log "Prometheus: ok, Loki: ok, ArgoCD: ok"

# ─── Prometheus metrics ───────────────────────────────────────────────────

prom_query() {
    retry_curl "Prometheus" -G "${PROMETHEUS_URL}/api/v1/query" --data-urlencode "query=$1"
}
prom_value() {
    echo "$1" | jq -r '[.data.result[].value[1]] | first // "N/A"'
}
loki_query() {
    retry_curl "Loki" -G "$1" "${@:2}"
}
argocd_query() {
    retry_curl "ArgoCD" "$1" -H "${AUTH_HEADER}"
}

log "Querying Prometheus metrics..."

POD_READY_JSON=$(prom_query "min(kube_pod_status_ready{pod=~\"${CONTAINER}.*\",condition=\"true\"})")
POD_READY=$(prom_value "$POD_READY_JSON")

MEMORY_JSON=$(prom_query "sum(container_memory_working_set_bytes{container=\"${CONTAINER}\"})")
MEMORY_BYTES=$(prom_value "$MEMORY_JSON")
if [ "$MEMORY_BYTES" != "N/A" ]; then
    MEMORY_MB=$(echo "$MEMORY_BYTES" | awk '{printf "%.1f", $1/1048576}')
else
    MEMORY_MB="N/A"
fi

RESTARTS_JSON=$(prom_query "sum(kube_pod_container_status_restarts_total{container=\"${CONTAINER}\"})")
RESTART_COUNT=$(prom_value "$RESTARTS_JSON")

WAITING_JSON=$(prom_query "kube_pod_container_status_waiting_reason{container=\"${CONTAINER}\"}")
WAITING_REASONS=$(echo "$WAITING_JSON" | jq -r '[.data.result[] | select((.value[1] | tonumber) > 0) | .metric.reason + "=" + .value[1]] | join(", ")' 2>/dev/null)
[ -z "$WAITING_REASONS" ] && WAITING_REASONS="none"

MEMORY_MAX_JSON=$(prom_query "max_over_time(sum(container_memory_working_set_bytes{container=\"${CONTAINER}\"})[24h:5m])")
MEMORY_MAX_BYTES=$(prom_value "$MEMORY_MAX_JSON")
if [ "$MEMORY_MAX_BYTES" != "N/A" ]; then
    MEMORY_MAX_MB=$(echo "$MEMORY_MAX_BYTES" | awk '{printf "%.1f", $1/1048576}')
else
    MEMORY_MAX_MB="N/A"
fi

CPU_AVG_JSON=$(prom_query "avg_over_time(sum(rate(container_cpu_usage_seconds_total{container=\"${CONTAINER}\"}[5m]))[24h:5m])")
CPU_AVG=$(echo "$CPU_AVG_JSON" | jq -r '[.data.result[].value[1] | tonumber] | add // 0' 2>/dev/null || echo "N/A")
if [ "$CPU_AVG" != "N/A" ] && [ "$CPU_AVG" != "0" ]; then
    CPU_AVG_PERCENT=$(echo "$CPU_AVG" | awk '{printf "%.2f%%", $1*100}')
else
    CPU_AVG_PERCENT="${CPU_AVG}"
fi

CPU_MAX_JSON=$(prom_query "max_over_time(sum(rate(container_cpu_usage_seconds_total{container=\"${CONTAINER}\"}[5m]))[24h:5m])")
CPU_MAX=$(echo "$CPU_MAX_JSON" | jq -r '[.data.result[].value[1] | tonumber] | add // 0' 2>/dev/null || echo "N/A")
if [ "$CPU_MAX" != "N/A" ] && [ "$CPU_MAX" != "0" ]; then
    CPU_MAX_PERCENT=$(echo "$CPU_MAX" | awk '{printf "%.2f%%", $1*100}')
else
    CPU_MAX_PERCENT="${CPU_MAX}"
fi

TOTAL_REQUESTS_JSON=$(prom_query "sum(increase(http_server_request_duration_seconds_count{job=\"${CONTAINER}\"}[24h]))")
TOTAL_REQUESTS=$(echo "$TOTAL_REQUESTS_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")

# NOTE: 5xx counted here for completeness only — it is NOT a health signal.
# The /bot webhook always returns HTTP 200 to Telegram regardless of internal
# exceptions, so this is structurally always 0 (AGENT-FLOWS-REDESIGN.md §1.4,
# §9). Do not use it as a health gate in project.md/product.md.
TOTAL_5XX_JSON=$(prom_query "sum(increase(http_server_request_duration_seconds_count{http_response_status_code=~\"5..\",job=\"${CONTAINER}\"}[24h]))")
TOTAL_5XX=$(echo "$TOTAL_5XX_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")

THROTTLE_JSON=$(prom_query "sum(increase(container_cpu_cfs_throttled_periods_total{container=\"${CONTAINER}\"}[24h]))")
THROTTLE_TOTAL=$(echo "$THROTTLE_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")

# ─── Loki logs ──────────────────────────────────────────────────────────

log "Querying Loki logs (last 24h)..."

START_24H=$(date -u -d '24 hours ago' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v-24H +%Y-%m-%dT%H:%M:%SZ)

ERROR_LOG_COUNT_JSON=$(loki_query "${LOKI_URL}/loki/api/v1/query" \
    --data-urlencode "query=sum(count_over_time({container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\"[24h]))")
ERROR_LOG_COUNT=$(echo "$ERROR_LOG_COUNT_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

ERROR_LOGS_RESPONSE=$(loki_query "${LOKI_URL}/loki/api/v1/query_range" \
    --data-urlencode "query={container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\"" \
    --data-urlencode "start=${START_24H}" \
    --data-urlencode "limit=200")

# Grouped by SourceContext + message prefix (query_loki_patterns 404s on
# this Loki — see AGENT-FLOWS-REDESIGN.md §1.8/§6.1, built by hand here).
# Emits per-group SourceContext, count, AND one verbatim raw sample line —
# counts-only output left rules 1 (new failure mode) and 2 (error-group
# z-score) unevaluable during the 2026-07-26 drill: the new
# `Program.DrillRuntimeFailureLoop` SourceContext (unseen in 28 days, a
# guaranteed rule-1 hit) was invisible behind a bare "N occurrence(s)" line.
TOP_ERRORS_GROUPED=$(echo "$ERROR_LOGS_RESPONSE" | jq -r '
    [.data.result[].values[] | .[1]] |
    map(
        . as $raw
        | (try fromjson catch {}) as $parsed
        | {
            source_context: ($parsed.SourceContext // "unknown"),
            prefix: ((($parsed["@m"] // $parsed.RenderedMessage // $parsed.message // $parsed.msg // $raw) | tostring)[0:80]),
            raw: $raw
          }
    ) |
    group_by(.source_context + "|" + .prefix) |
    map({
        source_context: .[0].source_context,
        prefix: .[0].prefix,
        count: length,
        sample: .[0].raw
    }) |
    sort_by(-.count) |
    .[:10] |
    .[] |
    "  - **\(.source_context)** (\(.count)x): \(.prefix)\n    sample: `\(.sample)`"
' 2>/dev/null || echo "  - (parse error)")

WARN_LOG_COUNT_JSON=$(loki_query "${LOKI_URL}/loki/api/v1/query" \
    --data-urlencode "query=sum(count_over_time({container=\"${CONTAINER}\"} | json | level=\"Warning\"[24h]))")
WARN_LOG_COUNT=$(echo "$WARN_LOG_COUNT_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

LOG_VOLUME_JSON=$(loki_query "${LOKI_URL}/loki/api/v1/query" \
    --data-urlencode "query=count_over_time({container=\"${CONTAINER}\"}[24h])")
LOG_VOLUME_RAW=$(echo "$LOG_VOLUME_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")
if [[ "$LOG_VOLUME_RAW" =~ ^[0-9]+$ ]]; then
    LOG_VOLUME="${LOG_VOLUME_RAW} lines"
else
    LOG_VOLUME="$LOG_VOLUME_RAW"
fi

# ─── ArgoCD status ──────────────────────────────────────────────────────

log "Querying ArgoCD status..."

ARGO_RESPONSE=$(argocd_query "${ARGOCD_URL}/api/v1/applications/${APP_NAME}")

SYNC_STATUS=$(echo "$ARGO_RESPONSE" | jq -r '.status.sync.status // "Unknown"')
HEALTH_STATUS=$(echo "$ARGO_RESPONSE" | jq -r '.status.health.status // "Unknown"')
DEPLOYED_IMAGES=$(echo "$ARGO_RESPONSE" | jq -r '.status.summary.images // [] | join(", ")' 2>/dev/null \
    | sed 's/:\([a-f0-9]\{12\}\)[a-f0-9]\{28,\}/:\1…/g' || true)
[ -z "$DEPLOYED_IMAGES" ] && DEPLOYED_IMAGES="Unknown"
ARGO_CONDITIONS=$(echo "$ARGO_RESPONSE" | jq -r '[.status.conditions[]? | "\(.type): \(.message)"] | join("\n")' 2>/dev/null || true)
[ -z "$ARGO_CONDITIONS" ] && ARGO_CONDITIONS="none"

# ─── Bot-specific metrics (metric_prefix-driven, works for any bot) ──────

log "Querying bot-specific metrics (prefix=${METRIC_PREFIX})..."

CMD_USAGE_JSON=$(prom_query "sum by (command)(increase(${METRIC_PREFIX}command_total[24h]))")
CMD_USAGE=$(echo "$CMD_USAGE_JSON" | jq -r '
    [.data.result[] | {cmd: .metric.command, count: (.value[1] | tonumber | floor)}]
    | sort_by(-.count)
    | .[] | "  - \(.cmd): \(.count)"
' 2>/dev/null)
[ -z "$CMD_USAGE" ] && CMD_USAGE="  - (no data yet)"

CB_USAGE_JSON=$(prom_query "sum by (action)(increase(${METRIC_PREFIX}callback_total[24h]))")
CB_USAGE=$(echo "$CB_USAGE_JSON" | jq -r '
    [.data.result[] | {action: .metric.action, count: (.value[1] | tonumber | floor)}]
    | sort_by(-.count)
    | .[] | "  - \(.action): \(.count)"
' 2>/dev/null)
[ -z "$CB_USAGE" ] && CB_USAGE="  - (no data yet)"

# ─── Output markdown report ───────────────────────────────────────────────

log "Generating report for bot=${BOT}."

cat <<EOF

## Infrastructure Health (Prometheus) — ${BOT}

### Current Status

| Metric | Value |
|--------|-------|
| Pod Ready | ${POD_READY} |
| Memory Usage | ${MEMORY_MB} MB |
| Container Restarts (total) | ${RESTART_COUNT} |
| Waiting Reasons | ${WAITING_REASONS} |

### 24h Trends

| Metric | Value |
|--------|-------|
| Peak Memory | ${MEMORY_MAX_MB} MB |
| Avg CPU | ${CPU_AVG_PERCENT} |
| Peak CPU | ${CPU_MAX_PERCENT} |
| Total Requests | ${TOTAL_REQUESTS} |
| Total 5xx Errors (dead metric — webhook always returns 200, never a health signal) | ${TOTAL_5XX} |
| CPU Throttled Periods | ${THROTTLE_TOTAL} |

## Error Logs (24h via Loki)

- **Error/Fatal entries**: ${ERROR_LOG_COUNT}
- **Warning entries**: ${WARN_LOG_COUNT}
- **Total log volume (24h)**: ${LOG_VOLUME}

### Top Error Patterns (grouped by SourceContext + message prefix, verbatim sample per group)
${TOP_ERRORS_GROUPED:-  - (none)}

## Deployment Status (ArgoCD)

| Field | Value |
|-------|-------|
| Sync Status | ${SYNC_STATUS} |
| Health Status | ${HEALTH_STATUS} |
| Deployed Image | ${DEPLOYED_IMAGES} |

### Conditions
${ARGO_CONDITIONS}

## Bot Usage (24h)

### Command Usage
${CMD_USAGE}

### Callback Actions
${CB_USAGE}
EOF

log "Report generated successfully for bot=${BOT}."
