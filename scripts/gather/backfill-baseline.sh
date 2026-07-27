#!/usr/bin/env bash
# backfill-baseline.sh <bot> <state-dir> [days=28]
#
# Reconstructs ~<days> days of DAILY rollups for <bot> into the `agent-state`
# baseline history, so the monitor role's 7d/28d medians are warm on day one
# instead of after a month of real runs accumulate. See
# AGENT-FLOWS-REDESIGN.md §3.3 ("Retention in Loki is at least ~90 days, so
# the first ~4 weeks can be backfilled from Loki/Prometheus rather than
# waiting a month for baselines to warm up") and deliverable 2 of the Phase 2
# brief.
#
# *** UNVERIFIED AGAINST REAL DATA — see report. This agent has no VPN/prod
# access; the script has been reviewed, bash -n'd and shellcheck'd, but never
# executed against real Loki/Prometheus/Postgres. Run it once by hand
# (workflow_dispatch) and inspect the result before relying on it. ***
#
# What it backfills, and a deliberate scope note: the brief names Loki and
# Prometheus as the ~90-day-retention sources to backfill from. This script
# ALSO backfills the postgres-sourced domain series (event/coupon_event/
# message_log/llm_usage) — those tables are the PRIMARY tracked series for
# vahter/coupon per §6.1 and have no meaningful retention limit (they are
# the live production tables, not a metrics TSDB), so leaving them out would
# warm only the log-volume/error baselines and leave every domain series
# (messages, bans, funnel counts, ...) cold for a full month regardless.
# Prometheus-sourced pod/restart history is NOT backfilled — Kubernetes
# state (restarts, replica counts) is evaluated as a DIRECT rule on the
# LIVE current run in monitor.yml, never baseline-relative (see monitor.md),
# so there is nothing to warm there.
#
# Known gap (see report deliverable 3): coupon's `user_feedback_7d` series
# (baseline.sh gather: a 7-day TRAILING count, unlike every other series
# here which is a plain 24h-per-calendar-day bucket) is deliberately NOT
# backfilled — computing a correct rolling 7-day sum per historical day is
# a materially different query shape, and there is no live data available
# to this agent to validate it against. `chat_message_24h` (same 24h-bucket
# shape as everything else) IS backfilled.
#
# One JSONL line PER CALENDAR DAY (UTC, 00:00–24:00) is written, with
# "run_id":"backfill" and "origin":"backfill", timestamped at that day's
# 23:59:59Z so it sorts correctly among real per-run entries and is
# correctly excluded/included by baseline.sh stats' trailing-window math.
#
# Idempotent: re-running for the same day range REPLACES only the prior
# "origin":"backfill" lines for those days — real per-run lines appended by
# the monitor workflow (any run_id other than "backfill") are never touched
# or deleted. Safe to re-run after a bad partial run.
#
# Runnable as a workflow_dispatch job (a thin wrapper workflow can call this
# directly — see report). Requires the same env vars as
# scripts/gather/baseline.sh gather: PROMETHEUS_URL, LOKI_URL, DB_PROD_HOST,
# DB_PROD_USERNAME, DB_PROD_PASSWORD. ARGOCD_URL/ARGOCD_AUTH_TOKEN are not
# required (no ArgoCD backfill, see above).
#
# Dry run: set DRY_RUN=1 (or "true") to compute everything exactly as a real
# run would (same queries, same preflight, fails loud the same way on an
# unreachable source), but stop before the write step — no mkdir, no file
# write, no git state touched. Instead it prints the exact JSONL line(s) it
# would (re)write for this bot plus a summary (line count, date range,
# how many days already have a backfill entry that would be refreshed).

set -euo pipefail

# shellcheck source=scripts/gather/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

BOT="${1:?usage: backfill-baseline.sh <bot> <state-dir> [days=28]}"
STATE_DIR="${2:?usage: backfill-baseline.sh <bot> <state-dir> [days=28]}"
DAYS="${3:-28}"

case "${DRY_RUN:-0}" in
    1|true|TRUE|True) DRY_RUN=1 ;;
    *) DRY_RUN=0 ;;
esac

: "${LOKI_URL:?LOKI_URL is required}"
: "${DB_PROD_HOST:?DB_PROD_HOST is required}"
: "${DB_PROD_USERNAME:?DB_PROD_USERNAME is required}"
: "${DB_PROD_PASSWORD:?DB_PROD_PASSWORD is required}"

if ! bot_exists "$BOT"; then
    log "ERROR: bot '${BOT}' is not present in ${BOTS_YML}"
    exit 1
fi

CONTAINER="$(bot_field "$BOT" '.container')"
DB_NAME="$(bot_field "$BOT" '.db_name')"
DATABASE_URL="postgresql://${DB_PROD_USERNAME}:${DB_PROD_PASSWORD}@${DB_PROD_HOST}/${DB_NAME}"

