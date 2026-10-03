#!/usr/bin/env bash
# Rebuild and seed only the production end-to-end databases on the existing local ace-db.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

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

set -a
# shellcheck disable=SC1090
source "$DOCKER_ENV_FILE"
set +a
MYSQL_USER="${MYSQL_USER:?MYSQL_USER is required in the selected docker env file}"
MYSQL_PASSWORD="${MYSQL_PASSWORD:?MYSQL_PASSWORD is required in the selected docker env file}"
[[ "$DB_HOST_PORT" == 3310 ]] || { echo "End-to-end data uses only 127.0.0.1:3310; DB_HOST_PORT in the docker env file must be 3310." >&2; exit 1; }

DAT_DIR="${ACE_HOST_DAT_DIRECTORY:-$HOME/ace_dats/retail}"
[[ -d "$DAT_DIR" ]] || { echo "Missing DAT directory: $DAT_DIR" >&2; exit 1; }
CONFIG_DIR="${MARKET_E2E_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-e2e}"
mkdir -p "$CONFIG_DIR"
CONFIG="$CONFIG_DIR/Config.js"
cp "$ROOT/Source/ACE.Server/Config.js.docker" "$CONFIG"
sed -i \
  -e 's|"Host": "ace-db"|"Host": "127.0.0.1"|g' \
  -e 's|"Port": 3306|"Port": 3310|g' \
  -e 's|"Database": "ace_auth"|"Database": "ace_market_e2e_auth"|' \
  -e 's|"Database": "ace_shard"|"Database": "ace_market_e2e_shard"|' \
  -e "s|\"DatFilesDirectory\": \"/ace/Dats\"|\"DatFilesDirectory\": \"$DAT_DIR\"|" \
  "$CONFIG"

python3 - "$CONFIG" <<'PY'
import json
import os
import sys
from pathlib import Path

path = Path(sys.argv[1])
config = path.read_text()
config = config.replace('"Username": ""', f'"Username": {json.dumps(os.environ["MYSQL_USER"])}')
config = config.replace('"Password": ""', f'"Password": {json.dumps(os.environ["MYSQL_PASSWORD"])}')
path.write_text(config)
PY
chmod 600 "$CONFIG"

unset MYSQL_USER MYSQL_PASSWORD MYSQL_ROOT_PASSWORD MARKET_SERVICE_KEY MARKET_COOKIE_SECRET ACE_HOST_DAT_DIRECTORY

export MARKET_DEV_ROOT="$ROOT"
export MARKET_DEV_CONFIG="$CONFIG"
exec "$ROOT/scripts/market/dev.sh" fresh
