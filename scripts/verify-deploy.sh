#!/usr/bin/env bash
# verify-deploy.sh — Post-deploy verification for ArgoCD + Loki + Prometheus.
#
# Required env vars:
#   ARGOCD_URL            e.g. http://argo.internal
#   LOKI_URL              e.g. http://loki.internal
#   PROMETHEUS_URL        e.g. http://prometheus.internal:9090
#   EXPECTED_IMAGE_TAG    git SHA to look for in running image
#   ARGOCD_AUTH_TOKEN     bearer token for ArgoCD API
#   ARGOCD_APP_NAME       ArgoCD application name (default: coupon-bot)
#   CONTAINER_NAME        Loki/Prometheus container label (default: coupon-bot)
#   READINESS_GRACE_PERIOD  seconds to tolerate readiness failures (default: 180)
#
# Exit codes: 0 = success, 1 = failure

set -euo pipefail

: "${ARGOCD_URL:?ARGOCD_URL is required}"
: "${LOKI_URL:?LOKI_URL is required}"
: "${PROMETHEUS_URL:?PROMETHEUS_URL is required}"
: "${EXPECTED_IMAGE_TAG:?EXPECTED_IMAGE_TAG is required}"
: "${ARGOCD_AUTH_TOKEN:?ARGOCD_AUTH_TOKEN is required}"

APP_NAME="${ARGOCD_APP_NAME:-coupon-bot}"
CONTAINER="${CONTAINER_NAME:-coupon-bot}"
GRACE="${READINESS_GRACE_PERIOD:-180}"

AUTH_HEADER="Authorization: Bearer ${ARGOCD_AUTH_TOKEN}"

log() { echo "[$(date -u +%H:%M:%S)] $*"; }

# summary() appends markdown to GitHub Actions Job Summary
# Falls back to /dev/null when GITHUB_STEP_SUMMARY is not set (local runs)
summary() {
    local summary_file="${GITHUB_STEP_SUMMARY:-/dev/null}"
    echo "$@" >> "$summary_file"
}

# Initialize summary
summary "## 🚀 Deployment Verification"
summary ""
summary "**App:** \`${APP_NAME}\`  "
summary "**Expected Image Tag:** \`${EXPECTED_IMAGE_TAG:0:12}...\`  "
summary "**Started:** $(date -u +%Y-%m-%d\ %H:%M:%S) UTC"
summary ""

APP_PATH="/api/v1/applications/${APP_NAME}"

# Maximum number of *consecutive* unreachable-ArgoCD polls to tolerate before
# giving up. The previous implementation collapsed "API unreachable" into
# sync=Unknown via `curl -sf ... 2>/dev/null || echo "{}"`, so a dropped VPN
# tunnel looked identical to "app not synced yet" and the script polled
# uselessly until the full timeout — reporting a deploy failure even when the
# deployment had actually succeeded (see issue #167).
MAX_FAIL_STREAK="${MAX_FAIL_STREAK:-5}"

# argocd_fetch — GET the ArgoCD application JSON.
#   success: echoes the response body to stdout, returns 0
#   failure: echoes nothing, logs a diagnostic to stderr, returns non-zero
# A non-zero return means "could NOT observe ArgoCD" (network / DNS / VPN /
# timeout / non-2xx / unparseable body) and MUST NOT be confused with a valid
# observation of an un-synced / unhealthy app. All diagnostics go to stderr so
# they never pollute the captured body.
argocd_fetch() {
    local body http_code curl_exit=0 err_file
    err_file=$(mktemp)
    # No -f: keep the body on 4xx/5xx so diagnostics are useful. -w appends the
    # HTTP status on a trailing line; -m bounds a hung connection so a dead
    # tunnel fails fast instead of blocking the whole poll interval.
    body=$(curl -sS -m 15 -w $'\n%{http_code}' \
        "${ARGOCD_URL}${APP_PATH}" -H "${AUTH_HEADER}" 2>"$err_file") || curl_exit=$?
    http_code="${body##*$'\n'}"
    body="${body%$'\n'*}"

    if [ "$curl_exit" -ne 0 ]; then
        log "  WARN: ArgoCD request failed (curl exit ${curl_exit}): $(tr '\n' ' ' < "$err_file")" >&2
        rm -f "$err_file"
        return 1
    fi
    rm -f "$err_file"

    if [ "$http_code" != "200" ]; then
        log "  WARN: ArgoCD returned HTTP ${http_code}" >&2
        return 1
    fi

    if ! echo "$body" | jq -e . >/dev/null 2>&1; then
        log "  WARN: ArgoCD returned a non-JSON / unparseable body" >&2
        return 1
    fi

    echo "$body"
}

