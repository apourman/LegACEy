#!/usr/bin/env bash
# Isolated production-image stack. Its Compose project owns only the API and BFF containers.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

E2E_RUN_DIR="${MARKET_E2E_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-e2e}"
E2E_API_CREDENTIALS="${MARKET_E2E_API_CREDENTIALS_FILE:-${E2E_RUN_DIR}/api-db.env}"
CONFIG="$E2E_RUN_DIR/Config.js"
E2E_COMPOSE=(docker compose --project-name market-e2e --env-file "$DOCKER_ENV_FILE" --env-file "$E2E_API_CREDENTIALS" -f "$ROOT/docker/docker-compose.e2e.yml")

ensure_e2e_api_credentials() {
  local credentials_dir password user
  credentials_dir="$(dirname "$E2E_API_CREDENTIALS")"
  mkdir -p "$credentials_dir"
  chmod 700 "$credentials_dir"
  if [[ ! -f "$E2E_API_CREDENTIALS" ]]; then
    password="$(openssl rand -hex 32)"
    (umask 077; printf 'MARKET_E2E_API_DB_USER=market_e2e_api\nMARKET_E2E_API_DB_PASSWORD=%s\n' "$password" > "$E2E_API_CREDENTIALS")
  fi
  chmod 600 "$E2E_API_CREDENTIALS"
  user="$(sed -n 's/^MARKET_E2E_API_DB_USER=//p' "$E2E_API_CREDENTIALS" | tail -1)"
  password="$(sed -n 's/^MARKET_E2E_API_DB_PASSWORD=//p' "$E2E_API_CREDENTIALS" | tail -1)"
  [[ "$user" == market_e2e_api && "$password" =~ ^[a-f0-9]{64}$ ]] || {
    echo "Invalid E2E API credentials file: $E2E_API_CREDENTIALS" >&2
    return 1
  }
}

provision_e2e_api_login() {
  local password
  password="$(sed -n 's/^MARKET_E2E_API_DB_PASSWORD=//p' "$E2E_API_CREDENTIALS" | tail -1)"
  [[ "$password" =~ ^[a-f0-9]{64}$ ]] || { echo "Invalid E2E API credentials file: $E2E_API_CREDENTIALS" >&2; return 1; }
  # The isolated API identity can write only to its two disposable databases. ace_world is shared with the game and stays read-only.
  docker exec -i docker-ace-db-1 sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --default-character-set=utf8mb4' <<SQL
DROP USER IF EXISTS 'market_e2e_api'@'%';
CREATE USER 'market_e2e_api'@'%' IDENTIFIED BY '$password';
GRANT ALL PRIVILEGES ON \`ace_market_e2e_auth\`.* TO 'market_e2e_api'@'%';
GRANT ALL PRIVILEGES ON \`ace_market_e2e_shard\`.* TO 'market_e2e_api'@'%';
GRANT SELECT ON \`ace_world\`.* TO 'market_e2e_api'@'%';
FLUSH PRIVILEGES;
SQL
}

compose() {
  ensure_e2e_api_credentials
  "${E2E_COMPOSE[@]}" "$@"
}

wait_healthy() {
  local service="$1" id state
  for _ in $(seq 1 90); do
    id="$("${E2E_COMPOSE[@]}" ps -q "$service" 2>/dev/null || true)"
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

case "${1:-}" in
  up)
    "$ROOT/scripts/market/fresh.sh"
    ensure_e2e_api_credentials
    provision_e2e_api_login
    compose up -d --build
    wait_healthy market-api
    wait_healthy market-bff
    echo "End-to-end stack healthy at http://127.0.0.1:5174."
    ;;
  fresh)
    "$ROOT/scripts/market/fresh.sh"
    ;;
  audit)
    [[ -f "$CONFIG" ]] || { echo "Missing end-to-end config; run scripts/market/fresh.sh first." >&2; exit 1; }
    MARKET_DEV_ROOT="$ROOT" MARKET_DEV_CONFIG="$CONFIG" "$ROOT/scripts/market/dev.sh" audit
    ;;
  down)
    compose down --remove-orphans
    ;;
  *)
    echo "Usage: scripts/market/e2e.sh <up|fresh|audit|down>" >&2
    exit 2
    ;;
esac
