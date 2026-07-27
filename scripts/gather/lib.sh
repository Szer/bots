#!/usr/bin/env bash
# lib.sh — shared helpers for scripts/gather/{runtime,product}.sh.
#
# Source this, don't execute it:
#   source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
#
# Provides:
#   log                 timestamped stderr logger
#   retry_curl          fail-loud retrying curl wrapper (lifted from the
#                       Phase-0 gather-metrics.sh — a failed request, after
#                       retries, is fatal: it prints curl's real error and
#                       exits non-zero rather than substituting an empty
#                       result)
#   probe               non-fatal connectivity check that returns "ok" or
#                       "unreachable: <error>" so a manifest can be built
#                       BEFORE deciding whether to fail loud
#   emit_manifest       prints the {"bot":...,"sources":{...}} manifest line
#   bots_yml_json       the full .github/bots.yml registry as JSON (yq if
#                       present, else python3+PyYAML — see below)
#   bot_field           read one field of one bot's registry entry via jq
#
# set -euo pipefail is intentionally NOT set here — this file is sourced by
# scripts that set their own strict-mode flags; sourcing shouldn't surprise
# a caller with a stricter shell than it asked for.

GATHER_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BOTS_YML="${BOTS_YML:-${GATHER_LIB_DIR}/../../.github/bots.yml}"

log() { echo "[$(date -u +%H:%M:%S)] $*" >&2; }

# retry_curl LABEL curl-args...
# Stderr is left unredirected so curl's own diagnostic ("Could not resolve
# host", "Connection refused", the failing HTTP status, etc) reaches the
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

# probe NAME cmd [args...]
# Runs `cmd args...` once as a connectivity check. NEVER aborts the script —
# echoes "ok" (and returns 0) on success, or "unreachable: <first line of
# stderr>" (and returns 1) on failure. Callers use this to build the
# {"sources":...} manifest for ALL sources before deciding whether any
# required one is down; "source unreachable" must never be swallowed into
# looking like "everything is fine" (see AGENT-FLOWS-REDESIGN.md §1.3/§3.2).
probe() {
    local name="$1"
    shift
    local err status
    # The exit status of `if CMD; then ... fi` is always 0 when the CMD
    # branch is false and there is no else (POSIX: an if with no executed
    # branch exits 0) — capture the real status INSIDE an else branch,
    # where $? still reflects the failing command, not after `fi`.
    if err=$("$@" 2>&1 >/dev/null); then
        echo "ok"
        return 0
    else
        status=$?
    fi
    log "PROBE FAILED [${name}] (exit ${status}): ${err}"
    # Keep the manifest value short and single-line.
    echo "unreachable: $(printf '%s' "$err" | head -1 | cut -c1-200)"
    return 1
}

# probe_optional NAME cmd [args...]
# Same mechanics as probe() (never aborts, echoes a single-line status), but
# for a source that is OPTIONAL evidence — enrichment, not core evidence. A
# failure here must never skip the bot the way a REQUIRED probe() failure
# does. The `degraded(optional): ...` prefix (vs. probe()'s `unreachable:
# ...`) is the load-bearing, data-driven distinction: a guard's "is this bot
# clean?" check can `select(.value != "ok" and (.value | startswith
# ("degraded(optional):") | not))` to skip on any REQUIRED failure while
# deliberately ignoring any OPTIONAL one, regardless of which source key it
# is — see monitor.yml's guard step and monitor.md. Currently used for the
# ArgoCD deploy-*history* fetch only (fixed 2026-07-27 after run 30233211533
# made this source's failure wrongly fail-loud and skip every bot).
probe_optional() {
    local name="$1"
    shift
    local err status
    if err=$("$@" 2>&1 >/dev/null); then
        echo "ok"
        return 0
    else
        status=$?
    fi
    log "PROBE DEGRADED (optional) [${name}] (exit ${status}): ${err}"
    echo "degraded(optional): $(printf '%s' "$err" | head -1 | cut -c1-200)"
    return 1
}

