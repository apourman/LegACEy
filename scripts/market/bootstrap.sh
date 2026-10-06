#!/usr/bin/env bash
# Starts the local Docker MySQL (port 3310) if it isn't running, and creates the market stack's own auth and shard databases:
# base schemas, the market update scripts, and the development marker. An existing database is left as it is, apart from the
# (idempotent) market scripts.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

if db_running; then
  echo "ace-db is already running."
else
  "$ROOT/scripts/db-bootstrap/bootstrap.sh"
fi

database_exists() {
  [[ "$(db_sql -N -e "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = '$1';")" == "1" ]]
}

# the development marker (scripts/market/dev-marker.sql, the same file DevelopmentGuard.Mark runs), for databases this script creates
mark() {
  db_sql "$1" < "$ROOT/scripts/market/dev-marker.sql"
}

# the base scripts create and USE their own database (ace_auth, ace_shard); rename it
create_from_base() {
  local database="$1" base="$2" name="$3"
  sed -e "s/\`$name\`/\`$database\`/g" "$ROOT/Database/Base/$base" | db_sql
}

if database_exists "$MARKET_AUTH_DATABASE"; then
  echo "$MARKET_AUTH_DATABASE exists; left as it is."
else
  echo "Creating $MARKET_AUTH_DATABASE..."
  create_from_base "$MARKET_AUTH_DATABASE" AuthenticationBase.sql ace_auth
  mark "$MARKET_AUTH_DATABASE"
fi

if database_exists "$MARKET_SHARD_DATABASE"; then
  echo "$MARKET_SHARD_DATABASE exists; left as it is."
else
  echo "Creating $MARKET_SHARD_DATABASE..."
  create_from_base "$MARKET_SHARD_DATABASE" ShardBase.sql ace_shard
  mark "$MARKET_SHARD_DATABASE"
fi

# idempotent; the game server's update runner applies the other shard updates when it starts
echo "Applying the market schema to $MARKET_SHARD_DATABASE..."
db_sql "$MARKET_SHARD_DATABASE" < "$MARKET_SCHEMA_SCRIPT"
progress_columns="$(db_sql -N -e "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '$MARKET_SHARD_DATABASE' AND TABLE_NAME = 'market_ticket' AND COLUMN_NAME IN ('progress', 'progress_Time', 'progress_Until', 'result');")"
if [[ "$progress_columns" == "0" ]]; then
  db_sql "$MARKET_SHARD_DATABASE" < "$ROOT/Database/Updates/Shard/2026-10-01-00-Market-Ticket-Progress.sql"
elif [[ "$progress_columns" != "4" ]]; then
  echo "The market ticket progress schema is only partly installed ($progress_columns of 4 columns); repair it before bootstrapping." >&2
  exit 1
fi
# the API's web sessions (CREATE TABLE IF NOT EXISTS, so it's safe to apply on every run)
db_sql "$MARKET_SHARD_DATABASE" < "$ROOT/Database/Updates/Shard/2026-10-02-00-Market-Web-Sessions.sql"
db_sql "$MARKET_SHARD_DATABASE" < "$ROOT/Database/Updates/Shard/2026-10-05-00-Market-Vault-Position.sql"
# market_enabled is off on a server; open the market here (INSERT IGNORE, so a value changed since is kept)
db_sql "$MARKET_SHARD_DATABASE" < "$ROOT/scripts/market/dev-settings.sql"

for database in "$MARKET_AUTH_DATABASE" "$MARKET_SHARD_DATABASE"; do
  if [[ "$(db_sql -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$database' AND table_name = 'legacey_dev_marker';")" != "1" ]]; then
    echo "Note: $database has no development marker, so the seed tool will refuse it. If it really is a development database: scripts/market/dev.sh mark"
  fi
done

echo "Market databases ready: $MARKET_AUTH_DATABASE, $MARKET_SHARD_DATABASE (127.0.0.1:$DB_HOST_PORT)."
