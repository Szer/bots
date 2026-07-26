#!/usr/bin/env bash
# gather-metrics.sh — Collects infrastructure metrics for daily self-assessment.
#
# Required env vars:
#   PROMETHEUS_URL        e.g. http://prometheus.internal:9090
#   LOKI_URL              e.g. http://loki.internal
#   ARGOCD_URL            e.g. http://argo.internal
#   ARGOCD_AUTH_TOKEN     bearer token for ArgoCD API
#   ARGOCD_APP_NAME       ArgoCD application name (default: coupon-bot)
#   CONTAINER_NAME        container label (default: coupon-bot)
#
# Output: structured markdown report to stdout, led by a machine-readable
# JSON source manifest line, e.g. {"sources":{"prometheus":"ok","loki":"ok","argocd":"ok"}}
#
# Failure behavior: every data source (Prometheus, Loki, ArgoCD) is probed
# up front. A failed probe is fatal, after retries — the script prints the
# real curl error to stderr and exits non-zero rather than substituting an
# empty/N-A result (a genuinely empty result set from a *successful* query is
# not an error and is reported as-is). "Source unreachable" must never render
# as "everything is fine" — see gather-product-data.sh for the sibling
# version of this same rule.

set -euo pipefail

: "${PROMETHEUS_URL:?PROMETHEUS_URL is required}"
: "${LOKI_URL:?LOKI_URL is required}"
: "${ARGOCD_URL:?ARGOCD_URL is required}"
: "${ARGOCD_AUTH_TOKEN:?ARGOCD_AUTH_TOKEN is required}"

CONTAINER="${CONTAINER_NAME:-coupon-bot}"
APP_NAME="${ARGOCD_APP_NAME:-coupon-bot}"
AUTH_HEADER="Authorization: Bearer ${ARGOCD_AUTH_TOKEN}"

log() { echo "[$(date -u +%H:%M:%S)] $*" >&2; }

# Generic retrying curl wrapper shared by all three sources below. `-S` keeps
# curl's own diagnostic ("Could not resolve host", "Connection refused", the
# failing HTTP status, etc) visible on stderr even though `-s` mutes the
# progress meter; stderr itself is left unredirected so that text reaches the
# workflow log. A failed request (after retries) is fatal — the caller only
# picks the label used in the log line.
retry_curl() {
    local label="$1"
    shift
    local attempt
    for attempt in 1 2 3; do
        if result=$(curl -sSf --connect-timeout 5 --max-time 20 "$@"); then
            echo "$result"
            return 0
        fi
        log "${label} request failed (attempt ${attempt}/3), retrying..."
        sleep 1
    done
    log "ERROR: ${label} request failed after 3 attempts (see curl error above)"
    exit 1
}

# Helper: query Prometheus instant endpoint, return raw JSON
prom_query() {
    retry_curl "Prometheus" -G "${PROMETHEUS_URL}/api/v1/query" --data-urlencode "query=$1"
}

# Helper: extract scalar value from Prometheus response (first result)
prom_value() {
    echo "$1" | jq -r '[.data.result[].value[1]] | first // "N/A"'
}

# Helper: query Loki, return raw JSON
loki_query() {
    retry_curl "Loki" -G "$1" "${@:2}"
}

# Helper: query ArgoCD, return raw JSON
argocd_query() {
    retry_curl "ArgoCD" "$1" -H "${AUTH_HEADER}"
}

# ─── Preflight connectivity check ────────────────────────────────────────────
# One trivial probe per data source so a broken endpoint fails in one second
# with one clear error, instead of after many identical multi-second timeouts
# scattered through the report below. Every probe below is fatal on failure
# (via retry_curl/exit 1) — by the time the source manifest is emitted, all
# three sources are known reachable.

log "Verifying connectivity..."

log "Checking Prometheus connectivity..."
prom_query "vector(1)" >/dev/null

