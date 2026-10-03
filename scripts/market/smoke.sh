#!/usr/bin/env bash
# The local stack's smoke check, through the BFF as a browser would: BFF -> API -> database -> game -> database -> API -> BFF, with no game client.
# Checks the BFF's health, signs in as a seeded account through the BFF, asks for an MMD withdrawal to a seeded character that isn't online
# through the BFF's /api/* proxy, and expects the game server to fail it "offline" within 10 seconds. On failure it names the missing piece:
# config, database, schema, API, BFF, seed or game. The service key and the client IP are proven on the way: the BFF adds both to every API
# request, and the API refuses any request without the key (401) or with a malformed client IP (400).
# The password and the session cookie stay in private temporary files, never on the command line.
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

# ---- config: both secrets in docker.env (they are passed to the containers; nothing here sends them)
docker_env_secret MARKET_SERVICE_KEY >/dev/null || fail "config" "the Market API's service key isn't configured (see above)"
docker_env_secret MARKET_COOKIE_SECRET >/dev/null || fail "config" "the BFF's cookie secret isn't configured (see above)"

headers="$(mktemp)"
session_header="$(mktemp)"
trap 'rm -f "$headers" "$session_header"' EXIT
chmod 600 "$headers" "$session_header"

# curl to the BFF as the site itself: changes carry the site's Origin (the BFF's cross-site check)
bff_curl() {
  curl -H "Origin: $MARKET_WEB_URL" "$@"
}

# ---- database
db_running || fail "database" "the ace-db container isn't running (scripts/market/bootstrap.sh)"
db_sql -N -e "SELECT 1;" >/dev/null 2>&1 || fail "database" "MySQL in ace-db doesn't answer the application login from docker.env"
ok "database: ace-db answers on 127.0.0.1:$DB_HOST_PORT"

# ---- schema
tables="$(db_sql -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$MARKET_SHARD_DATABASE' AND table_name IN ('market_ticket', 'market_vault_item', 'market_balance', 'market_web_session');")"
[[ "$tables" == "4" ]] || fail "schema" "$MARKET_SHARD_DATABASE lacks market tables (scripts/market/bootstrap.sh applies the market update scripts)"
ok "schema: $MARKET_SHARD_DATABASE has the market tables"

# ---- API: healthy by its container's health check, and private (no host port)
health="$(api_health 30)"
[[ "$health" != missing ]] || fail "API" "the market-api container doesn't exist (scripts/market/api.sh)"
[[ "$health" == running/healthy ]] || fail "API" "the market-api container is $health (scripts/market/api.sh; docker compose -f docker/docker-compose.local.yml logs market-api)"
[[ -z "$(docker port "$(api_container)")" ]] || fail "API" "market-api publishes a host port; it must be reachable only by the BFF"
ok "API: market-api is healthy and has no host port"

# ---- BFF
health="$(web_health 30)"
[[ "$health" != missing ]] || fail "BFF" "the market-web container doesn't exist (scripts/market/web.sh)"
[[ "$health" == running/healthy ]] || fail "BFF" "the market-web container is $health (scripts/market/web.sh; docker compose -f docker/docker-compose.local.yml logs market-web)"
[[ "$(curl -sS --max-time 5 "$MARKET_WEB_URL/health")" == ok ]] || fail "BFF" "$MARKET_WEB_URL/health doesn't answer ok"
unlisted="$(curl -sS --max-time 5 -o /dev/null -w '%{http_code}' "$MARKET_WEB_URL/api/tokens")" || fail "BFF" "$MARKET_WEB_URL doesn't answer"
[[ "$unlisted" == 404 ]] || fail "BFF" "GET /api/tokens answered $unlisted through the BFF; a route off the allowlist must get 404"
facets="$(curl -sS --max-time 10 -o /dev/null -w '%{http_code}' "$MARKET_WEB_URL/api/facets")" || fail "BFF" "GET /api/facets failed"
[[ "$facets" == 200 ]] || fail "config" "GET /api/facets through the BFF answered $facets: the BFF's and the API's service keys differ (scripts/market/api.sh, then web.sh)"
ok "BFF: healthy at $MARKET_WEB_URL, refuses a route off its allowlist, and reaches the API with the service key"

