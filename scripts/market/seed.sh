#!/usr/bin/env bash
# Seeds the market stack's databases (behind the development guard). Password: MARKET_SEED_PASSWORD, default "marketdev".
set -euo pipefail
exec "$(dirname "${BASH_SOURCE[0]}")/dev.sh" seed "$@"
