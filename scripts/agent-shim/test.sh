#!/usr/bin/env bash
# Exercises every rule in ./gh against a fake real gh on a scratch PATH.
set -uo pipefail

SHIM_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAKE_GH_DIR="$WORK/fake-gh"
mkdir -p "$FAKE_GH_DIR"
cat > "$FAKE_GH_DIR/gh" << 'FAKE'
#!/usr/bin/env bash
set -euo pipefail
echo "$*" >> "${FAKE_GH_CALLS:?}"
if [ "$1" = "api" ]; then
  case "$2" in
    */comments) cat "${FAKE_GH_COMMENTS_JSON:?}" ;;
    */timeline) cat "${FAKE_GH_TIMELINE_JSON:?}" ;;
    *) echo "[]" ;;
  esac
fi
exit 0
FAKE
chmod +x "$FAKE_GH_DIR/gh"

export PATH="$SHIM_DIR:$FAKE_GH_DIR:$PATH"
export GITHUB_REPOSITORY="org/repo"

empty_json="$WORK/empty.json"
echo '[]' > "$empty_json"

human_comments="$WORK/human-comments.json"
echo '[{"user":{"login":"alice"},"created_at":"2020-01-01T00:00:00Z"}]' > "$human_comments"

recent_ts="$(date -u -d '-1 day' +%Y-%m-%dT%H:%M:%SZ)"
recent_bot_comments="$WORK/recent-bot-comments.json"
echo "[{\"user\":{\"login\":\"github-actions[bot]\"},\"created_at\":\"${recent_ts}\"}]" > "$recent_bot_comments"

old_ts="$(date -u -d '-30 day' +%Y-%m-%dT%H:%M:%SZ)"
old_bot_comments="$WORK/old-bot-comments.json"
echo "[{\"user\":{\"login\":\"github-actions[bot]\"},\"created_at\":\"${old_ts}\"}]" > "$old_bot_comments"

pr_timeline="$WORK/pr-timeline.json"
echo '[{"event":"cross-referenced","source":{"issue":{"pull_request":{}}}}]' > "$pr_timeline"

pass=0
fail=0
STATUS=0

check() {
  local desc="$1" expect_status="$2" got_status="$3"
  if [ "$got_status" = "$expect_status" ]; then
    echo "ok - $desc"
    pass=$((pass + 1))
  else
    echo "NOT OK - $desc (expected exit $expect_status, got $got_status)"
    fail=$((fail + 1))
  fi
}

check_log_contains() {
  local desc="$1" pattern="$2" log="$3"
  if grep -qF "$pattern" "$log" 2>/dev/null; then
    echo "ok - $desc"
    pass=$((pass + 1))
  else
    echo "NOT OK - $desc (expected '$pattern' in $log)"
    fail=$((fail + 1))
  fi
}

# gh_run CALLS_LOG COMMENTS_JSON TIMELINE_JSON gh-args... ; sets $STATUS, never
# trips set -e (a REFUSED case exits non-zero on purpose).
gh_run() {
  local calls_log="$1" comments_json="$2" timeline_json="$3"; shift 3
  : > "$calls_log"
  if FAKE_GH_CALLS="$calls_log" FAKE_GH_COMMENTS_JSON="$comments_json" FAKE_GH_TIMELINE_JSON="$timeline_json" \
      gh "$@" > /dev/null 2>"$WORK/last-stderr"; then
    STATUS=0
  else
    STATUS=$?
  fi
}

actions_log="$WORK/actions-1.log"; calls_log="$WORK/calls-1.log"
export AGENT_ACTIONS_LOG="$actions_log"; unset AGENT_LOG_ISSUE AGENT_COMMENT_THROTTLE_DAYS
gh_run "$calls_log" "$human_comments" "$empty_json" issue comment 100 --body-file x
check "human comment -> refused" 1 "$STATUS"
check_log_contains "human comment -> logged REFUSED" "REFUSED issue comment 100" "$actions_log"

