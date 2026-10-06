#!/usr/bin/env bash
# Starts the BFF (market-web: the website's own server, with live reload) after the API is healthy, then waits for its health check.
# Run from the main checkout; ace-db and market-api are never recreated from here.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
# the BFF sends the service key to the API and signs its session cookie with the cookie secret; it refuses to start without either
require_service_key
require_cookie_secret
[[ "$(api_health 60)" == running/healthy ]] || { echo "market-api is not healthy: run scripts/market/api.sh first." >&2; exit 1; }
"${COMPOSE[@]}" up -d --no-deps --build market-web

echo "Waiting for market-web to report healthy (it installs its dependencies first)..."
health="$(web_health 120)"
if [[ "$health" != running/healthy ]]; then
  "${COMPOSE[@]}" logs --tail 30 market-web >&2
  echo "market-web is $health (logs above)." >&2
  exit 1
fi
echo "Market website: http://localhost:$MARKET_WEB_PORT"
