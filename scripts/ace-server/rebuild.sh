#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE="$ROOT/docker/docker-compose.local.yml"

"$ROOT/scripts/db-bootstrap/bootstrap.sh"
dotnet build "$ROOT/Source/ACE.Server/ACE.Server.csproj" -c "${CONFIGURATION:-Debug}" -p:Platform=x64

printf 'Server build completed. Start it with scripts/ace-server/run-host.sh --no-build.\n'
