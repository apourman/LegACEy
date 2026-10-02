#!/usr/bin/env bash
# Shared settings for the local market stack scripts. Source it; don't run it.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

[[ -f "$ROOT/docker.env" ]] || {
  echo "Missing $ROOT/docker.env; copy docker.env.example and set local values." >&2
  exit 1
}

# --env-file: the market-api service takes its database login (MYSQL_USER, MYSQL_PASSWORD) from docker.env
COMPOSE=(docker compose --env-file "$ROOT/docker.env" -f "$ROOT/docker/docker-compose.local.yml")

# the market stack's own databases on the local Docker MySQL, so seeding never touches ace_auth or ace_shard
MARKET_AUTH_DATABASE="${MARKET_AUTH_DATABASE:-ace_market_auth}"
MARKET_SHARD_DATABASE="${MARKET_SHARD_DATABASE:-ace_market_shard}"
MARKET_API_PORT="${MARKET_API_PORT:-5080}"
MARKET_API_URL="${MARKET_API_URL:-http://127.0.0.1:$MARKET_API_PORT}"
DB_HOST_PORT="${DB_HOST_PORT:-3310}"
MARKET_GAME_RUN_DIR="${MARKET_GAME_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-host}"
MARKET_SCHEMA_SCRIPT="$ROOT/Database/Updates/Shard/2026-09-28-00-Market-Schema.sql"
export MARKET_SEED_PASSWORD="${MARKET_SEED_PASSWORD:-marketdev}"

# the names go into SQL and sed unquoted
for name in "$MARKET_AUTH_DATABASE" "$MARKET_SHARD_DATABASE"; do
  [[ "$name" =~ ^[A-Za-z0-9_]+$ ]] || { echo "Database names may hold only letters, digits and _: '$name'" >&2; exit 1; }
done

# The Market API's service key, MARKET_SERVICE_KEY in docker.env (the same value compose gives the API and the website's dev proxy).
# Prints it for capture; never echo it or put it on a command line. Fails, saying what to do, when it's missing or too short.
service_key() {
  local key
  key="$(sed -n 's/^[[:space:]]*MARKET_SERVICE_KEY=//p' "$ROOT/docker.env" | tail -1 | tr -d '\r')"
  key="${key#\"}"; key="${key%\"}"
  if (( ${#key} < 32 )); then
    echo "MARKET_SERVICE_KEY in $ROOT/docker.env is missing or shorter than 32 characters." >&2
    echo "Add a line: MARKET_SERVICE_KEY=<the output of: openssl rand -hex 32>" >&2
    return 1
  fi
  printf '%s' "$key"
}

# exits when the service key isn't configured
require_service_key() {
  service_key >/dev/null || exit 1
}

# runs mysql in the ace-db container as the application user; the password stays inside the container
db_sql() {
  "${COMPOSE[@]}" exec -T ace-db sh -c 'MYSQL_PWD="$MYSQL_PASSWORD" exec mysql -u"$MYSQL_USER" --default-character-set=utf8mb4 "$@"' mysql "$@"
}

db_running() {
  [[ -n "$("${COMPOSE[@]}" ps --status running -q ace-db 2>/dev/null)" ]]
}

# the market-api container's id, running or not (empty when it doesn't exist)
api_container() {
  "${COMPOSE[@]}" ps -a -q market-api 2>/dev/null || true
}

# "<state>/<health>" of the market-api container, waiting while its health check is still starting (up to 2 * $1 seconds)
api_health() {
  local container health
  container="$(api_container)"
  [[ -n "$container" ]] || { echo missing; return; }

  for _ in $(seq "${1:-30}"); do
    health="$(docker inspect -f '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$container" 2>/dev/null || echo missing)"
    [[ "$health" == running/starting ]] || break
    sleep 2
  done

  echo "$health"
}
