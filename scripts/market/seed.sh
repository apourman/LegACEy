#!/usr/bin/env bash
# Seeds the market stack's databases (behind the development guard). Password: --password, MARKET_SEED_PASSWORD, or "marketdev".
set -euo pipefail
exec "$(dirname "${BASH_SOURCE[0]}")/dev.sh" seed "$@"