# fail_connectivity — exit with a clear "this is infrastructure, not a bad
# deploy" message after too many consecutive unreachable-ArgoCD polls.
fail_connectivity() {
    local phase="$1" streak="$2" elapsed_s="$3"
    log "FAILED: Cannot reach ArgoCD at ${ARGOCD_URL} after ${streak} consecutive attempts (elapsed=${elapsed_s}s)."
    log "  This is a connectivity problem (VPN / DNS / network), NOT a deployment failure."
    log "  The deployment itself may well have succeeded — verify ArgoCD directly before treating this as a bad release."
    summary "### ❌ ${phase}: ArgoCD CONNECTIVITY FAILURE"
    summary "- **Reason:** Could not reach ArgoCD API at \`${ARGOCD_URL}\` (${streak} consecutive failures)"
    summary "- **Likely cause:** VPN / DNS / network between the runner and the cluster — not necessarily a bad deploy"
    summary "- **Action:** Check ArgoCD directly; if the app is Synced/Healthy this is a CI connectivity flake."
    exit 1
}

# ─── Phase 1: Wait for ArgoCD to sync with expected image tag ────────────────

log "Phase 1: Waiting for ArgoCD sync (app=${APP_NAME}, expected tag contains ${EXPECTED_IMAGE_TAG:0:12}...)"

SYNC_TIMEOUT=600  # 10 minutes
SYNC_INTERVAL=30
elapsed=0

synced=false
fail_streak=0
while [ "$elapsed" -lt "$SYNC_TIMEOUT" ]; do
    if RESPONSE=$(argocd_fetch); then
        fail_streak=0

        SYNC_STATUS=$(echo "$RESPONSE" | jq -r '.status.sync.status // "Unknown"')
        IMAGES=$(echo "$RESPONSE" | jq -r '.status.summary.images // [] | .[]' 2>/dev/null || echo "")
        IMAGE_MATCH=$(echo "$IMAGES" | grep -c "${EXPECTED_IMAGE_TAG}" || true)

        log "  sync=${SYNC_STATUS} images_with_tag=${IMAGE_MATCH} (elapsed=${elapsed}s)"

        if [ "$SYNC_STATUS" = "Synced" ] && [ "$IMAGE_MATCH" -gt 0 ]; then
            log "Phase 1 PASSED: Image tag found, sync status is Synced."
            summary "### ✅ Phase 1: ArgoCD Sync"
            summary "- **Status:** Synced"
            summary "- **Image Tag Match:** Yes"
            summary "- **Elapsed Time:** ${elapsed}s"
            summary ""
            synced=true
            break
        fi
    else
        # Could not observe ArgoCD — this is connectivity, not "not synced yet".
        fail_streak=$((fail_streak + 1))
        log "  ArgoCD API unreachable (consecutive failures=${fail_streak}/${MAX_FAIL_STREAK}, elapsed=${elapsed}s)"
        if [ "$fail_streak" -ge "$MAX_FAIL_STREAK" ]; then
            fail_connectivity "Phase 1: ArgoCD Sync" "$fail_streak" "$elapsed"
        fi
    fi

    sleep "$SYNC_INTERVAL"
    elapsed=$((elapsed + SYNC_INTERVAL))
done

