#!/usr/bin/env bash
# The local stack's smoke check: API -> database -> game -> database -> API, with no game client.
# Signs in as a seeded account, asks for an MMD withdrawal to a seeded character that isn't online, and expects the game server
# to fail it "offline" within 10 seconds. On failure it names the missing piece: database, schema, API, seed or game.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

ACCOUNT="${MARKET_SMOKE_ACCOUNT:-seedalpha}"
PASSWORD="${MARKET_SMOKE_PASSWORD:-$MARKET_SEED_PASSWORD}"
CHARACTER="${MARKET_SMOKE_CHARACTER:-Seed Alpha}"
TIMEOUT_SECONDS=10

fail() {
  echo "SMOKE FAIL ($1): $2" >&2
  exit 1
}

ok() {
  echo "  ok  $1"
}

command -v jq >/dev/null || fail "tools" "jq is required"

# ---- database
db_running || fail "database" "the ace-db container isn't running (scripts/market/bootstrap.sh)"
db_sql -N -e "SELECT 1;" >/dev/null 2>&1 || fail "database" "MySQL in ace-db doesn't answer the application login from docker.env"
ok "database: ace-db answers on 127.0.0.1:$DB_HOST_PORT"

# ---- schema
tables="$(db_sql -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$MARKET_SHARD_DATABASE' AND table_name IN ('market_ticket', 'market_vault_item', 'market_balance');")"
[[ "$tables" == "3" ]] || fail "schema" "$MARKET_SHARD_DATABASE has no market tables (scripts/market/bootstrap.sh applies the market update script)"
ok "schema: $MARKET_SHARD_DATABASE has the market tables"

# ---- API (the container's health check, then the public facets endpoint)
health="$(api_health 30)"
[[ "$health" != missing ]] || fail "API" "the market-api container doesn't exist (scripts/market/api.sh)"
[[ "$health" == running/healthy ]] || fail "API" "the market-api container is $health (scripts/market/api.sh; docker compose -f docker/docker-compose.local.yml logs market-api)"

curl -fsS --max-time 5 -o /dev/null "$MARKET_API_URL/api/facets" || fail "API" "$MARKET_API_URL/api/facets doesn't answer"
ok "API: market-api is healthy and $MARKET_API_URL/api/facets answers"

# ---- sign in as a seeded account
headers="$(mktemp)"
trap 'rm -f "$headers"' EXIT

# the password goes through stdin, not the command line
login_status="$(jq -nc --arg a "$ACCOUNT" --arg p "$PASSWORD" '{account: $a, password: $p}' | curl -sS --max-time 10 -o /dev/null -D "$headers" -w '%{http_code}' \
  -H 'Content-Type: application/json' --data-binary @- "$MARKET_API_URL/api/auth/login")" || fail "API" "sign-in request failed"
[[ "$login_status" == 200 ]] || fail "seed" "signing in as $ACCOUNT answered $login_status (scripts/market/seed.sh creates it)"
cookie="$(sed -n 's/^[Ss]et-[Cc]ookie: \(market_session=[^;]*\).*/\1/p' "$headers" | tail -1)"
[[ -n "$cookie" ]] || fail "API" "sign-in set no market_session cookie"
ok "sign-in: $ACCOUNT"

me="$(curl -fsS --max-time 10 -H "Cookie: $cookie" "$MARKET_API_URL/api/me")" || fail "API" "GET /api/me failed"
character_id="$(jq -r --arg n "$CHARACTER" '.characters[] | select(.name == $n) | .id' <<<"$me" | head -1)"
[[ -n "$character_id" ]] || fail "seed" "$ACCOUNT has no character named '$CHARACTER'"

# ---- a ticket the game must answer
response="$(curl -sS --max-time 10 -D "$headers" -w '\n%{http_code}' -H "Cookie: $cookie" -H 'X-Market-Request: 1' -H 'Content-Type: application/json' \
  -d "$(jq -nc --argjson c "$character_id" --arg k "smoke-$(date +%s%N)" '{characterId: $c, amount: 1, idempotencyKey: $k}')" \
  "$MARKET_API_URL/api/mmd/withdraw")" || fail "API" "POST /api/mmd/withdraw failed"
[[ "$(tail -1 <<<"$response")" == 202 ]] || fail "API" "POST /api/mmd/withdraw answered $(tail -1 <<<"$response"): $(head -1 <<<"$response")"
location="$(sed -n 's/^[Ll]ocation: \([^[:space:]]*\).*/\1/p' "$headers" | tail -1)"
[[ "$location" == /api/tickets/* ]] || fail "API" "the ticket's Location '$location' isn't under /api/tickets"
ok "ticket: mmd_withdraw for $CHARACTER created at $location"

deadline=$((SECONDS + TIMEOUT_SECONDS))
while :; do
  ticket="$(curl -fsS --max-time 5 -H "Cookie: $cookie" "$MARKET_API_URL$location")" || fail "API" "GET $location failed"
  status="$(jq -r .status <<<"$ticket")"
  [[ "$status" == WAITING || "$status" == CLAIMED ]] || break
  (( SECONDS < deadline )) || break
  sleep 0.5
done

code="$(jq -r .resultCode <<<"$ticket")"
case "$status/$code" in
  FAILED/offline)
    ok "game: failed the ticket 'offline' ($(jq -r .resultMessage <<<"$ticket"))"
    echo "SMOKE PASS: API, database and game server see each other."
    ;;
  WAITING/*)
    fail "game" "the ticket is still WAITING after ${TIMEOUT_SECONDS}s: the game server isn't running or isn't polling $MARKET_SHARD_DATABASE (scripts/market/game.sh)" ;;
  CLAIMED/*)
    fail "game" "the game claimed the ticket but didn't finish it within ${TIMEOUT_SECONDS}s" ;;
  *)
    fail "game" "expected FAILED/offline, got $status/$code (is $CHARACTER logged in?)" ;;
esac
