#!/usr/bin/env bash
set -euo pipefail
source /docker-entrypoint-initdb.d/common.inc

ensure_database "${ACE_DB_AUTH_NAME:-ace_auth}"
import_sql_file "$DB_ROOT/Base/AuthenticationBase.sql" "${ACE_DB_AUTH_NAME:-ace_auth}"
