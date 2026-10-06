#!/usr/bin/env bash
# Runs the game server natively against the market stack's databases (a wrapper around scripts/ace-server/run-host.sh).
# Its Config.js goes to $MARKET_GAME_RUN_DIR, which the seed tool reads too. Extra arguments go to run-host.sh (e.g. --no-build).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

db_running || { echo "ace-db is not running: run scripts/market/bootstrap.sh first." >&2; exit 1; }

export ACE_HOST_RUN_DIR="$MARKET_GAME_RUN_DIR"
export ACE_HOST_LOG_DIR="${ACE_HOST_LOG_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-logs}"
export ACE_HOST_AUTH_DATABASE="$MARKET_AUTH_DATABASE"
export ACE_HOST_SHARD_DATABASE="$MARKET_SHARD_DATABASE"

exec "$ROOT/scripts/ace-server/run-host.sh" --no-db "$@"
