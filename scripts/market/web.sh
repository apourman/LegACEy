#!/usr/bin/env bash
# Start the website after the API is healthy. Run from the main checkout; ace-db is never recreated.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
[[ "$(api_health 60)" == running/healthy ]] || { echo "market-api is not healthy: run scripts/market/api.sh first." >&2; exit 1; }
"${COMPOSE[@]}" up -d --no-deps --build market-web
echo "Market website: http://localhost:${MARKET_WEB_PORT:-5173}"
