#!/usr/bin/env bash
# Shared settings for the local market stack scripts. Source it; don't run it.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

DOCKER_ENV_FILE="${MARKET_DOCKER_ENV_FILE:-$ROOT/docker.env}"
[[ -f "$DOCKER_ENV_FILE" ]] || {
  echo "Missing $DOCKER_ENV_FILE; copy docker.env.example and set local values." >&2
  exit 1
}

# --env-file: the market-api service takes its database login (MYSQL_USER, MYSQL_PASSWORD) from docker.env
COMPOSE=(docker compose --env-file "$DOCKER_ENV_FILE" -f "$ROOT/docker/docker-compose.local.yml")

# the market stack's own databases on the local Docker MySQL, so seeding never touches ace_auth or ace_shard
MARKET_AUTH_DATABASE="${MARKET_AUTH_DATABASE:-ace_market_auth}"
MARKET_SHARD_DATABASE="${MARKET_SHARD_DATABASE:-ace_market_shard}"
# the BFF (market-web), the stack's one public piece; the Market API has no host port
MARKET_WEB_PORT="${MARKET_WEB_PORT:-5173}"
MARKET_WEB_URL="${MARKET_WEB_URL:-http://127.0.0.1:$MARKET_WEB_PORT}"
DB_HOST_PORT="${DB_HOST_PORT:-3310}"
MARKET_GAME_RUN_DIR="${MARKET_GAME_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-host}"
MARKET_SCHEMA_SCRIPT="$ROOT/Database/Updates/Shard/2026-09-28-00-Market-Schema.sql"
# the end-to-end stack's own databases and state (e2e.sh, fresh.sh). Fixed, not settings: ACE.MarketDev's guard (DevelopmentGuardSettings)
# and docker/market-api/appsettings.E2E.json name the same pair.
MARKET_E2E_AUTH_DATABASE=ace_market_e2e_auth
MARKET_E2E_SHARD_DATABASE=ace_market_e2e_shard
MARKET_E2E_RUN_DIR="${MARKET_E2E_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-e2e}"
export MARKET_SEED_PASSWORD="${MARKET_SEED_PASSWORD:-marketdev}"

# the names go into SQL and sed unquoted
for name in "$MARKET_AUTH_DATABASE" "$MARKET_SHARD_DATABASE"; do
  [[ "$name" =~ ^[A-Za-z0-9_]+$ ]] || { echo "Database names may hold only letters, digits and _: '$name'" >&2; exit 1; }
done

# A secret from docker.env: MARKET_SERVICE_KEY (the Market API's service key, given to the API and the BFF) or MARKET_COOKIE_SECRET (signs the
# BFF's session cookie). Prints it for capture; never echo it or put it on a command line. Fails, saying what to do, when it's missing or too short.
docker_env_secret() {
  local name="$1" value
  value="$(sed -n "s/^[[:space:]]*$name=//p" "$DOCKER_ENV_FILE" | tail -1 | tr -d '\r')"
  value="${value#\"}"; value="${value%\"}"
  # 32: the API's minimum key length (ServiceGate.MinimumKeyLength in Source/ACE.MarketApi/ServiceGate.cs) and the BFF's
  # (minimumSecretLength in market-web/src/bff/settings.server.ts); keep them equal
  if (( ${#value} < 32 )); then
    echo "$name in $DOCKER_ENV_FILE is missing or shorter than 32 characters." >&2
    echo "Add a line: $name=<the output of: openssl rand -hex 32>" >&2
    return 1
  fi
  printf '%s' "$value"
}

service_key() {
  docker_env_secret MARKET_SERVICE_KEY
}

# exits when the service key isn't configured
require_service_key() {
  service_key >/dev/null || exit 1
}

# exits when the BFF's cookie secret isn't configured
require_cookie_secret() {
  docker_env_secret MARKET_COOKIE_SECRET >/dev/null || exit 1
}

# runs mysql in the ace-db container as the application user; the password stays inside the container
db_sql() {
  "${COMPOSE[@]}" exec -T ace-db sh -c 'MYSQL_PWD="$MYSQL_PASSWORD" exec mysql -u"$MYSQL_USER" --default-character-set=utf8mb4 "$@"' mysql "$@"
}

db_running() {
  [[ -n "$("${COMPOSE[@]}" ps --status running -q ace-db 2>/dev/null)" ]]
}

# exits unless docker-ace-db-1 is this compose project's running, healthy ace-db, published at 127.0.0.1:3310: the only MySQL the
# end-to-end scripts write, and the endpoint ACE.MarketDev's fresh guard accepts
require_local_ace_db() {
  local container_id compose_id health published
  [[ "$DB_HOST_PORT" == 3310 ]] || { echo "End-to-end data uses only 127.0.0.1:3310; DB_HOST_PORT must be 3310." >&2; exit 1; }
  db_running || { echo "docker-ace-db-1 is not running; start the local MySQL service first." >&2; exit 1; }
  container_id="$(docker inspect -f '{{.Id}}' docker-ace-db-1 2>/dev/null || true)"
  compose_id="$("${COMPOSE[@]}" ps --status running -q ace-db 2>/dev/null || true)"
  [[ -n "$container_id" && "$container_id" == "$compose_id" ]] || {
    echo "Refusing to use MySQL: the local compose service is not docker-ace-db-1." >&2
    exit 1
  }
  health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' docker-ace-db-1)"
  [[ "$health" == healthy ]] || { echo "docker-ace-db-1 is not healthy ($health)." >&2; exit 1; }
  published="$(docker port docker-ace-db-1 3306/tcp 2>/dev/null || true)"
  [[ "$published" == 127.0.0.1:3310 ]] || { echo "docker-ace-db-1 is not published at 127.0.0.1:3310 only (published: ${published:-nothing})." >&2; exit 1; }
}

# a compose service's container id, running or not (empty when it doesn't exist)
service_container() {
  "${COMPOSE[@]}" ps -a -q "$1" 2>/dev/null || true
}

api_container() {
  service_container market-api
}

# "<state>/<health>" of a service's container (market-api, market-web), from Docker's own health check, waiting while it is still starting
# (up to 2 * $2 seconds). The API has no host port, so this is how the scripts know it is up.
service_health() {
  local container health
  container="$(service_container "$1")"
  [[ -n "$container" ]] || { echo missing; return; }

  for _ in $(seq "${2:-30}"); do
    health="$(docker inspect -f '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$container" 2>/dev/null || echo missing)"
    [[ "$health" == running/starting ]] || break
    sleep 2
  done

  echo "$health"
}

api_health() {
  service_health market-api "${1:-30}"
}

web_health() {
  service_health market-web "${1:-30}"
}
