#!/usr/bin/env bash
# DEPRECATED — superseded by scripts/gather/runtime.sh <bot> (parameterized
# per-bot via .github/bots.yml). See .github/AGENT-FLOWS-REDESIGN.md Phase 1.
#
# Kept as a thin forwarding shim only so pre-existing references (e.g.
# src/CouponHubBot/docs/OBSERVABILITY.md) still resolve to a working script.
# No workflow calls this file anymore — product.yml/project.yml call
# scripts/gather/runtime.sh directly. Defaults to the coupon bot, matching
# this script's old CONTAINER_NAME/ARGOCD_APP_NAME defaults.
set -euo pipefail
echo "[gather-metrics.sh] DEPRECATED shim — forwarding to scripts/gather/runtime.sh coupon. Call scripts/gather/runtime.sh <bot> directly instead." >&2
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/gather/runtime.sh" "${1:-coupon}"
