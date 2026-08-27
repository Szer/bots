#!/usr/bin/env bash

# Ratio + comment-block-length gate. --cached (pre-commit) or --range A...B (CI).
# tokei before/after delta per language; thresholds in .comment-ratio.conf.
set -euo pipefail

TOKEI_VERSION="v12.1.2"
TOKEI_ASSET="tokei-x86_64-unknown-linux-musl.tar.gz"
TOKEI_URL="https://github.com/XAMPPRocky/tokei/releases/download/${TOKEI_VERSION}/${TOKEI_ASSET}"
TOKEI_SHA256="331e77046935d655dce8d97ebb943fcc7e9684586dadf3d197f3df5e760cd31b"
TOKEI_CACHE_DIR="${HOME}/.cache/comment-gate/tokei-${TOKEI_VERSION}"

DEFAULT_RATIO=0.15
DEFAULT_FLOOR=5
MAX_COMMENT_BLOCK=2
IGNORE_LANGUAGES=""

repo_root=$(git rev-parse --show-toplevel)
conf_file="${repo_root}/.comment-ratio.conf"
if [ -f "$conf_file" ]; then
  # shellcheck disable=SC1090
  . "$conf_file"
fi

if [ -n "${COMMENT_RATIO_MAX:-}" ]; then
  DEFAULT_RATIO="$COMMENT_RATIO_MAX"
fi
if [ -n "${COMMENT_RATIO_MIN_LINES:-}" ]; then
  DEFAULT_FLOOR="$COMMENT_RATIO_MIN_LINES"
fi

mode="${1:-}"
case "$mode" in
  --cached)
    if git rev-parse -q --verify HEAD >/dev/null; then
      before_ref=HEAD
    else
      before_ref=$(git hash-object -t tree /dev/null)
    fi
    name_status_args=(--cached -M --diff-filter=ACMR --name-status -z "$before_ref")
    ;;
  --range)
    range="${2:?--range requires <A...B>}"
    a_ref="${range%%...*}"
    b_ref="${range##*...}"
    before_ref=$(git merge-base "$a_ref" "$b_ref")
    after_ref="$b_ref"
    name_status_args=(-M --diff-filter=ACMR --name-status -z "$before_ref" "$after_ref")
    ;;
  *)
    echo "usage: $0 --cached | --range <A...B>" >&2
    exit 2
    ;;
esac

work_dir=$(mktemp -d)
trap 'rm -rf "$work_dir"' EXIT

# -z output must stay in a file, not a $(...) var — bash strings can't hold
# embedded NULs, capture would silently truncate at the first record.
name_status_file="$work_dir/name-status"
git diff --no-ext-diff --no-color "${name_status_args[@]}" > "$name_status_file"

if [ ! -s "$name_status_file" ]; then
  echo "comment-ratio: no changed files in diff, skipping"
  exit 0
fi

resolve_tokei() {
  # Only accept a PATH tokei if it's exactly 12.1.2 — other versions count
  # some constructs differently, and hook/CI/clones must agree bit-for-bit.
  if command -v tokei >/dev/null 2>&1; then
    local path_tokei path_version
    path_tokei=$(command -v tokei)
    path_version=$("$path_tokei" --version 2>/dev/null | awk '{print $2}')
    if [ "$path_version" = "12.1.2" ]; then
      printf '%s\n' "$path_tokei"
      return 0
    fi
  fi
  local bin="${TOKEI_CACHE_DIR}/tokei"
  if [ -x "$bin" ]; then
    printf '%s\n' "$bin"
    return 0
  fi
  mkdir -p "$TOKEI_CACHE_DIR"
  local tmp_tar
  tmp_tar="$work_dir/tokei.tar.gz"
  if ! curl -fsSL -o "$tmp_tar" "$TOKEI_URL"; then
    echo "comment-ratio: FAIL — could not download tokei from $TOKEI_URL" >&2
    exit 1
  fi
  local got_sha
  got_sha=$(sha256sum "$tmp_tar" | cut -d' ' -f1)
  if [ "$got_sha" != "$TOKEI_SHA256" ]; then
    echo "comment-ratio: FAIL — tokei tarball sha256 mismatch (got $got_sha, expected $TOKEI_SHA256)" >&2
    exit 1
  fi
  local extract_dir="$work_dir/tokei-extract"
  mkdir -p "$extract_dir"
  tar -xzf "$tmp_tar" -C "$extract_dir" tokei
  chmod +x "$extract_dir/tokei"
  mv "$extract_dir/tokei" "$bin"
  printf '%s\n' "$bin"
}

