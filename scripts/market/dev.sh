#!/usr/bin/env bash
# The development tool (Source/ACE.MarketDev) on the market stack's databases: check, mark (interactive) or seed.
# The seed password comes from MARKET_SEED_PASSWORD (default marketdev, set in common.sh), not the command line.
# It reads the Config.js that scripts/market/game.sh writes, and refuses any database the development guard doesn't allow.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

CONFIG="$MARKET_GAME_RUN_DIR/Config.js"
[[ -f "$CONFIG" ]] || { echo "Missing $CONFIG: run scripts/market/game.sh once first (it writes the game's configuration)." >&2; exit 1; }

build_log="$(mktemp)"
trap 'rm -f "$build_log"' EXIT
dotnet build "$ROOT/Source/ACE.MarketDev/ACE.MarketDev.csproj" -c Debug -p:Platform=x64 -nologo >"$build_log" 2>&1 || {
  cat "$build_log" >&2
  echo "Building the development tool failed (output above)." >&2
  exit 1
}
cd "$MARKET_GAME_RUN_DIR"
dotnet "$ROOT/Source/ACE.MarketDev/bin/x64/Debug/net10.0/ACE.MarketDev.dll" "$@" --config "$CONFIG"
