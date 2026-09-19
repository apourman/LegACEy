#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Debug}"

"$ROOT/scripts/ace-server/run-host.sh" --configuration "$CONFIGURATION" --no-build