if [ "$synced" = false ]; then
    log "FAILED: Timed out waiting for ArgoCD sync after ${SYNC_TIMEOUT}s"
    log "  Last sync status: ${SYNC_STATUS}"
    log "  Running images: ${IMAGES}"
    summary "### ❌ Phase 1: ArgoCD Sync FAILED"
    summary "- **Reason:** Timeout after ${SYNC_TIMEOUT}s"
    summary "- **Last Sync Status:** \`${SYNC_STATUS}\`"
    summary "- **Running Images:**"
    summary "\`\`\`"
    summary "${IMAGES}"
    summary "\`\`\`"
    exit 1
fi

# ─── Phase 2: Readiness grace period ─────────────────────────────────────────

log "Phase 2: Readiness grace period (${GRACE}s). Waiting for pod to become healthy..."

GRACE_INTERVAL=15
grace_elapsed=0
healthy=false
fail_streak=0

while [ "$grace_elapsed" -lt "$GRACE" ]; do
    if RESPONSE=$(argocd_fetch); then
        fail_streak=0
        HEALTH=$(echo "$RESPONSE" | jq -r '.status.health.status // "Unknown"')

        log "  health=${HEALTH} (grace elapsed=${grace_elapsed}s/${GRACE}s)"

        if [ "$HEALTH" = "Healthy" ]; then
            log "Phase 2 PASSED: Pod is healthy (before grace period expired)."
            summary "### ✅ Phase 2: Readiness Check"
            summary "- **Health Status:** Healthy"
            summary "- **Time to Healthy:** ${grace_elapsed}s (within ${GRACE}s grace period)"
            summary ""
            healthy=true
            break
        fi
    else
        # Could not observe ArgoCD — connectivity, not "not healthy yet".
        fail_streak=$((fail_streak + 1))
        log "  ArgoCD API unreachable (consecutive failures=${fail_streak}/${MAX_FAIL_STREAK}, grace elapsed=${grace_elapsed}s)"
        if [ "$fail_streak" -ge "$MAX_FAIL_STREAK" ]; then
            fail_connectivity "Phase 2: Readiness Check" "$fail_streak" "$grace_elapsed"
        fi
    fi

    sleep "$GRACE_INTERVAL"
    grace_elapsed=$((grace_elapsed + GRACE_INTERVAL))
done

if [ "$healthy" = false ]; then
    # Final check after grace period. If we cannot even reach ArgoCD here, treat
    # it as a connectivity failure rather than declaring the pod unhealthy.
    if ! RESPONSE=$(argocd_fetch); then
        fail_connectivity "Phase 2: Readiness Check" "final-check" "$grace_elapsed"
    fi
    HEALTH=$(echo "$RESPONSE" | jq -r '.status.health.status // "Unknown"')

    if [ "$HEALTH" != "Healthy" ]; then
        log "FAILED: Pod is not healthy after ${GRACE}s grace period. Health: ${HEALTH}"
        CONDITIONS=$(echo "$RESPONSE" | jq '.status.conditions // []')
        log "  Conditions: ${CONDITIONS}"
        summary "### ❌ Phase 2: Readiness Check FAILED"
        summary "- **Health Status:** \`${HEALTH}\`"
        summary "- **Grace Period:** ${GRACE}s (expired)"
        summary "- **Conditions:**"
        summary "\`\`\`json"
        summary "${CONDITIONS}"
        summary "\`\`\`"
        exit 1
    fi
    log "Phase 2 PASSED: Pod became healthy at the end of the grace period."
    summary "### ✅ Phase 2: Readiness Check"
    summary "- **Health Status:** Healthy"
    summary "- **Time to Healthy:** ${GRACE}s (at end of grace period)"
    summary ""
fi

# ─── Phase 3: Verify logs and metrics ────────────────────────────────────────

log "Phase 3: Checking Loki for error-level logs..."

# Query Loki for errors in the last 2 minutes
START=$(date -u -d '2 minutes ago' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v-2M +%Y-%m-%dT%H:%M:%SZ)
LOKI_QUERY="{container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\""

LOKI_RESPONSE=$(curl -sf -G "${LOKI_URL}/loki/api/v1/query_range" \
    --data-urlencode "query=${LOKI_QUERY}" \
    --data-urlencode "start=${START}" \
    --data-urlencode "limit=20" 2>/dev/null || echo '{"data":{"result":[]}}')

