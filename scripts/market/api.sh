#!/usr/bin/env bash
# Builds and (re)starts the Market API container alone, then waits for its health check. Run it again after an API code change.
# The API listens on 127.0.0.1:$MARKET_API_PORT only.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

db_running || { echo "ace-db is not running: run scripts/market/bootstrap.sh first." >&2; exit 1; }

# --no-deps: ace-db is already up; never recreate it from here
"${COMPOSE[@]}" up -d --no-deps --build market-api

printf 'Waiting for market-api to report healthy...'
for _ in {1..60}; do
  status="$(docker inspect -f '{{.State.Health.Status}}' "$("${COMPOSE[@]}" ps -q market-api)" 2>/dev/null || true)"
  if [[ "$status" == "healthy" ]]; then
    printf ' healthy at %s/api\n' "$MARKET_API_URL"
    exit 0
  fi
  sleep 2
  printf '.'
done

printf '\n' >&2
"${COMPOSE[@]}" logs --tail 30 market-api >&2
echo "market-api did not become healthy (logs above)." >&2
exit 1
