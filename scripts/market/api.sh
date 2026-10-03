#!/usr/bin/env bash
# Builds and (re)starts the Market API container alone, then waits for its health check. Run it again after an API code change.
# The API is private: it has no host port. Only the BFF (market-web) reaches it, on the compose network they share, and it answers only requests
# carrying MARKET_SERVICE_KEY from docker.env (except /health). Readiness comes from the container's own health check (Docker's status).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

# the API refuses to start without it; say so before building
require_service_key
db_running || { echo "ace-db is not running: run scripts/market/bootstrap.sh first." >&2; exit 1; }

# --no-deps: ace-db is already up; never recreate it from here
"${COMPOSE[@]}" up -d --no-deps --build market-api

echo "Waiting for market-api to report healthy..."
health="$(api_health 60)"

if [[ "$health" == running/healthy ]]; then
  echo "market-api is healthy (Docker health check; no host port: the BFF reaches it as http://market-api:8080)"
  exit 0
fi

"${COMPOSE[@]}" logs --tail 30 market-api >&2
echo "market-api is $health (logs above)." >&2
exit 1
