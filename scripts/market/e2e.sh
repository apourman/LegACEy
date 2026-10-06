#!/usr/bin/env bash
# Isolated production-image stack. Its Compose project owns only the API and BFF containers.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

E2E_API_CREDENTIALS="${MARKET_E2E_API_CREDENTIALS_FILE:-${MARKET_E2E_RUN_DIR}/api-db.env}"
CONFIG="$MARKET_E2E_RUN_DIR/Config.js"
E2E_URL=http://127.0.0.1:5174
E2E_SMOKE_ACCOUNT="${MARKET_E2E_SMOKE_ACCOUNT:-website-desktop-alpha}"
E2E_COMPOSE=(docker compose --project-name market-e2e --env-file "$DOCKER_ENV_FILE" -f "$ROOT/docker/docker-compose.e2e.yml")

# one value from the credentials file, after checking its shape: user market_e2e_api, password 64 hex characters
e2e_credential() {
  local name="$1" value
  value="$(sed -n "s/^$name=//p" "$E2E_API_CREDENTIALS" | tail -1)"
  case "$name" in
    MARKET_E2E_API_DB_USER) [[ "$value" == market_e2e_api ]] ;;
    MARKET_E2E_API_DB_PASSWORD) [[ "$value" =~ ^[a-f0-9]{64}$ ]] ;;
  esac || { echo "Invalid E2E API credentials file: $E2E_API_CREDENTIALS" >&2; return 1; }
  printf '%s' "$value"
}

ensure_e2e_api_credentials() {
  local credentials_dir
  credentials_dir="$(dirname "$E2E_API_CREDENTIALS")"
  mkdir -p "$credentials_dir"
  chmod 700 "$credentials_dir"
  if [[ ! -f "$E2E_API_CREDENTIALS" ]]; then
    (umask 077; printf 'MARKET_E2E_API_DB_USER=market_e2e_api\nMARKET_E2E_API_DB_PASSWORD=%s\n' "$(openssl rand -hex 32)" > "$E2E_API_CREDENTIALS")
  fi
  chmod 600 "$E2E_API_CREDENTIALS"
  e2e_credential MARKET_E2E_API_DB_USER >/dev/null
  e2e_credential MARKET_E2E_API_DB_PASSWORD >/dev/null
}