log "backfill-baseline: bot=${BOT} state_dir=${STATE_DIR} days=${DAYS} container=${CONTAINER} db=${DB_NAME} dry_run=${DRY_RUN}"

# ── preflight (fail loud — a backfill built on an unreachable source would
# silently seed false "everything is zero" history, exactly the class of bug
# this whole redesign exists to kill) ─────────────────────────────────────
probe "postgres" env PGCONNECT_TIMEOUT=5 psql "${DATABASE_URL}" -v ON_ERROR_STOP=1 -t -A -c "SELECT 1;" >/dev/null || {
    log "ERROR: postgres unreachable for bot=${BOT} — aborting backfill (never seed baseline history from a degraded source)"
    exit 1
}
probe "loki" curl -sSf --connect-timeout 5 --max-time 20 -G "${LOKI_URL}/loki/api/v1/labels" >/dev/null || {
    log "ERROR: loki unreachable for bot=${BOT} — aborting backfill"
    exit 1
}

# ── postgres: one query returns all <=28 day-bucketed rows at once ───────
log "Querying ${DAYS} days of day-bucketed domain series from postgres (db=${DB_NAME})..."

PG_ROWS_JSON="[]"
case "$BOT" in
    vahter)
        PG_ROWS_JSON=$(psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -t -A -F $'\t' -c "
            SELECT
                date_trunc('day', created_at)::date AS day,
                count(*) FILTER (WHERE event_type = 'MessageReceived') AS messages_received,
                count(DISTINCT (data->>'userId')::bigint) FILTER (WHERE event_type = 'MessageReceived') AS unique_senders,
                count(*) FILTER (WHERE event_type = 'UserBanned') AS user_banned,
                count(*) FILTER (WHERE event_type = 'VahterActed') AS vahter_acted,
                count(*) FILTER (WHERE event_type = 'LlmClassified' AND data->>'verdict' = 'SPAM') AS llm_classified_spam,
                count(*) FILTER (WHERE event_type = 'MlScoredMessage' AND (data->>'isSpam')::bool) AS ml_scored_spam,
                count(*) FILTER (WHERE event_type = 'CallbackCreated') AS callback_created,
                count(*) FILTER (WHERE event_type = 'CallbackResolved') AS callback_resolved,
                count(*) FILTER (WHERE event_type = 'MessageMarkedHam') AS message_marked_ham,
                count(*) FILTER (WHERE event_type = 'MessageMarkedSpam') AS message_marked_spam
            FROM event
            WHERE created_at >= NOW() - INTERVAL '${DAYS} days'
            GROUP BY day
            ORDER BY day;
        " | jq -R -s '
            split("\n") | map(select(length > 0)) | map(split("\t")) | map({
                day: .[0],
                messages_received_24h: (.[1] | tonumber),
                unique_senders_24h: (.[2] | tonumber),
                user_banned_24h: (.[3] | tonumber),
                vahter_acted_24h: (.[4] | tonumber),
                llm_classified_spam_24h: (.[5] | tonumber),
                ml_scored_spam_24h: (.[6] | tonumber),
                callback_created_24h: (.[7] | tonumber),
                callback_resolved_24h: (.[8] | tonumber),
                message_marked_ham_24h: (.[9] | tonumber),
                message_marked_spam_24h: (.[10] | tonumber)
            })')
        ;;
    coupon)
        PG_ROWS_JSON=$(psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -t -A -F $'\t' -c "
            SELECT
                date_trunc('day', created_at)::date AS day,
                count(*) FILTER (WHERE event_type = 'added') AS added,
                count(*) FILTER (WHERE event_type = 'taken') AS taken,
                count(*) FILTER (WHERE event_type = 'used') AS used,
                count(*) FILTER (WHERE event_type = 'returned') AS returned,
                count(*) FILTER (WHERE event_type = 'voided') AS voided,
                count(DISTINCT user_id) AS unique_users
            FROM coupon_event
            WHERE created_at >= NOW() - INTERVAL '${DAYS} days'
            GROUP BY day
            ORDER BY day;
        " | jq -R -s '
            split("\n") | map(select(length > 0)) | map(split("\t")) | map({
                day: .[0],
                added_24h: (.[1] | tonumber),
                taken_24h: (.[2] | tonumber),
                used_24h: (.[3] | tonumber),
                returned_24h: (.[4] | tonumber),
                voided_24h: (.[5] | tonumber),
                unique_users_24h: (.[6] | tonumber)
            })')
        # baseline.sh gather also tracks chat_message_24h (day-bucketable the
        # same way as the coupon_event series above) and user_feedback_7d
        # (a 7-day TRAILING window, a different shape entirely — see report
        # deliverable 3: deliberately NOT backfilled here, flagged as a known
        # gap rather than risking a wrong rolling-window query with no live
        # data to validate it against).
        CHAT_ROWS_JSON=$(psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -t -A -F $'\t' -c "
            SELECT
                date_trunc('day', created_at)::date AS day,
                count(*) AS chat_message_count
            FROM chat_message
            WHERE created_at >= NOW() - INTERVAL '${DAYS} days'
            GROUP BY day
            ORDER BY day;
        " | jq -R -s '
            split("\n") | map(select(length > 0)) | map(split("\t")) | map({
                day: .[0],
                chat_message_24h: (.[1] | tonumber)
            })')
        # Merge coupon_event and chat_message rows by day (full outer join, missing side = 0).
        PG_ROWS_JSON=$(jq -n --argjson a "$PG_ROWS_JSON" --argjson b "$CHAT_ROWS_JSON" '
            ($a + $b) | group_by(.day) | map(reduce .[] as $r ({}; . + $r))
        ')
        ;;
    alita)
        PG_ROWS_JSON=$(psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -t -A -F $'\t' -c "
            SELECT
                date_trunc('day', sent_at)::date AS day,
                count(*) AS message_count
            FROM message_log
            WHERE sent_at >= NOW() - INTERVAL '${DAYS} days'
            GROUP BY day
            ORDER BY day;
        " | jq -R -s '
            split("\n") | map(select(length > 0)) | map(split("\t")) | map({
                day: .[0],
                message_log_24h: (.[1] | tonumber)
            })')
        LLM_ROWS_JSON=$(psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -t -A -F $'\t' -c "
            SELECT
                date_trunc('day', called_at)::date AS day,
                count(*) AS call_count,
                coalesce(sum(cost_usd), 0) AS total_cost_usd
            FROM llm_usage
            WHERE called_at >= NOW() - INTERVAL '${DAYS} days'
            GROUP BY day
            ORDER BY day;
        " | jq -R -s '
            split("\n") | map(select(length > 0)) | map(split("\t")) | map({
                day: .[0],
                llm_calls_24h: (.[1] | tonumber),
                llm_cost_usd_24h: (.[2] | tonumber)
            })')
        # Merge message_log and llm_usage rows by day (full outer join, missing side = 0).
        PG_ROWS_JSON=$(jq -n --argjson a "$PG_ROWS_JSON" --argjson b "$LLM_ROWS_JSON" '
            ($a + $b) | group_by(.day) | map(reduce .[] as $r ({}; . + $r))
        ')
        ;;
    *)
        log "WARNING: bot=${BOT} has no known backfill query — postgres series will be empty for every day"
        ;;
esac

# ── loki: per-day log volume / error / warning counts (90d retention) ────
# Loki has no single query returning day-bucketed history the way postgres
# does here, and query_loki_patterns 404s on this Loki (§1.8) — so this
# loops one absolute-time instant-query per day. 3 queries * up to 28 days
# = <=84 requests; each already retried by retry_curl.
log "Querying ${DAYS} days of Loki log volume/error/warning counts (container=${CONTAINER})..."

LOKI_ROWS_JSON="[]"
for offset in $(seq 1 "$DAYS"); do
    day=$(date -u -d "${offset} days ago" +%Y-%m-%d 2>/dev/null || date -u -v-"${offset}"d +%Y-%m-%d)
    day_end="${day}T23:59:59Z"

    lines=$(retry_curl "Loki(lines,${day})" -G "${LOKI_URL}/loki/api/v1/query" \
        --data-urlencode "query=sum(count_over_time({container=\"${CONTAINER}\"}[24h]))" \
        --data-urlencode "time=${day_end}" \
        | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0')
    errors=$(retry_curl "Loki(errors,${day})" -G "${LOKI_URL}/loki/api/v1/query" \
        --data-urlencode "query=sum(count_over_time({container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\"[24h]))" \
        --data-urlencode "time=${day_end}" \
        | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0')
    warnings=$(retry_curl "Loki(warnings,${day})" -G "${LOKI_URL}/loki/api/v1/query" \
        --data-urlencode "query=sum(count_over_time({container=\"${CONTAINER}\"} | json | level=\"Warning\"[24h]))" \
        --data-urlencode "time=${day_end}" \
        | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0')

    LOKI_ROWS_JSON=$(jq -n --argjson acc "$LOKI_ROWS_JSON" --arg day "$day" \
        --argjson lines "${lines:-0}" --argjson errors "${errors:-0}" --argjson warnings "${warnings:-0}" \
        '$acc + [{day: $day, log_lines_24h: $lines, log_errors_24h: $errors, log_warnings_24h: $warnings}]')
done

# ── merge postgres + loki rows by day; missing side defaults to 0 series ──
MERGED_JSON=$(jq -n --argjson pg "$PG_ROWS_JSON" --argjson lk "$LOKI_ROWS_JSON" '
    ($pg + $lk)
    | group_by(.day)
    | map(reduce .[] as $r ({}; . + $r))
    | sort_by(.day)
')

DAY_COUNT=$(echo "$MERGED_JSON" | jq 'length')
log "Backfill computed ${DAY_COUNT} day(s) of rollups for bot=${BOT}."

# ── idempotent write: drop prior origin=backfill lines for the days we are
# about to (re)write, in every existing month file for this bot, then
# append the freshly computed backfill lines, grouped back by month ───────
# NOTE: mkdir is skipped entirely in DRY_RUN — a dry run must not create so
# much as an empty directory. Existing lines are still read (read-only) if
# the directory already exists, so the "already present" count below is
# accurate.
BOT_DIR="${STATE_DIR}/${BOT}"
if [ "$DRY_RUN" != "1" ]; then
    mkdir -p "$BOT_DIR"
fi

EXISTING_JSON="[]"
if [ -d "$BOT_DIR" ]; then
    shopt -s nullglob
    existing_files=("${BOT_DIR}"/*.jsonl)
    shopt -u nullglob
    if [ "${#existing_files[@]}" -gt 0 ]; then
        EXISTING_JSON=$(cat "${existing_files[@]}" | jq -s '.')
    fi
fi

DAYS_BEING_WRITTEN=$(echo "$MERGED_JSON" | jq -c '[.[].day]')

KEPT_JSON=$(jq -n --argjson existing "$EXISTING_JSON" --argjson days "$DAYS_BEING_WRITTEN" '
    $existing | map(select(
        (.run_id != "backfill") or ((.ts[0:10]) as $d | ($days | index($d) | not))
    ))
')

NEW_LINES_JSON=$(echo "$MERGED_JSON" | jq -c --arg bot "$BOT" '
    .[] | {
        bot: $bot,
        ts: (.day + "T23:59:59Z"),
        run_id: "backfill",
        origin: "backfill",
        series: (del(.day)),
        error_groups: {},
        pods: {},
        sources: {postgres: "ok", loki: "ok", argocd: "not_backfilled", prometheus: "not_backfilled"}
    }
')

ALREADY_PRESENT=$(jq -n --argjson existing "$EXISTING_JSON" --argjson days "$DAYS_BEING_WRITTEN" '
    [$existing[] | select(.run_id == "backfill") | .ts[0:10]]
    | map(select(. as $d | $days | index($d)))
    | unique
    | length
')
NEW_COUNT=$((DAY_COUNT - ALREADY_PRESENT))
DATE_MIN=$(echo "$MERGED_JSON" | jq -r '[.[].day] | if length == 0 then "n/a" else min end')
DATE_MAX=$(echo "$MERGED_JSON" | jq -r '[.[].day] | if length == 0 then "n/a" else max end')

if [ "$DRY_RUN" = "1" ]; then
    log "DRY RUN: bot=${BOT} — no files written, nothing committed/pushed."
    log "DRY RUN: bot=${BOT} — would (re)write ${DAY_COUNT} line(s) to ${STATE_DIR}/${BOT}/, covering ${DATE_MIN}..${DATE_MAX}. ${ALREADY_PRESENT} day(s) already have a backfill entry (would be refreshed in place), ${NEW_COUNT} day(s) are new."
    log "DRY RUN: bot=${BOT} — exact JSONL line(s) that would be (re)written:"
    echo "$NEW_LINES_JSON"
    log "DRY RUN complete for bot=${BOT}. Re-run with DRY_RUN unset (or 0) to actually write + push."
    exit 0
fi

ALL_JSON=$(jq -n --argjson kept "$KEPT_JSON" --argjson new "[$(echo "$NEW_LINES_JSON" | paste -sd, -)]" '
    ($kept + $new) | sort_by(.ts)
')

# Regroup by month (YYYY-MM from ts) and rewrite each month file in full —
# backfill is a rare, explicit maintenance operation (workflow_dispatch
# only), unlike the steady append-only flow in baseline.sh append.
MONTHS=$(echo "$ALL_JSON" | jq -r '[.[].ts[0:7]] | unique | .[]')
for month in $MONTHS; do
    out_file="${BOT_DIR}/${month}.jsonl"
    echo "$ALL_JSON" | jq -c --arg m "$month" '.[] | select(.ts[0:7] == $m)' > "$out_file"
    log "wrote $(echo "$ALL_JSON" | jq --arg m "$month" '[.[] | select(.ts[0:7] == $m)] | length') line(s) to ${out_file}"
done

log "Backfill complete for bot=${BOT}: ${DAY_COUNT} day(s) written/refreshed across ${STATE_DIR}/${BOT}/ (${ALREADY_PRESENT} refreshed, ${NEW_COUNT} new), covering ${DATE_MIN}..${DATE_MAX}. UNVERIFIED against real data — inspect the output before trusting it."
