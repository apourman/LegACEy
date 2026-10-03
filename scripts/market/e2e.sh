#!/usr/bin/env bash
# Isolated production-image stack. Its Compose project owns only the API and BFF containers.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

E2E_COMPOSE=(docker compose --project-name market-e2e --env-file "$DOCKER_ENV_FILE" -f "$ROOT/docker/docker-compose.e2e.yml")
CONFIG="${MARKET_E2E_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/legacey/market-e2e}/Config.js"

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
    "${E2E_COMPOSE[@]}" up -d --build
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
    "${E2E_COMPOSE[@]}" down --remove-orphans
    ;;
  *)
    echo "Usage: scripts/market/e2e.sh <up|fresh|audit|down>" >&2
    exit 2
    ;;
esac