provision_e2e_api_login() {
  local password
  ensure_e2e_api_credentials
  password="$(e2e_credential MARKET_E2E_API_DB_PASSWORD)"
  # The isolated API identity can write only to its two disposable databases. ace_world is shared with the game and stays read-only.
  docker exec -i docker-ace-db-1 sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --default-character-set=utf8mb4' <<SQL
DROP USER IF EXISTS 'market_e2e_api'@'%';
CREATE USER 'market_e2e_api'@'%' IDENTIFIED BY '$password';
GRANT ALL PRIVILEGES ON \`$MARKET_E2E_AUTH_DATABASE\`.* TO 'market_e2e_api'@'%';
GRANT ALL PRIVILEGES ON \`$MARKET_E2E_SHARD_DATABASE\`.* TO 'market_e2e_api'@'%';
GRANT SELECT ON \`ace_world\`.* TO 'market_e2e_api'@'%';
FLUSH PRIVILEGES;
SQL
}

# compose for commands that create or start the API: it needs the generated database login
compose() {
  ensure_e2e_api_credentials
  "${E2E_COMPOSE[@]}" --env-file "$E2E_API_CREDENTIALS" "$@"
}

# compose for commands that only look at or stop existing containers (ps, stop, down). The compose file requires the login variables
# even then; placeholders stand in so these never create a credentials file. Existing containers keep the login they were created with.
compose_existing() {
  MARKET_E2E_API_DB_USER=unused MARKET_E2E_API_DB_PASSWORD=unused "${E2E_COMPOSE[@]}" "$@"
}

stack_running() {
  [[ -n "$(compose_existing ps --status running -q 2>/dev/null || true)" ]]
}

wait_healthy() {
  local service="$1" id state
  for _ in $(seq 1 90); do
    id="$(compose_existing ps -q "$service" 2>/dev/null || true)"
    if [[ -n "$id" ]]; then
      state="$(docker inspect -f '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$id" 2>/dev/null || echo missing)"
      [[ "$state" == running/healthy ]] && return 0
      [[ "$state" == exited/* || "$state" == dead/* ]] && break
    fi
    sleep 2
  done
  echo "$service did not become healthy (last state: ${state:-missing})." >&2
  return 1
}

# Signs in as a seeded end-to-end account and browses listings through the production BFF. The password and the session cookie stay
# in private temporary files, never on the command line.
smoke() {
  local work status rows
  command -v jq >/dev/null || { echo "E2E SMOKE FAIL: jq is required." >&2; return 1; }
  work="$(mktemp -d)"
  trap 'rm -rf "${work:-}"; trap - RETURN' RETURN

  [[ "$(curl -sS --max-time 5 "$E2E_URL/health")" == ok ]] || { echo "E2E SMOKE FAIL: $E2E_URL/health doesn't answer ok." >&2; return 1; }

  status="$(jq -nc --arg a "$E2E_SMOKE_ACCOUNT" --arg p "$MARKET_SEED_PASSWORD" '{account: $a, password: $p}' \
    | curl -sS --max-time 10 -o /dev/null -D "$work/headers" -w '%{http_code}' -H "Origin: $E2E_URL" -H 'Content-Type: application/json' \
      --data-binary @- "$E2E_URL/auth/sign-in")" || { echo "E2E SMOKE FAIL: the sign-in request failed." >&2; return 1; }
  [[ "$status" == 200 ]] || { echo "E2E SMOKE FAIL: signing in as $E2E_SMOKE_ACCOUNT answered $status." >&2; return 1; }
  sed -n 's/^[Ss]et-[Cc]ookie: \(market_session=[^;]*\).*/Cookie: \1/p' "$work/headers" | tail -1 > "$work/session"
  [[ -s "$work/session" ]] || { echo "E2E SMOKE FAIL: sign-in set no market_session cookie." >&2; return 1; }

  rows="$(curl -fsS --max-time 10 -H @"$work/session" "$E2E_URL/api/listings" | jq '.listings | length')" \
    || { echo "E2E SMOKE FAIL: GET /api/listings through the BFF failed." >&2; return 1; }
  (( rows > 0 )) || { echo "E2E SMOKE FAIL: browse returned no listings; the seed made none." >&2; return 1; }

  curl -sS --max-time 10 -o /dev/null -H "Origin: $E2E_URL" -H @"$work/session" -H 'Content-Type: application/json' -d '{}' "$E2E_URL/auth/sign-out" || true
  echo "E2E smoke: signed in as $E2E_SMOKE_ACCOUNT and browsed $rows listings at $E2E_URL."
}

case "${1:-}" in
  up)
    # fresh drops the databases: never under a running API
    stack_running && compose_existing stop
    "$ROOT/scripts/market/fresh.sh"
    provision_e2e_api_login
    compose up -d --build
    wait_healthy market-api
    wait_healthy market-bff
    smoke
    echo "End-to-end stack healthy at $E2E_URL."
    ;;
  fresh)
    # fresh drops the databases: a running stack is stopped first and started again on the new data
    if stack_running; then
      compose_existing stop
      "$ROOT/scripts/market/fresh.sh"
      compose start
      wait_healthy market-api
      wait_healthy market-bff
    else
      "$ROOT/scripts/market/fresh.sh"
    fi
    ;;
  smoke)
    smoke
    ;;
  audit)
    [[ -f "$CONFIG" ]] || { echo "Missing end-to-end config; run scripts/market/fresh.sh first." >&2; exit 1; }
    MARKET_DEV_ROOT="$ROOT" MARKET_DEV_CONFIG="$CONFIG" "$ROOT/scripts/market/dev.sh" audit
    ;;
  down)
    compose_existing down --remove-orphans
    ;;
  *)
    echo "Usage: scripts/market/e2e.sh <up|fresh|smoke|audit|down>" >&2
    exit 2
    ;;
esac
