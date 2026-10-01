#!/usr/bin/env bash
# Builds and (re)starts the Market API container alone, then waits for its health check. Run it again after an API code change.
# The API listens on 127.0.0.1:$MARKET_API_PORT only.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

db_running || { echo "ace-db is not running: run scripts/market/bootstrap.sh first." >&2; exit 1; }

# --no-deps: ace-db is already up; never recreate it from here
"${COMPOSE[@]}" up -d --no-deps --build market-api

echo "Waiting for market-api to report healthy..."
health="$(api_health 60)"

if [[ "$health" == running/healthy ]]; then
  echo "market-api is healthy at $MARKET_API_URL/api"
  exit 0
fi

"${COMPOSE[@]}" logs --tail 30 market-api >&2
echo "market-api is $health (logs above)." >&2
exit 1
