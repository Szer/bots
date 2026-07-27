#!/usr/bin/env bash
# baseline.sh — reads/writes the `agent-state` orphan-branch baseline history
# and computes baseline-relative statistics for the `monitor` role.
# See .github/AGENT-FLOWS-REDESIGN.md §3.3 / §3.6 / §6.1.
#
# Subcommands:
#
#   baseline.sh gather <bot>
#       LIVE. Requires PROMETHEUS_URL/LOKI_URL/DB_PROD_HOST/etc (see below).
#       Queries postgres (trailing-24h counts of this bot's tracked series,
#       via .github/bots.yml `db_name`), Loki (log volume, error/warning
#       counts, and error lines grouped by SourceContext + message prefix —
#       query_loki_patterns 404s here, see AGENT-FLOWS-REDESIGN.md §1.8/§6.1),
#       and ArgoCD/Prometheus (pod health). Prints ONE JSON object to stdout:
#       {"series":{...},"error_groups":{...},"pods":{...},"sources":{...}}
#       This is the "today's rollup" that `append` persists.
#
#   baseline.sh append <bot> <state-dir> [<gather-json-file>|-]
#       Wraps the gather-json (file, or "-"/omitted for stdin) with
#       {"bot":...,"ts":<UTC now>,"run_id":...} and appends it as ONE line to
#       <state-dir>/<bot>/<YYYY-MM>.jsonl, creating the bot directory and/or
#       month file if this is the first run ever for that bot (or the first
#       of a new month). Never rewrites history — append-only.
#
#   baseline.sh stats <bot> <state-dir>
#       PURE / hermetic — reads only files already on disk, no network, no
#       wall-clock dependency (the "current" run is the most recent line by
#       its own `ts`, and all windows are computed relative to THAT
#       timestamp — not `date -u now` — so this is fully deterministic and
#       safe to unit-test against synthetic fixtures). Reads every
#       <state-dir>/<bot>/*.jsonl file, and for every series key present in
#       the most recent line computes: current, trailing-7d median,
#       trailing-28d median, ratio-vs-28d-median, z-score-vs-28d, the number
#       of distinct calendar days of history within the 28d window, and a
#       `low_confidence` flag (true when history_days_28d < 14 — see
#       AGENT-FLOWS-REDESIGN.md §6.1 "Refusal condition"). Guards every
#       division: a zero (or absent) 28d median yields `ratio: null`, not a
#       divide-by-zero, and the same for a zero (or single-sample) 28d
#       stdev yielding `z_score: null` — a dormant/zero-inflated series must
#       never produce a spurious anomaly (this is the single most important
#       property of this script — a wrong z-score silently produces false
#       anomalies forever). Prints ONE JSON object to stdout.
#
#   baseline.sh commit <state-dir> <commit-message>
#       Commits + pushes whatever `append` (and its caller) wrote in
#       <state-dir> to the `agent-state` branch. Only meaningful when
#       <state-dir> is a checkout (via `git worktree add`) of that branch.
#       A no-op (not an error) if there is nothing to commit.
#
# First-run handling: if <state-dir>/<bot>/ or its month file doesn't exist,
# `append` creates it (mkdir -p). If a bot has NO history file at all yet,
# `stats` still runs — every series reports history_days_28d=0,
# low_confidence=true, median/ratio/z_score=null — never a crash.

set -euo pipefail

# shellcheck source=scripts/gather/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

SUBCOMMAND="${1:-}"
shift || true

usage() {
    cat >&2 <<'EOF'
usage:
  baseline.sh gather <bot>
  baseline.sh append <bot> <state-dir> [<gather-json-file>|-]
  baseline.sh stats  <bot> <state-dir>
  baseline.sh commit <state-dir> <commit-message>
EOF
    exit 1
}

# ─── gather ────────────────────────────────────────────────────────────────

