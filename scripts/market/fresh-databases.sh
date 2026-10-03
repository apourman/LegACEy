#!/usr/bin/env bash
# Rebuild only the fixed end-to-end databases. ACE.MarketDev calls this after its
# configuration-only guard has accepted both names and the local MySQL endpoint.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

[[ "$DB_HOST_PORT" == 3310 ]] || { echo "End-to-end data uses only 127.0.0.1:3310; DB_HOST_PORT must be 3310." >&2; exit 1; }
db_running || { echo "docker-ace-db-1 is not running." >&2; exit 1; }
container_id="$(docker inspect -f '{{.Id}}' docker-ace-db-1 2>/dev/null || true)"
compose_id="$("${COMPOSE[@]}" ps --status running -q ace-db 2>/dev/null || true)"
[[ -n "$container_id" && "$container_id" == "$compose_id" ]] || {
  echo "Refusing to use MySQL: the local compose service is not docker-ace-db-1." >&2
  exit 1
}
health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' docker-ace-db-1)"
[[ "$health" == healthy ]] || { echo "docker-ace-db-1 is not healthy ($health)." >&2; exit 1; }

AUTH_DATABASE=ace_market_e2e_auth
SHARD_DATABASE=ace_market_e2e_shard

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