log "Checking Loki connectivity..."
loki_query "${LOKI_URL}/loki/api/v1/labels" >/dev/null

log "Checking ArgoCD connectivity..."
argocd_query "${ARGOCD_URL}/api/v1/applications/${APP_NAME}" >/dev/null

log "Prometheus: ok, Loki: ok, ArgoCD: ok"

SOURCES_MANIFEST='{"sources":{"prometheus":"ok","loki":"ok","argocd":"ok"}}'

# ─── Prometheus metrics ───────────────────────────────────────────────────────

log "Querying Prometheus metrics..."

# ── Current status (instant gauges) ──

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

# ── 24h trends ──

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

TOTAL_5XX_JSON=$(prom_query "sum(increase(http_server_request_duration_seconds_count{http_response_status_code=~\"5..\",job=\"${CONTAINER}\"}[24h]))")
TOTAL_5XX=$(echo "$TOTAL_5XX_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")

THROTTLE_JSON=$(prom_query "sum(increase(container_cpu_cfs_throttled_periods_total{container=\"${CONTAINER}\"}[24h]))")
THROTTLE_TOTAL=$(echo "$THROTTLE_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")

# ─── Loki logs ────────────────────────────────────────────────────────────────

log "Querying Loki logs (last 24h)..."

START_24H=$(date -u -d '24 hours ago' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v-24H +%Y-%m-%dT%H:%M:%SZ)

# Error/Fatal total count (accurate, uncapped)
ERROR_LOG_COUNT_JSON=$(loki_query "${LOKI_URL}/loki/api/v1/query" \
    --data-urlencode "query=sum(count_over_time({container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\"[24h]))")
