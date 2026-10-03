#!/usr/bin/env bash
# Rebuild only the fixed end-to-end databases. ACE.MarketDev calls this after its
# configuration-only guard has accepted both names and the local MySQL endpoint.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

require_local_ace_db

AUTH_DATABASE="$MARKET_E2E_AUTH_DATABASE"
SHARD_DATABASE="$MARKET_E2E_SHARD_DATABASE"

create_from_base() {
  local database="$1" base="$2" name="$3"
  sed -e "s/\`$name\`/\`$database\`/g" "$ROOT/Database/Base/$base" | db_sql
}

echo "Recreating the end-to-end databases..."
db_sql -e "DROP DATABASE IF EXISTS \`$AUTH_DATABASE\`; DROP DATABASE IF EXISTS \`$SHARD_DATABASE\`;"
create_from_base "$AUTH_DATABASE" AuthenticationBase.sql ace_auth
create_from_base "$SHARD_DATABASE" ShardBase.sql ace_shard

for script in "$ROOT"/Database/Updates/Shard/*Market*.sql; do
  [[ -f "$script" ]] || continue
  echo "Applying $(basename "$script")..."
  db_sql "$SHARD_DATABASE" < "$script"
done

for database in "$AUTH_DATABASE" "$SHARD_DATABASE"; do
  db_sql "$database" < "$ROOT/scripts/market/dev-marker.sql"
done

echo "End-to-end database schemas and development markers are ready."
