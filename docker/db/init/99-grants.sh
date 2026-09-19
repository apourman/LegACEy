#!/usr/bin/env bash
set -euo pipefail
source /docker-entrypoint-initdb.d/common.inc

user="${MYSQL_USER:-acedockeruser}"
password="${MYSQL_PASSWORD:-acedocker-password}"
user_sql="${user//\'/\'\'}"
password_sql="${password//\'/\'\'}"

mysql_exec -e "CREATE USER IF NOT EXISTS '${user_sql}'@'%' IDENTIFIED BY '${password_sql}';"
for database in "${ACE_DB_AUTH_NAME:-ace_auth}" "${ACE_DB_SHARD_NAME:-ace_shard}" "${ACE_DB_WORLD_NAME:-ace_world}"; do
  mysql_exec -e "GRANT ALL PRIVILEGES ON \`${database}\`.* TO '${user_sql}'@'%';"
done
mysql_exec -e 'FLUSH PRIVILEGES;'
