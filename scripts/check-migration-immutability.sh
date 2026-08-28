#!/usr/bin/env bash

# Fails if a PR edits/deletes/renames a migration file already on the base branch.
# --range A...B diffs against the merge-base, so files added-then-edited within the same PR still pass.
set -euo pipefail

MIGRATION_DIRS=(
  "src/vahter-bot/migrations"
  "src/coupon-hub-bot/migrations"
  "src/alita-bot/migrations"
)

mode="${1:-}"
case "$mode" in
  --range)
    range="${2:?--range requires <A...B>}"
    a_ref="${range%%...*}"
    b_ref="${range##*...}"
    before_ref=$(git merge-base "$a_ref" "$b_ref")
    after_ref="$b_ref"
    ;;
  *)
    echo "usage: $0 --range <A...B>" >&2
    exit 2
    ;;
esac

offenders=$(git diff --no-ext-diff --no-color -M --diff-filter=MDR --name-status \
  "$before_ref" "$after_ref" -- "${MIGRATION_DIRS[@]}")

if [ -z "$offenders" ]; then
  echo "migration-immutability: OK — no edited/deleted/renamed migration files"
  exit 0
fi

echo "::error::Applied migrations are immutable — add a new migration instead of editing/deleting/renaming an existing one. Offending files:"
echo "$offenders"
exit 1
