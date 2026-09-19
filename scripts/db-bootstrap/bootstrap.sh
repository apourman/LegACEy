#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE=(docker compose -f "$ROOT/docker/docker-compose.local.yml")
MYSQL_ROOT_PASSWORD="${MYSQL_ROOT_PASSWORD:-1999asheron2017}"

"${COMPOSE[@]}" up -d ace-db
printf 'Waiting for MySQL to accept connections...\n'

for _ in {1..60}; do
  if "${COMPOSE[@]}" exec -T ace-db mysqladmin ping \
      -h 127.0.0.1 -uroot -p"$MYSQL_ROOT_PASSWORD" --silent >/dev/null 2>&1; then
    "${COMPOSE[@]}" ps ace-db
    exit 0
  fi
  sleep 2
done

"${COMPOSE[@]}" ps ace-db >&2
exit 1
