#!/usr/bin/env bash
# PreToolUse/Bash gate: block `git commit` invocations carrying --no-verify or -n.
set -euo pipefail

input=$(cat)

if command -v jq >/dev/null 2>&1; then
  cmd=$(printf '%s' "$input" | jq -r '.tool_input.command // empty')
else
  cmd=$(printf '%s' "$input" | grep -o '"command"[[:space:]]*:[[:space:]]*"[^"]*"' | head -n1 | sed -E 's/^"command"[[:space:]]*:[[:space:]]*"//; s/"$//')
fi

[ -z "$cmd" ] && exit 0

# statements that actually invoke `git ... commit`, stopping at shell separators
statements=$(printf '%s\n' "$cmd" | grep -oE '\bgit\b[^;&|]*\bcommit\b[^;&|]*' || true)
[ -z "$statements" ] && exit 0

while IFS= read -r stmt; do
  [ -z "$stmt" ] && continue
  if [[ "$stmt" =~ (^|[[:space:]])--no-verify([[:space:]]|$) ]] || [[ "$stmt" =~ (^|[[:space:]])-n([[:space:]]|$) ]]; then
    echo "commit hooks are mandatory in this repo; commit without --no-verify" >&2
    exit 2
  fi
done <<< "$statements"

exit 0