# ---- sign in as a seeded account, through the BFF

# the password goes through stdin, not the command line
login_status="$(jq -nc --arg a "$ACCOUNT" --arg p "$PASSWORD" '{account: $a, password: $p}' | bff_curl -sS --max-time 10 -o /dev/null -D "$headers" -w '%{http_code}' \
  -H 'Content-Type: application/json' --data-binary @- "$MARKET_WEB_URL/auth/sign-in")" || fail "BFF" "sign-in request failed"
[[ "$login_status" == 200 ]] || fail "seed" "signing in as $ACCOUNT answered $login_status (scripts/market/seed.sh creates it)"
cookie="$(sed -n 's/^[Ss]et-[Cc]ookie: \(market_session=[^;]*\).*/\1/p' "$headers" | tail -1)"
[[ -n "$cookie" ]] || fail "BFF" "sign-in set no market_session cookie"
printf 'Cookie: %s\n' "$cookie" >"$session_header"
unset cookie
ok "sign-in: $ACCOUNT, through the BFF"

me="$(bff_curl -fsS --max-time 10 -H @"$session_header" "$MARKET_WEB_URL/api/me")" || fail "BFF" "GET /api/me through the BFF failed"
character_id="$(jq -r --arg n "$CHARACTER" '.characters[] | select(.name == $n) | .id' <<<"$me" | head -1)"
[[ -n "$character_id" ]] || fail "seed" "$ACCOUNT has no character named '$CHARACTER'"

# ---- a ticket the game must answer, through the BFF's /api/* proxy
response="$(bff_curl -sS --max-time 10 -D "$headers" -w '\n%{http_code}' -H @"$session_header" -H 'X-Market-Request: 1' -H 'Content-Type: application/json' \
  -d "$(jq -nc --argjson c "$character_id" --arg k "smoke-$(date +%s%N)" '{characterId: $c, amount: 1, idempotencyKey: $k}')" \
  "$MARKET_WEB_URL/api/mmd/withdraw")" || fail "BFF" "POST /api/mmd/withdraw through the BFF failed"
[[ "$(tail -1 <<<"$response")" == 202 ]] || fail "API" "POST /api/mmd/withdraw answered $(tail -1 <<<"$response"): $(head -1 <<<"$response")"
location="$(sed -n 's/^[Ll]ocation: \([^[:space:]]*\).*/\1/p' "$headers" | tail -1)"
[[ "$location" == /api/tickets/* ]] || fail "API" "the ticket's Location '$location' isn't under /api/tickets"
ok "ticket: mmd_withdraw for $CHARACTER created at $location"

deadline=$((SECONDS + TIMEOUT_SECONDS))
while :; do
  ticket="$(bff_curl -fsS --max-time 5 -H @"$session_header" "$MARKET_WEB_URL$location")" || fail "BFF" "GET $location through the BFF failed"
  status="$(jq -r .status <<<"$ticket")"
  [[ "$status" == WAITING || "$status" == CLAIMED ]] || break
  (( SECONDS < deadline )) || break
  sleep 0.5
done

# the session ends at the API, not only in this script
bff_curl -sS --max-time 10 -o /dev/null -H @"$session_header" -H 'Content-Type: application/json' -d '{}' "$MARKET_WEB_URL/auth/sign-out" || true

code="$(jq -r .resultCode <<<"$ticket")"
case "$status/$code" in
  FAILED/offline)
    ok "game: failed the ticket 'offline' ($(jq -r .resultMessage <<<"$ticket"))"
    echo "SMOKE PASS: BFF, API, database and game server see each other."
    ;;
  WAITING/*)
    fail "game" "the ticket is still WAITING after ${TIMEOUT_SECONDS}s: the game server isn't running or isn't polling $MARKET_SHARD_DATABASE (scripts/market/game.sh)" ;;
  CLAIMED/*)
    fail "game" "the game claimed the ticket but didn't finish it within ${TIMEOUT_SECONDS}s" ;;
  *)
    fail "game" "expected FAILED/offline, got $status/$code (is $CHARACTER logged in?)" ;;
esac