# emit_manifest BOT name1=status1 [name2=status2 ...]
# Prints one JSON line: {"bot":"...","generated_at":"...","sources":{...}}.
# Built with jq (not string concatenation) so status text containing quotes
# or the probe's own error text is always valid JSON.
emit_manifest() {
    local bot="$1"
    shift
    local now
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    local jq_args=(--arg bot "$bot" --arg now "$now")
    local src_filter="{}"
    local i=0
    local pair name status
    for pair in "$@"; do
        name="${pair%%=*}"
        status="${pair#*=}"
        jq_args+=(--arg "k${i}" "$name" --arg "v${i}" "$status")
        src_filter="(${src_filter} + {(\$k${i}): \$v${i}})"
        i=$((i + 1))
    done
    # -c is load-bearing: the manifest MUST be a single line, because every
    # consumer reads it with `head -n1`. Without -c, jq pretty-prints and
    # consumers get a bare "{" — see the 2026-07-27 incident where this made
    # monitor.yml's guard exit 2 and, worse, made product.yml's guard read a
    # degraded bot as CLEAN (its jq error was swallowed by `|| true`).
    jq -cn "${jq_args[@]}" "{bot: \$bot, generated_at: \$now, sources: (${src_filter})}"
}

# manifest_has_bad_source MANIFEST_JSON
# Returns 0 (true) if any REQUIRED source in the manifest is not "ok". A
# source whose status starts with `degraded(optional):` (see probe_optional()
# above) is deliberately ignored here — it is enrichment evidence, and its
# failure must degrade, not skip, the bot.
manifest_has_bad_source() {
    local bad
    bad=$(echo "$1" | jq -r '.sources | to_entries[] | select(.value != "ok") | select((.value | startswith("degraded(optional):")) | not) | .key')
    [ -n "$bad" ]
}

_ensure_pyyaml() {
    if python3 -c "import yaml" >/dev/null 2>&1; then
        return 0
    fi
    log "python3 'yaml' module not found — installing pyyaml (no yq on this runner)"
    if python3 -m pip install --quiet --user pyyaml >/dev/null 2>&1; then
        return 0
    fi
    log "ERROR: could not import PyYAML and could not install it, and no 'yq' binary is on PATH"
    return 1
}

# bots_yml_json — the entire bots.yml registry, converted to JSON on stdout.
# Prefers `yq` (portable, no dependency install) when present on the runner;
# falls back to python3 + PyYAML (installed on demand) for portability to
# runners that don't ship yq — GitHub's ubuntu-latest images do not.
bots_yml_json() {
    if command -v yq >/dev/null 2>&1; then
        yq -o=json '.' "$BOTS_YML"
        return $?
    fi
    _ensure_pyyaml || exit 1
    python3 -c '
import json, sys, yaml
with open(sys.argv[1], "r", encoding="utf-8") as f:
    print(json.dumps(yaml.safe_load(f)))
' "$BOTS_YML"
}

# bot_field BOT JQ_FILTER
# JQ_FILTER is evaluated relative to .bots[BOT], e.g. bot_field vahter '.container'
bot_field() {
    local bot="$1" filter="$2"
    bots_yml_json | jq -r --arg bot "$bot" ".bots[\$bot] | ${filter}"
}

# bot_exists BOT — 0 if BOT is a key under .bots in the registry
bot_exists() {
    local bot="$1"
    local found
    found=$(bots_yml_json | jq -r --arg bot "$bot" 'has("bots") and (.bots | has($bot))')
    [ "$found" = "true" ]
}

# all_bots_with_role ROLE — newline-separated bot keys whose roles include ROLE
all_bots_with_role() {
    local role="$1"
    bots_yml_json | jq -r --arg role "$role" '.bots | to_entries[] | select(.value.roles | index($role)) | .key'
}
