#!/usr/bin/env bash
# Rebuild and seed only the production end-to-end databases on the existing local ace-db.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

require_local_ace_db

set -a
# shellcheck disable=SC1090
source "$DOCKER_ENV_FILE"
set +a
MYSQL_USER="${MYSQL_USER:?MYSQL_USER is required in the selected docker env file}"
MYSQL_PASSWORD="${MYSQL_PASSWORD:?MYSQL_PASSWORD is required in the selected docker env file}"
# the env file can set DB_HOST_PORT too; it must still be the endpoint checked above
[[ "$DB_HOST_PORT" == 3310 ]] || { echo "End-to-end data uses only 127.0.0.1:3310; DB_HOST_PORT in the docker env file must be 3310." >&2; exit 1; }

DAT_DIR="${ACE_HOST_DAT_DIRECTORY:-$HOME/ace_dats/retail}"
[[ -d "$DAT_DIR" ]] || { echo "Missing DAT directory: $DAT_DIR" >&2; exit 1; }
mkdir -p "$MARKET_E2E_RUN_DIR"
CONFIG="$MARKET_E2E_RUN_DIR/Config.js"

# the configuration holds the database password: private from the moment it exists
(
  umask 077
  rm -f "$CONFIG"
  cp "$ROOT/Source/ACE.Server/Config.js.docker" "$CONFIG"
  sed -i \
    -e 's|"Host": "ace-db"|"Host": "127.0.0.1"|g' \
    -e 's|"Port": 3306|"Port": 3310|g' \
    -e "s|\"Database\": \"ace_auth\"|\"Database\": \"$MARKET_E2E_AUTH_DATABASE\"|" \
    -e "s|\"Database\": \"ace_shard\"|\"Database\": \"$MARKET_E2E_SHARD_DATABASE\"|" \
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
)

unset MYSQL_USER MYSQL_PASSWORD MYSQL_ROOT_PASSWORD MARKET_SERVICE_KEY MARKET_COOKIE_SECRET ACE_HOST_DAT_DIRECTORY

export MARKET_DEV_ROOT="$ROOT"
export MARKET_DEV_CONFIG="$CONFIG"
exec "$ROOT/scripts/market/dev.sh" fresh
