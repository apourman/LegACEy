#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE="$ROOT/docker/docker-compose.local.yml"
CONFIG_TEMPLATE="$ROOT/Source/ACE.Server/Config.js.docker"
CONFIG_DIR="${ACE_HOST_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/ace-host}"
LOG_DIR="${ACE_HOST_LOG_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/ace-logs}"
MODS_DIR="${ACE_MODS_DIRECTORY:-${XDG_DATA_HOME:-$HOME/.local/share}/legacey/ace-mods}"
CONFIGURATION=Debug
START_DB=true
BUILD=true

usage() { echo "Usage: run-host.sh [--configuration NAME] [--no-build] [--no-db] [--help]"; }
while (($#)); do
  case "$1" in
    --configuration)
      [[ $# -ge 2 ]] || { usage >&2; exit 2; }
      CONFIGURATION="$2"
      shift
      ;;
    --no-build) BUILD=false ;;
    --no-db) START_DB=false ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
  shift
done

if [[ "$START_DB" == true ]]; then
  "$ROOT/scripts/db-bootstrap/bootstrap.sh"
fi

DAT_DIR="${ACE_DAT_FILES_DIRECTORY:-$HOME/ace_dats/retail}"
[[ -d "$DAT_DIR" ]] || { echo "Missing DAT directory: $DAT_DIR" >&2; exit 1; }
mkdir -p "$CONFIG_DIR" "$LOG_DIR" "$MODS_DIR"
cp "$CONFIG_TEMPLATE" "$CONFIG_DIR/Config.js"
sed -i \
  -e "s|\"Host\": \"ace-db\"|\"Host\": \"127.0.0.1\"|g" \
  -e "s|\"Port\": 3306|\"Port\": ${DB_HOST_PORT:-3310}|g" \
  -e "s|\"DatFilesDirectory\": \"/ace/Dats\"|\"DatFilesDirectory\": \"$DAT_DIR\"|" \
  -e "s|\"ModsDirectory\": \"/ace/Mods\"|\"ModsDirectory\": \"$MODS_DIR\"|" \
  -e "s|\"Username\": \"acedockeruser\"|\"Username\": \"${MYSQL_USER:-acedockeruser}\"|g" \
  -e "s|\"Password\": \"2020acEmulator2017\"|\"Password\": \"${MYSQL_PASSWORD:-2020acEmulator2017}\"|g" \
  "$CONFIG_DIR/Config.js"

if [[ "$BUILD" == true ]]; then
  dotnet build "$ROOT/Source/ACE.Server/ACE.Server.csproj" -c "$CONFIGURATION" -p:Platform=x64
fi

DLL="$ROOT/Source/ACE.Server/bin/x64/$CONFIGURATION/net10.0/ACE.Server.dll"
[[ -f "$DLL" ]] || { echo "Missing $DLL; run without --no-build first." >&2; exit 1; }

cd "$CONFIG_DIR"
export DOTNET_RUNNING_IN_CONTAINER=false
export ACE_NONINTERACTIVE_CONSOLE="${ACE_NONINTERACTIVE_CONSOLE:-false}"
exec dotnet "$DLL"