tokei_bin=$(resolve_tokei)

before_dir="$work_dir/before"
after_dir="$work_dir/after"
mkdir -p "$before_dir" "$after_dir"

read_after() {
  if [ "$mode" = "--cached" ]; then
    git show ":$1"
  else
    git show "$after_ref:$1"
  fi
}

declare -a offend_files=()
while IFS= read -r -d '' status; do
  case "$status" in
    R*|C*)
      IFS= read -r -d '' old_path
      IFS= read -r -d '' new_path
      ;;
    *)
      IFS= read -r -d '' old_path
      new_path="$old_path"
      ;;
  esac
  case "$status" in
    A*) : ;; # added files have no before-content
    *)
      mkdir -p "$before_dir/$(dirname "$old_path")"
      git show "$before_ref:$old_path" > "$before_dir/$old_path" 2>/dev/null || true
      ;;
  esac
  mkdir -p "$after_dir/$(dirname "$new_path")"
  read_after "$new_path" > "$after_dir/$new_path" 2>/dev/null || true
  offend_files+=("$new_path")
done < "$name_status_file"

diff_args=()
if [ "$mode" = "--cached" ]; then
  diff_args=(--cached "$before_ref")
else
  diff_args=("$before_ref" "$after_ref")
fi
raw_diff_file="$work_dir/raw-diff"
git diff --no-ext-diff --no-color -U0 --diff-filter=ACMR "${diff_args[@]}" -- "${offend_files[@]}" > "$raw_diff_file" 2>/dev/null || true

# Doc-comment exemption: strip /// lines from both trees before tokei runs.
# Block-style KDoc/JavaDoc doc comments slip through — accepted v1 limitation.
strip_doc_comments() {
  local dir="$1"
  while IFS= read -r -d '' f; do
    awk '{ t=$0; sub(/^[ \t]+/,"",t); if (t !~ /^\/\/\//) print }' "$f" > "$f.tmp" && mv "$f.tmp" "$f"
  done < <(find "$dir" -type f \( -name '*.fs' -o -name '*.fsx' -o -name '*.cs' \) -print0)
  while IFS= read -r -d '' f; do
    awk '{ t=$0; sub(/^[ \t]+/,"",t); if (t !~ /^\/\/\// && t !~ /^\/\/!/) print }' "$f" > "$f.tmp" && mv "$f.tmp" "$f"
  done < <(find "$dir" -type f -name '*.rs' -print0)
}
strip_doc_comments "$before_dir"
strip_doc_comments "$after_dir"

parse_tokei() {
  "$tokei_bin" -C --no-ignore --hidden "$1" 2>/dev/null | awk '
    /^=+$/ { next }
    /^ Language / { next }
    {
      if ($1 == "Total") next
      n = NF
      lang = $1
      for (i = 2; i <= n - 5; i++) lang = lang " " $i
      printf "%s\t%d\t%d\n", lang, $(n-2), $(n-1)
    }
  '
}

before_report=$(parse_tokei "$before_dir")
after_report=$(parse_tokei "$after_dir")

normalize_lang() {
  local l="${1^^}"
  l="${l//#/SHARP}"
  printf '%s' "$l" | sed -E 's/[^A-Z0-9]+/_/g; s/^_+//; s/_+$//'
}

declare -A before_code=() before_com=() after_code=() after_com=() langs=()
if [ -n "$before_report" ]; then
  while IFS=$'\t' read -r lang code com; do
    norm=$(normalize_lang "$lang")
    before_code[$norm]=$code
    before_com[$norm]=$com
    langs[$norm]="$lang"
  done <<< "$before_report"
fi
if [ -n "$after_report" ]; then
  while IFS=$'\t' read -r lang code com; do
    norm=$(normalize_lang "$lang")
    after_code[$norm]=$code
    after_com[$norm]=$com
    langs[$norm]="$lang"
  done <<< "$after_report"
fi