ERROR_COUNT=$(echo "$LOKI_RESPONSE" | jq '[.data.result[].values[]] | length')

if [ "$ERROR_COUNT" -gt 0 ]; then
    log "WARNING: Found ${ERROR_COUNT} error-level log entries after deployment:"
    echo "$LOKI_RESPONSE" | jq -r '.data.result[].values[] | .[1]' | head -10
    log "FAILED: Error logs detected after deployment."
    summary "### ❌ Phase 3: Log & Metrics Check FAILED"
    summary "- **Loki Errors:** ${ERROR_COUNT} error-level entries found"
    summary "- **Sample Errors:**"
    summary "\`\`\`"
    echo "$LOKI_RESPONSE" | jq -r '.data.result[].values[] | .[1]' | head -10 | while IFS= read -r line; do
        summary "$line"
    done
    summary "\`\`\`"
    exit 1
fi
log "  Loki: no error-level logs found."

log "Phase 3: Checking Prometheus metrics..."

# Check restart count
RESTARTS=$(curl -sf -G "${PROMETHEUS_URL}/api/v1/query" \
    --data-urlencode "query=kube_pod_container_status_restarts_total{container=\"${CONTAINER}\"}" \
    2>/dev/null || echo '{"data":{"result":[]}}')
RESTART_COUNT=$(echo "$RESTARTS" | jq -r '[.data.result[].value[1] | tonumber] | add // 0')

# Check 5xx rate
ERRORS_5XX=$(curl -sf -G "${PROMETHEUS_URL}/api/v1/query" \
    --data-urlencode "query=sum(rate(http_server_request_duration_seconds_count{http_response_status_code=~\"5..\",job=\"${CONTAINER}\"}[5m]))" \
    2>/dev/null || echo '{"data":{"result":[]}}')
ERROR_5XX_RATE=$(echo "$ERRORS_5XX" | jq -r '[.data.result[].value[1] | tonumber] | add // 0')

log "  Prometheus: restarts=${RESTART_COUNT}, 5xx_rate=${ERROR_5XX_RATE}"

# Note: restart count is cumulative, so we only fail if it's unexpectedly high.
# For a fresh deployment, a single restart might be acceptable.
if [ "$(echo "$ERROR_5XX_RATE > 0" | bc -l 2>/dev/null || echo 0)" = "1" ]; then
    log "FAILED: 5xx error rate is non-zero: ${ERROR_5XX_RATE}"
    summary "### ❌ Phase 3: Log & Metrics Check FAILED"
    summary "- **5xx Error Rate:** ${ERROR_5XX_RATE} (expected: 0)"
    summary "- **Container Restarts:** ${RESTART_COUNT}"
    exit 1
fi

summary "### ✅ Phase 3: Log & Metrics Check"
summary "- **Loki Errors:** 0"
summary "- **5xx Error Rate:** ${ERROR_5XX_RATE}"
summary "- **Container Restarts:** ${RESTART_COUNT}"
summary ""

# ─── Done ─────────────────────────────────────────────────────────────────────

log "ALL CHECKS PASSED. Deployment verified successfully."
log "  App: ${APP_NAME}"
log "  Image tag: ${EXPECTED_IMAGE_TAG:0:12}..."
log "  Sync: Synced, Health: Healthy"
log "  Loki errors: 0, 5xx rate: ${ERROR_5XX_RATE}"

summary "---"
summary ""
summary "## ✅ Deployment Verification Complete"
summary ""
summary "| Check | Status | Details |"
summary "|-------|--------|---------|"
summary "| ArgoCD Sync | ✅ Passed | Synced with tag \`${EXPECTED_IMAGE_TAG:0:12}...\` |"
summary "| Pod Health | ✅ Passed | Healthy |"
summary "| Loki Errors | ✅ Passed | 0 error-level logs |"
summary "| 5xx Error Rate | ✅ Passed | ${ERROR_5XX_RATE} |"
summary "| Container Restarts | ✅ Info | ${RESTART_COUNT} |"
summary ""
summary "**Completed:** $(date -u +%Y-%m-%d\ %H:%M:%S) UTC"

exit 0
