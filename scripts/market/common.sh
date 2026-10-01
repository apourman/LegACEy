#!/usr/bin/env bash
# Shared settings for the local market stack scripts. Source it; don't run it.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE=(docker compose -f "$ROOT/docker/docker-compose.local.yml")

# the market stack's own databases on the local Docker MySQL, so seeding never touches ace_auth or ace_shard
MARKET_AUTH_DATABASE="${MARKET_AUTH_DATABASE:-ace_market_auth}"
MARKET_SHARD_DATABASE="${MARKET_SHARD_DATABASE:-ace_market_shard}"
MARKET_API_PORT="${MARKET_API_PORT:-5080}"
MARKET_API_URL="${MARKET_API_URL:-http://127.0.0.1:$MARKET_API_PORT}"
DB_HOST_PORT="${DB_HOST_PORT:-3310}"
MARKET_GAME_RUN_DIR="${MARKET_GAME_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-host}"
MARKET_SCHEMA_SCRIPT="$ROOT/Database/Updates/Shard/2026-09-28-00-Market-Schema.sql"

[[ -f "$ROOT/docker.env" ]] || {
  echo "Missing $ROOT/docker.env; copy docker.env.example and set local values." >&2
  exit 1
}

# the database login, read without exporting the rest of docker.env (its ACE_* values describe the game container)
MYSQL_USER="$(sed -n 's/^MYSQL_USER=//p' "$ROOT/docker.env" | tail -1)"
[[ -n "$MYSQL_USER" ]] || { echo "MYSQL_USER is required in docker.env" >&2; exit 1; }

# runs mysql in the ace-db container as the application user; the password stays inside the container
db_sql() {
  "${COMPOSE[@]}" exec -T ace-db sh -c 'MYSQL_PWD="$MYSQL_PASSWORD" exec mysql -u"$MYSQL_USER" --default-character-set=utf8mb4 "$@"' mysql "$@"
}

db_running() {
  [[ -n "$("${COMPOSE[@]}" ps --status running -q ace-db 2>/dev/null)" ]]
}
