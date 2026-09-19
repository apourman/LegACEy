#!/usr/bin/env bash
set -euo pipefail

DB_ROOT="${DB_ROOT:-/repo/Database}"
MYSQL_PASSWORD="${MYSQL_ROOT_PASSWORD:?MYSQL_ROOT_PASSWORD is required}"

mysql_exec() {
  mysql --protocol=socket -uroot -p"${MYSQL_PASSWORD}" --default-character-set=utf8mb4 "$@"
}

ensure_database() {
  mysql_exec -e "CREATE DATABASE IF NOT EXISTS \`${1}\` DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;"
}

import_sql_file() {
  mysql_exec -D "$2" < "$1"
}