actions_log="$WORK/actions-2.log"; calls_log="$WORK/calls-2.log"
export AGENT_ACTIONS_LOG="$actions_log"
gh_run "$calls_log" "$empty_json" "$pr_timeline" issue comment 101 --body-file x
check "PR cross-reference -> refused" 1 "$STATUS"
check_log_contains "PR cross-reference -> logged REFUSED" "REFUSED issue comment 101" "$actions_log"

actions_log="$WORK/actions-3.log"; calls_log="$WORK/calls-3.log"
export AGENT_ACTIONS_LOG="$actions_log" AGENT_COMMENT_THROTTLE_DAYS=14
gh_run "$calls_log" "$recent_bot_comments" "$empty_json" issue comment 102 --body-file x
check "recent bot comment -> refused" 1 "$STATUS"
check_log_contains "recent bot comment -> logged REFUSED" "REFUSED issue comment 102" "$actions_log"

actions_log="$WORK/actions-4.log"; calls_log="$WORK/calls-4.log"
export AGENT_ACTIONS_LOG="$actions_log" AGENT_COMMENT_THROTTLE_DAYS=14
gh_run "$calls_log" "$old_bot_comments" "$empty_json" issue comment 103 --body-file x
check "old bot comment only -> allowed" 0 "$STATUS"
check_log_contains "old bot comment only -> logged ALLOWED" "ALLOWED issue comment 103" "$actions_log"

actions_log="$WORK/actions-5.log"; calls_log="$WORK/calls-5.log"
export AGENT_ACTIONS_LOG="$actions_log" AGENT_LOG_ISSUE=999
gh_run "$calls_log" "$empty_json" "$empty_json" issue comment 999 --body-file x
check "log issue comment -> refused" 1 "$STATUS"
check_log_contains "log issue comment -> logged REFUSED" "REFUSED issue comment 999" "$actions_log"

actions_log="$WORK/actions-5b.log"; calls_log="$WORK/calls-5b.log"
export AGENT_ACTIONS_LOG="$actions_log" AGENT_LOG_ISSUE=999
gh_run "$calls_log" "$empty_json" "$empty_json" issue close 999
check "log issue close -> refused" 1 "$STATUS"
check_log_contains "log issue close -> logged REFUSED" "REFUSED issue close 999" "$actions_log"

actions_log="$WORK/actions-6.log"; calls_log="$WORK/calls-6.log"
export AGENT_ACTIONS_LOG="$actions_log"; unset AGENT_LOG_ISSUE
gh_run "$calls_log" "$empty_json" "$empty_json" issue create --title t --body-file x
check "issue create -> allowed" 0 "$STATUS"
check_log_contains "issue create -> logged ALLOWED" "ALLOWED issue create" "$actions_log"

actions_log="$WORK/actions-6b.log"; calls_log="$WORK/calls-6b.log"
export AGENT_ACTIONS_LOG="$actions_log"
gh_run "$calls_log" "$empty_json" "$empty_json" issue close 200
check "issue close (non-log-issue) -> allowed" 0 "$STATUS"
check_log_contains "issue close -> logged ALLOWED" "ALLOWED issue close 200" "$actions_log"

actions_log="$WORK/actions-7.log"; calls_log="$WORK/calls-7.log"
export AGENT_ACTIONS_LOG="$actions_log"
gh_run "$calls_log" "$empty_json" "$empty_json" issue list --label project
check "unrelated command -> passthrough exits ok" 0 "$STATUS"
check_log_contains "unrelated command -> logged PASSTHROUGH" "PASSTHROUGH issue list" "$actions_log"
check_log_contains "unrelated command -> reached fake gh" "issue list --label project" "$calls_log"

echo
echo "test.sh: ${pass} passed, ${fail} failed"
if [ "$fail" -gt 0 ]; then
  exit 1
fi