cmd_gather() {
    local bot="${1:?usage: baseline.sh gather <bot>}"

    : "${PROMETHEUS_URL:?PROMETHEUS_URL is required}"
    : "${LOKI_URL:?LOKI_URL is required}"
    : "${ARGOCD_URL:?ARGOCD_URL is required}"
    : "${ARGOCD_AUTH_TOKEN:?ARGOCD_AUTH_TOKEN is required}"
    : "${DB_PROD_HOST:?DB_PROD_HOST is required}"
    : "${DB_PROD_USERNAME:?DB_PROD_USERNAME is required}"
    : "${DB_PROD_PASSWORD:?DB_PROD_PASSWORD is required}"

    if ! bot_exists "$bot"; then
        log "ERROR: bot '${bot}' is not present in ${BOTS_YML}"
        exit 1
    fi

    local container app_name
    container="$(bot_field "$bot" '.container')"
    app_name="$(bot_field "$bot" '.argocd_app')"
    local db_name auth_header
    db_name="$(bot_field "$bot" '.db_name')"
    auth_header="Authorization: Bearer ${ARGOCD_AUTH_TOKEN}"
    local database_url="postgresql://${DB_PROD_USERNAME}:${DB_PROD_PASSWORD}@${DB_PROD_HOST}/${db_name}"

    log "baseline gather: bot=${bot} container=${container} argocd_app=${app_name} db=${db_name}"

    # ── preflight (non-fatal — always build the manifest first) ──────────
    local prom_status loki_status argocd_status postgres_status
    prom_status=$(probe "prometheus" curl -sSf --connect-timeout 5 --max-time 20 \
        -G "${PROMETHEUS_URL}/api/v1/query" --data-urlencode "query=vector(1)") || true
    loki_status=$(probe "loki" curl -sSf --connect-timeout 5 --max-time 20 \
        -G "${LOKI_URL}/loki/api/v1/labels") || true
    argocd_status=$(probe "argocd" curl -sSf --connect-timeout 5 --max-time 20 \
        "${ARGOCD_URL}/api/v1/applications/${app_name}" -H "${auth_header}") || true
    postgres_status=$(probe "postgres" env PGCONNECT_TIMEOUT=5 psql "${database_url}" -v ON_ERROR_STOP=1 -t -A -c "SELECT 1;") || true

    local sources_json
    sources_json=$(jq -n --arg p "$prom_status" --arg l "$loki_status" --arg a "$argocd_status" --arg d "$postgres_status" \
        '{prometheus:$p, loki:$l, argocd:$a, postgres:$d}')

    if echo "$sources_json" | jq -e 'to_entries[] | select(.value != "ok")' >/dev/null 2>&1; then
        log "ERROR: one or more required sources unreachable for bot=${bot}: $(echo "$sources_json" | jq -c .)"
        jq -n --argjson sources "$sources_json" '{series:{}, error_groups:{}, pods:{}, sources:$sources}'
        exit 1
    fi

    # ── postgres: trailing-24h counts of the tracked series (§6.1) ────────
    # Deliberately a fresh, minimal, bot-specific COUNT query rather than
    # reusing scripts/queries/<bot>/*.sql — those emit 28-DAY DAY-BUCKETED
    # rollups for the product agent's trend tables; here we want exactly
    # ONE row of trailing-24h totals per run, which `stats` then compares
    # across runs. Same tables/event_types/filters as the query_set files
    # (kept in sync manually — see scripts/queries/<bot>/*.sql headers).
    local series_json="{}"
    case "$bot" in
        vahter)
            series_json=$(psql "$database_url" -v ON_ERROR_STOP=1 -t -A -F $'\t' -c "
                SELECT
                    count(*) FILTER (WHERE event_type = 'MessageReceived'),
                    count(DISTINCT (data->>'userId')::bigint) FILTER (WHERE event_type = 'MessageReceived'),
                    count(*) FILTER (WHERE event_type = 'UserBanned'),
                    count(*) FILTER (WHERE event_type = 'VahterActed'),
                    count(*) FILTER (WHERE event_type = 'LlmClassified' AND data->>'verdict' = 'SPAM'),
                    count(*) FILTER (WHERE event_type = 'MlScoredMessage' AND (data->>'isSpam')::bool),
                    count(*) FILTER (WHERE event_type = 'CallbackCreated'),
                    count(*) FILTER (WHERE event_type = 'CallbackResolved'),
                    count(*) FILTER (WHERE event_type = 'MessageMarkedHam'),
                    count(*) FILTER (WHERE event_type = 'MessageMarkedSpam')
                FROM event
                WHERE created_at >= NOW() - INTERVAL '24 hours';
            " | awk -F'\t' '{printf "{\"messages_received_24h\":%s,\"unique_senders_24h\":%s,\"user_banned_24h\":%s,\"vahter_acted_24h\":%s,\"llm_classified_spam_24h\":%s,\"ml_scored_spam_24h\":%s,\"callback_created_24h\":%s,\"callback_resolved_24h\":%s,\"message_marked_ham_24h\":%s,\"message_marked_spam_24h\":%s}", $1,$2,$3,$4,$5,$6,$7,$8,$9,$10}')
            ;;
        coupon)
            series_json=$(psql "$database_url" -v ON_ERROR_STOP=1 -t -A -F $'\t' -c "
                SELECT
                    count(*) FILTER (WHERE event_type = 'added'),
                    count(*) FILTER (WHERE event_type = 'taken'),
                    count(*) FILTER (WHERE event_type = 'used'),
                    count(*) FILTER (WHERE event_type = 'returned'),
                    count(*) FILTER (WHERE event_type = 'voided'),
                    count(DISTINCT user_id)
                FROM coupon_event
                WHERE created_at >= NOW() - INTERVAL '24 hours';
            " | awk -F'\t' '{printf "{\"added_24h\":%s,\"taken_24h\":%s,\"used_24h\":%s,\"returned_24h\":%s,\"voided_24h\":%s,\"unique_users_24h\":%s}", $1,$2,$3,$4,$5,$6}')
            local chat_msgs feedback_7d
            chat_msgs=$(psql "$database_url" -v ON_ERROR_STOP=1 -t -A -c \
                "SELECT count(*) FROM chat_message WHERE created_at >= NOW() - INTERVAL '24 hours';" 2>/dev/null || echo 0)
            feedback_7d=$(psql "$database_url" -v ON_ERROR_STOP=1 -t -A -c \
                "SELECT count(*) FROM user_feedback WHERE created_at >= NOW() - INTERVAL '7 days';" 2>/dev/null || echo 0)
            series_json=$(echo "$series_json" | jq --argjson cm "${chat_msgs:-0}" --argjson fb "${feedback_7d:-0}" '. + {chat_message_24h: $cm, user_feedback_7d: $fb}')
            ;;
        alita)
            local msg_log llm_calls llm_cost
            msg_log=$(psql "$database_url" -v ON_ERROR_STOP=1 -t -A -c \
                "SELECT count(*) FROM message_log WHERE sent_at >= NOW() - INTERVAL '24 hours';" 2>/dev/null || echo 0)
            llm_calls=$(psql "$database_url" -v ON_ERROR_STOP=1 -t -A -c \
                "SELECT count(*) FROM llm_usage WHERE called_at >= NOW() - INTERVAL '24 hours';" 2>/dev/null || echo 0)
            llm_cost=$(psql "$database_url" -v ON_ERROR_STOP=1 -t -A -c \
                "SELECT coalesce(sum(cost_usd), 0) FROM llm_usage WHERE called_at >= NOW() - INTERVAL '24 hours';" 2>/dev/null || echo 0)
            series_json=$(jq -n --argjson m "${msg_log:-0}" --argjson c "${llm_calls:-0}" --arg cost "${llm_cost:-0}" \
                '{message_log_24h: $m, llm_calls_24h: $c, llm_cost_usd_24h: ($cost | tonumber)}')
            ;;
        *)
            log "WARNING: bot=${bot} has no known tracked-series query in baseline.sh — series will be empty"
            series_json="{}"
            ;;
    esac
    # Every bot also tracks Loki log volume / error volume per §6.1.
    local log_lines log_errors log_warnings
    log_lines=$(curl -sSf --connect-timeout 5 --max-time 20 -G "${LOKI_URL}/loki/api/v1/query" \
        --data-urlencode "query=sum(count_over_time({container=\"${container}\"}[24h]))" \
        | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo 0)
    log_errors=$(curl -sSf --connect-timeout 5 --max-time 20 -G "${LOKI_URL}/loki/api/v1/query" \
        --data-urlencode "query=sum(count_over_time({container=\"${container}\"} | json | level=~\"Error|Fatal\"[24h]))" \
        | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo 0)
    log_warnings=$(curl -sSf --connect-timeout 5 --max-time 20 -G "${LOKI_URL}/loki/api/v1/query" \
        --data-urlencode "query=sum(count_over_time({container=\"${container}\"} | json | level=\"Warning\"[24h]))" \
        | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo 0)
    series_json=$(echo "$series_json" | jq --argjson ll "${log_lines:-0}" --argjson le "${log_errors:-0}" --argjson lw "${log_warnings:-0}" \
        '. + {log_lines_24h: $ll, log_errors_24h: $le, log_warnings_24h: $lw}')

    # ── loki: error lines grouped by SourceContext + message prefix ──────
    # query_loki_patterns 404s on this Loki — build the grouping by hand
    # (AGENT-FLOWS-REDESIGN.md §1.8/§6.1).
    local start_24h error_groups_json
    start_24h=$(date -u -d '24 hours ago' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v-24H +%Y-%m-%dT%H:%M:%SZ)
    error_groups_json=$(curl -sSf --connect-timeout 5 --max-time 20 -G "${LOKI_URL}/loki/api/v1/query_range" \
        --data-urlencode "query={container=\"${container}\"} | json | level=~\"Error|Fatal\"" \
        --data-urlencode "start=${start_24h}" \
        --data-urlencode "limit=500" \
        | jq -c '
            [.data.result[].values[] | .[1]]
            | map(try (fromjson) catch {RenderedMessage: ., SourceContext: "unknown"})
            | map({
                key: ((.SourceContext // "unknown") + "|" + ((.RenderedMessage // .message // .msg // "") | .[0:80]))
              })
            | group_by(.key)
            | map({key: .[0].key, value: length})
            | from_entries
        ' 2>/dev/null || echo '{}')

    # ── argocd / prometheus: pod health (direct rules, not baseline-relative) ─
    local argo_response sync_status health_status deployed_image
    argo_response=$(curl -sSf --connect-timeout 5 --max-time 20 "${ARGOCD_URL}/api/v1/applications/${app_name}" -H "${auth_header}")
    sync_status=$(echo "$argo_response" | jq -r '.status.sync.status // "Unknown"')
    health_status=$(echo "$argo_response" | jq -r '.status.health.status // "Unknown"')
    # First deployed image, used as the `docker-image` input to _sre-agent.yml
    # for a monitor-triggered P1 escalation (see monitor.yml escalate-<bot> jobs).
    deployed_image=$(echo "$argo_response" | jq -r '(.status.summary.images // [])[0] // ""')

    local restarts ready desired
    restarts=$(curl -sSf --connect-timeout 5 --max-time 20 -G "${PROMETHEUS_URL}/api/v1/query" \
        --data-urlencode "query=sum(kube_pod_container_status_restarts_total{container=\"${container}\"})" \
        | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo 0)
    ready=$(curl -sSf --connect-timeout 5 --max-time 20 -G "${PROMETHEUS_URL}/api/v1/query" \
        --data-urlencode "query=sum(kube_pod_status_ready{pod=~\"${container}.*\",condition=\"true\"})" \
        | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo 0)
    desired=$(curl -sSf --connect-timeout 5 --max-time 20 -G "${PROMETHEUS_URL}/api/v1/query" \
        --data-urlencode "query=sum(kube_deployment_spec_replicas{deployment=~\"${container}.*\"})" \
        | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo 0)

    local pods_json
    pods_json=$(jq -n --argjson restarts "${restarts:-0}" --argjson ready "${ready:-0}" --argjson desired "${desired:-0}" \
        --arg sync "$sync_status" --arg health "$health_status" --arg image "$deployed_image" \
        '{restarts: $restarts, ready_replicas: $ready, desired_replicas: $desired, argocd_sync: $sync, argocd_health: $health, deployed_image: $image}')

    jq -n --argjson series "$series_json" --argjson errg "$error_groups_json" --argjson pods "$pods_json" --argjson sources "$sources_json" \
        '{series: $series, error_groups: $errg, pods: $pods, sources: $sources}'
}

# ─── append ────────────────────────────────────────────────────────────────

cmd_append() {
    local bot="${1:?usage: baseline.sh append <bot> <state-dir> [<gather-json-file>|-]}"
    local state_dir="${2:?usage: baseline.sh append <bot> <state-dir> [<gather-json-file>|-]}"
    local input="${3:--}"

    local gather_json
    if [ "$input" = "-" ]; then
        gather_json=$(cat)
    else
        gather_json=$(cat "$input")
    fi

    if ! echo "$gather_json" | jq -e . >/dev/null 2>&1; then
        log "ERROR: append input is not valid JSON for bot=${bot}"
        exit 1
    fi

    local now month bot_dir out_file
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    month="$(date -u +%Y-%m)"
    bot_dir="${state_dir}/${bot}"
    out_file="${bot_dir}/${month}.jsonl"

    mkdir -p "$bot_dir"

    local line
    line=$(echo "$gather_json" | jq -c --arg bot "$bot" --arg ts "$now" --arg run_id "${GITHUB_RUN_ID:-local}" \
        '{bot: $bot, ts: $ts, run_id: $run_id} + .')

    echo "$line" >> "$out_file"
    log "appended run for bot=${bot} to ${out_file}"
}

# ─── stats ─────────────────────────────────────────────────────────────────

# The jq statistics core. Pure function of the input array of run-objects
# (already sorted by ts ascending) — no network, no wall-clock. Exposed as
# its own function so the CLI plumbing (globbing files, sorting) stays
# separate from the arithmetic under test.
_stats_jq() {
    jq -s '
        def median(a):
            (a | length) as $n
            | if $n == 0 then null
              else (a | sort) as $s
              | if ($n % 2) == 1 then $s[($n - 1) / 2 | floor]
                else ($s[($n / 2 | floor) - 1] + $s[$n / 2 | floor]) / 2
                end
              end;

        def mean(a):
            if (a | length) == 0 then null else (a | add) / (a | length) end;

        def sample_stdev(a):
            (a | length) as $n
            | if $n < 2 then null
              else mean(a) as $m
              | ([a[] | (. - $m) * (. - $m)] | add / ($n - 1)) as $variance
              | ($variance | sqrt)
              end;

        (sort_by(.ts)) as $all
        | if ($all | length) == 0 then
            {error: "no history at all for this bot"}
          else
            $all[-1] as $current
            | ($current.ts | fromdateiso8601) as $now_epoch
            | ($all[:-1]) as $hist
            | (($current.series // {}) | keys) as $keys
            | {
                bot: $current.bot,
                current_ts: $current.ts,
                total_runs: ($all | length),
                series: (
                    reduce $keys[] as $k (
                        {};
                        . + {
                            ($k): (
                                ($hist
                                    | map(select(.series[$k] != null and (.ts | fromdateiso8601) >= ($now_epoch - 28 * 86400)))
                                ) as $h28
                                | ($h28 | map(select((.ts | fromdateiso8601) >= ($now_epoch - 7 * 86400)))) as $h7
                                | ($h28 | map(.series[$k])) as $v28
                                | ($h7 | map(.series[$k])) as $v7
                                | ($h28 | map(.ts[0:10]) | unique | length) as $history_days
                                | (median($v7)) as $med7
                                | (median($v28)) as $med28
                                | (mean($v28)) as $mean28
                                | (sample_stdev($v28)) as $sd28
                                | ($current.series[$k]) as $cur
                                | {
                                    current: $cur,
                                    median_7d: $med7,
                                    median_28d: $med28,
                                    ratio_vs_28d: (if ($med28 == null or $med28 == 0) then null else ($cur / $med28) end),
                                    z_score_28d: (if ($sd28 == null or $sd28 == 0) then null else (($cur - $mean28) / $sd28) end),
                                    history_days_28d: $history_days,
                                    low_confidence: ($history_days < 14),
                                    emerged_from_zero: (($med28 == 0 or $med28 == null) and $cur > 0)
                                }
                            )
                        }
                    )
                )
            }
          end
    '
}

cmd_stats() {
    local bot="${1:?usage: baseline.sh stats <bot> <state-dir>}"
    local state_dir="${2:?usage: baseline.sh stats <bot> <state-dir>}"
    local bot_dir="${state_dir}/${bot}"

    if [ ! -d "$bot_dir" ]; then
        log "WARNING: no baseline history directory for bot=${bot} at ${bot_dir} — first-run case, reporting empty history"
        jq -n --arg bot "$bot" '{bot: $bot, error: "no history at all for this bot"}'
        return 0
    fi

    shopt -s nullglob
    local files=("${bot_dir}"/*.jsonl)
    shopt -u nullglob
    if [ "${#files[@]}" -eq 0 ]; then
        log "WARNING: no baseline history files for bot=${bot} in ${bot_dir} — first-run case"
        jq -n --arg bot "$bot" '{bot: $bot, error: "no history at all for this bot"}'
        return 0
    fi

    cat "${files[@]}" | _stats_jq
}

# ─── commit ────────────────────────────────────────────────────────────────

cmd_commit() {
    local state_dir="${1:?usage: baseline.sh commit <state-dir> <commit-message>}"
    local message="${2:?usage: baseline.sh commit <state-dir> <commit-message>}"

    git -C "$state_dir" add -A
    if git -C "$state_dir" diff --cached --quiet; then
        log "nothing to commit in ${state_dir} — agent-state unchanged this run"
        return 0
    fi
    git -C "$state_dir" -c user.name="agent-state-bot" -c user.email="agent-state-bot@users.noreply.github.com" \
        commit -m "$message"
    git -C "$state_dir" push origin HEAD:agent-state
}

case "$SUBCOMMAND" in
    gather) cmd_gather "$@" ;;
    append) cmd_append "$@" ;;
    stats)  cmd_stats "$@" ;;
    commit) cmd_commit "$@" ;;
    *) usage ;;
esac