ERROR_LOG_COUNT=$(echo "$ERROR_LOG_COUNT_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

# Sample of Error/Fatal logs for pattern extraction (capped at 200)
ERROR_LOGS_RESPONSE=$(loki_query "${LOKI_URL}/loki/api/v1/query_range" \
    --data-urlencode "query={container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\"" \
    --data-urlencode "start=${START_24H}" \
    --data-urlencode "limit=200")

# Top unique error messages (deduplicated, counts only — messages redacted for public issues)
TOP_ERRORS_COUNT=$(echo "$ERROR_LOGS_RESPONSE" | jq -r '
    [.data.result[].values[] | .[1]] |
    map(. as $line | try (fromjson | .RenderedMessage // .message // .msg // $line) catch $line) |
    group_by(.) |
    map({count: length}) |
    sort_by(-.count) |
    .[:10] |
    .[] |
    "  - \(.count) occurrence(s)"
' 2>/dev/null || echo "  - (parse error)")

# Warning total count (accurate, uncapped)
WARN_LOG_COUNT_JSON=$(loki_query "${LOKI_URL}/loki/api/v1/query" \
    --data-urlencode "query=sum(count_over_time({container=\"${CONTAINER}\"} | json | level=\"Warning\"[24h]))")
WARN_LOG_COUNT=$(echo "$WARN_LOG_COUNT_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

# Total log volume (last 24h)
LOG_VOLUME_JSON=$(loki_query "${LOKI_URL}/loki/api/v1/query" \
    --data-urlencode "query=count_over_time({container=\"${CONTAINER}\"}[24h])")
LOG_VOLUME_RAW=$(echo "$LOG_VOLUME_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")
if [[ "$LOG_VOLUME_RAW" =~ ^[0-9]+$ ]]; then
    LOG_VOLUME="${LOG_VOLUME_RAW} lines"
else
    LOG_VOLUME="$LOG_VOLUME_RAW"
fi

# ─── ArgoCD status ────────────────────────────────────────────────────────────

log "Querying ArgoCD status..."

ARGO_RESPONSE=$(argocd_query "${ARGOCD_URL}/api/v1/applications/${APP_NAME}")

SYNC_STATUS=$(echo "$ARGO_RESPONSE" | jq -r '.status.sync.status // "Unknown"')
HEALTH_STATUS=$(echo "$ARGO_RESPONSE" | jq -r '.status.health.status // "Unknown"')
DEPLOYED_IMAGES=$(echo "$ARGO_RESPONSE" | jq -r '.status.summary.images // [] | join(", ")' 2>/dev/null \
    | sed 's/:\([a-f0-9]\{12\}\)[a-f0-9]\{28,\}/:\1…/g' || true)
[ -z "$DEPLOYED_IMAGES" ] && DEPLOYED_IMAGES="Unknown"
ARGO_CONDITIONS=$(echo "$ARGO_RESPONSE" | jq -r '[.status.conditions[]? | "\(.type): \(.message)"] | join("\n")' 2>/dev/null || true)
[ -z "$ARGO_CONDITIONS" ] && ARGO_CONDITIONS="none"

# ─── Bot-specific metrics ─────────────────────────────────────────────────────

log "Querying bot-specific metrics..."

# Command usage distribution (24h)
CMD_USAGE_JSON=$(prom_query "sum by (command)(increase(couponhubbot_command_total[24h]))")
CMD_USAGE=$(echo "$CMD_USAGE_JSON" | jq -r '
    [.data.result[] | {cmd: .metric.command, count: (.value[1] | tonumber | floor)}]
    | sort_by(-.count)
    | .[] | "  - \(.cmd): \(.count)"
' 2>/dev/null)
[ -z "$CMD_USAGE" ] && CMD_USAGE="  - (no data yet)"

# Callback action distribution (24h)
CB_USAGE_JSON=$(prom_query "sum by (action)(increase(couponhubbot_callback_total[24h]))")
CB_USAGE=$(echo "$CB_USAGE_JSON" | jq -r '
    [.data.result[] | {action: .metric.action, count: (.value[1] | tonumber | floor)}]
    | sort_by(-.count)
    | .[] | "  - \(.action): \(.count)"
' 2>/dev/null)
[ -z "$CB_USAGE" ] && CB_USAGE="  - (no data yet)"

# Feedback submissions (24h)
FEEDBACK_JSON=$(prom_query "sum(increase(couponhubbot_feedback_total[24h]))")
FEEDBACK_COUNT=$(echo "$FEEDBACK_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

# Button clicks (24h)
BUTTON_JSON=$(prom_query "sum(increase(couponhubbot_button_click_total[24h]))")
BUTTON_COUNT=$(echo "$BUTTON_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

# ─── Output markdown report ──────────────────────────────────────────────────

log "Generating report."

cat <<EOF
${SOURCES_MANIFEST}

## Infrastructure Health (Prometheus)

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
| Total 5xx Errors | ${TOTAL_5XX} |
| CPU Throttled Periods | ${THROTTLE_TOTAL} |

### Connectivity

| Service | Reachable |
|---------|-----------|
| Prometheus | yes |
| Loki | yes |
| ArgoCD | yes |

## Error Logs (24h via Loki)

- **Error/Fatal entries**: ${ERROR_LOG_COUNT}
- **Warning entries**: ${WARN_LOG_COUNT}
- **Total log volume (24h)**: ${LOG_VOLUME}

### Top Error Patterns (counts only — query Loki for details)
${TOP_ERRORS_COUNT:-  - (none)}

## Deployment Status (ArgoCD)

| Field | Value |
|-------|-------|
| Sync Status | ${SYNC_STATUS} |
| Health Status | ${HEALTH_STATUS} |
| Deployed Image | ${DEPLOYED_IMAGES} |

### Conditions
${ARGO_CONDITIONS}

## Bot Usage (24h)

| Metric | Value |
|--------|-------|
| Feedback Submissions | ${FEEDBACK_COUNT} |
| Button Clicks | ${BUTTON_COUNT} |

### Command Usage
${CMD_USAGE}

### Callback Actions
${CB_USAGE}
EOF

log "Report generated successfully."
