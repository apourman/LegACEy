#!/usr/bin/env bash
# The OpenAPI drift check, run by the pull-request workflow and the local gate: rebuilds the Market API's OpenAPI document and regenerates
# the website's client from it, then fails when either differs from the file in the checkout. Needs the .NET SDK and market-web's
# node_modules (npm ci); no DATs, no MySQL, no Config.js. In CI the checkout is the commit, so "differs from the checkout" is
# "differs from what's committed"; locally, uncommitted edits to the two files count as what's there.
# On drift the regenerated files are left in place: review them and commit them.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOCUMENT=Source/ACE.MarketApi/openapi.json
CLIENT=market-web/src/api-schema.ts

before="$(mktemp -d)"
trap 'rm -rf "$before"' EXIT
cp "$ROOT/$DOCUMENT" "$before/openapi.json"
cp "$ROOT/$CLIENT" "$before/api-schema.ts"

dotnet build "$ROOT/Source/ACE.MarketApi" -p:Platform=x64 -p:UpdateOpenApi=true
(cd "$ROOT/market-web" && npm run generate:api)

drift=0
for pair in "openapi.json:$DOCUMENT" "api-schema.ts:$CLIENT"; do
    if ! git --no-pager diff --no-index --exit-code -- "$before/${pair%%:*}" "$ROOT/${pair#*:}"; then
        echo "OpenAPI drift: $ROOT/${pair#*:} differs from the checked-in file (diff above)." >&2
        drift=1
    fi
done

if (( drift )); then
    echo "To refresh: dotnet build Source/ACE.MarketApi -p:Platform=x64 -p:UpdateOpenApi=true && (cd market-web && npm run generate:api), then commit both files." >&2
    exit 1
fi
echo "OpenAPI drift check: $DOCUMENT and $CLIENT match what the build generates."