table=""
fail_langs=""
for norm in "${!langs[@]}"; do
  case " $IGNORE_LANGUAGES " in
    *" $norm "*) continue ;;
  esac
  bc=${before_code[$norm]:-0}; bm=${before_com[$norm]:-0}
  ac=${after_code[$norm]:-0}; am=${after_com[$norm]:-0}
  dcode=$((ac - bc))
  dcom=$((am - bm))

  ratio_var="RATIO_${norm}"; floor_var="FLOOR_${norm}"
  ratio_threshold="${!ratio_var:-$DEFAULT_RATIO}"
  floor="${!floor_var:-$DEFAULT_FLOOR}"

  verdict=$(awk -v dcom="$dcom" -v dcode="$dcode" -v floor="$floor" -v thr="$ratio_threshold" '
    BEGIN {
      denom = dcom + ((dcode > 0) ? dcode : 0)
      ratio = (denom > 0) ? dcom / denom : 0
      if (dcom >= floor && ratio > thr) { printf "FAIL\t%.4f", ratio } else { printf "PASS\t%.4f", ratio }
    }')
  verdict_word="${verdict%%$'\t'*}"
  ratio_val="${verdict#*$'\t'}"

  table="${table}${langs[$norm]}\t${bc}\t${bm}\t${ac}\t${am}\t${dcode}\t${dcom}\t${ratio_val}\t${ratio_threshold}\t${floor}\t${verdict_word}\n"
  if [ "$verdict_word" = "FAIL" ]; then
    fail_langs="${fail_langs}${norm} "
  fi
done

# Comment-block length cap: independent of the ratio rule, scanned off the raw
# diff — consecutive marker-matching added lines (incl. blank "//", "///") form a block.
block_scan_file="$work_dir/block-scan"
awk '
  function flush() {
    if (block_len > 0 && cur_key != "") printf "%s\t%s\t%d\t%s\n", cur_key, cur_file, block_len, block_first
    block_len = 0; block_first = ""
  }
  function set_file(f,    base, ext) {
    flush()
    cur_file = f
    base = f; sub(/^.*\//, "", base)
    ext = ""
    if (base ~ /\./) { ext = base; sub(/^.*\./, "", ext); ext = tolower(ext) }
    if (ext=="fs"||ext=="fsx") { cur_key="FSHARP"; cur_pat="SLASH" }
    else if (ext=="cs") { cur_key="CSHARP"; cur_pat="SLASH" }
    else if (ext=="kt"||ext=="kts") { cur_key="KOTLIN"; cur_pat="SLASH" }
    else if (ext=="tf") { cur_key="HCL"; cur_pat="SLASH" }
    else if (ext=="alloy") { cur_key="ALLOY"; cur_pat="SLASH" }
    else if (ext=="river") { cur_key="RIVER"; cur_pat="SLASH" }
    else if (ext=="go") { cur_key="GO"; cur_pat="SLASH" }
    else if (ext=="rs") { cur_key="RUST"; cur_pat="SLASH" }
    else if (ext=="js") { cur_key="JS"; cur_pat="SLASH" }
    else if (ext=="ts") { cur_key="TS"; cur_pat="SLASH" }
    else if (ext=="yml"||ext=="yaml") { cur_key="YAML"; cur_pat="HASH" }
    else if (ext=="sh"||ext=="bash") { cur_key="SHELL"; cur_pat="HASH" }
    else if (ext=="toml") { cur_key="TOML"; cur_pat="HASH" }
    else if (ext=="py") { cur_key="PYTHON"; cur_pat="HASH" }
    else if (ext=="conf") { cur_key="CONF"; cur_pat="HASH" }
    else if (ext=="sql") { cur_key="SQL"; cur_pat="DASH" }
    else { cur_key=""; cur_pat="" }
  }
  /^\+\+\+ b\// { set_file(substr($0, 7)); next }
  /^@@/ { flush(); next }
  /^-/ { next }
  /^\+/ {
    if (cur_pat == "") next
    t = substr($0, 2)
    sub(/^[ \t]+/, "", t)
    matched = 0
    if (cur_pat == "SLASH" && t ~ /^(\/\/|\(\*)/) matched = 1
    else if (cur_pat == "HASH" && t ~ /^#/) matched = 1
    else if (cur_pat == "DASH" && t ~ /^--/) matched = 1
    if (matched) {
      if (block_len == 0) block_first = t
      block_len++
    } else {
      flush()
    }
    next
  }
  END { flush() }
' "$raw_diff_file" > "$block_scan_file"

block_table=""
block_count=0
while IFS=$'\t' read -r key file len first; do
  if [ -z "$key" ]; then
    continue
  fi
  block_var="BLOCK_${key}"
  block_limit="${!block_var:-$MAX_COMMENT_BLOCK}"
  if [ "$block_limit" -eq 0 ]; then
    continue
  fi
  if [ "$len" -gt "$block_limit" ]; then
    preview="$first"
    if [ "${#preview}" -gt 70 ]; then
      preview="${preview:0:70}..."
    fi
    block_table="${block_table}${file}\t${len}\t${block_limit}\t${preview}\n"
    block_count=$((block_count + 1))
  fi
done < "$block_scan_file"

if [ -z "$fail_langs" ] && [ "$block_count" -eq 0 ]; then
  echo "comment-ratio: OK"
  echo "Per-language net-new comment ratios:"
  printf '%b' "$table" | awk -F'\t' '{printf "  %s:  %+d comment lines vs %+d code lines \xe2\x86\x92 ratio %.2f (limit %.2f, floor %d)\n", $1,$7,$6,$8,$9,$10}'
  echo "Comment blocks: OK (no added block exceeds its line cap)"
  exit 0
fi

# Identical wording from hook and CI (same script); only commit-vs-PR
# phrasing below adapts to $mode.
if [ "$mode" = "--cached" ]; then
  scope_noun="this commit"; retry_hint="Delete the offending comments, re-stage, and commit again; this check re-runs automatically."
else
  scope_noun="this PR"; retry_hint="Delete the offending comments and push an amended or new commit; this check re-runs automatically."
fi

echo "✖ COMMENT-RATIO GATE FAILED"
echo
echo "This repo blocks commits whose NET NEW comment lines are too high relative to"
echo "net new code, AND any single added comment block longer than its line cap"
echo "(default ${MAX_COMMENT_BLOCK} lines), regardless of ratio. Measured on this diff only."
echo

if [ -n "$fail_langs" ]; then
  echo "Per-language result (only failing languages shown):"
  printf '%b' "$table" | awk -F'\t' '$11=="FAIL"{printf "  %s:  %+d comment lines vs %+d code lines \xe2\x86\x92 ratio %.2f (limit %.2f, floor %d)\n", $1,$7,$6,$8,$9,$10}'

  echo
  echo "Offending added comment lines (delete or condense these):"
  offend_lines_file="$work_dir/offend-lines"
  awk '
    /^\+\+\+ b\// { file = substr($0, 7); next }
    /^\+/ {
      t = substr($0, 2)
      sub(/^[ \t]+/, "", t)
      if (t == "") next
      if (t ~ /^\/\/\//) next
      if (t ~ /^(\/\/|#|\(\*|\/\*)/) printf "%s: %s\n", file, t
    }
  ' "$raw_diff_file" > "$offend_lines_file"
  offend_total=$(wc -l < "$offend_lines_file")
  head -n 30 "$offend_lines_file" | sed 's/^/  /'
  if [ "$offend_total" -gt 30 ]; then
    echo "  ... and $((offend_total - 30)) more"
  fi
fi

if [ "$block_count" -gt 0 ]; then
  echo
  echo "Comment blocks longer than ${MAX_COMMENT_BLOCK} lines (split, condense, or delete):"
  printf '%b' "$block_table" | head -n 30 | awk -F'\t' '{printf "  %s: %s-line block (limit %s) starting: %s\n", $1,$2,$3,$4}'
  if [ "$block_count" -gt 30 ]; then
    echo "  ... and $((block_count - 30)) more"
  fi
fi

echo
echo "How to fix:"
echo "  1. Delete narrative/process comments — code should explain itself."
echo "     Keep ONLY comments stating a non-obvious constraint (1-2 lines max)."
echo "  2. \`///\` doc comments are exempt from the ratio rule above, but NOT from"
echo "     the block-length cap below — writing docs is fine, running commentary isn't."
echo "  3. A comment block may be at most ${MAX_COMMENT_BLOCK} lines. If you cannot fit the"
echo "     thought in ${MAX_COMMENT_BLOCK} lines, the code needs restructuring — or it belongs"
echo "     in a design doc, not a comment."
echo "  4. $retry_hint"
echo
echo "Do NOT bypass with --no-verify (it is blocked for agents and CI re-checks the"
echo "full PR anyway). If you believe $scope_noun legitimately needs these comments,"
echo "STOP and ask the human maintainer — they can adjust .comment-ratio.conf or"
echo "apply the 'comment-ratio-exempt' PR label."
exit 1
