#!/usr/bin/env bash
set -euo pipefail
source /docker-entrypoint-initdb.d/common.inc

ensure_database "${ACE_DB_SHARD_NAME:-ace_shard}"
import_sql_file "$DB_ROOT/Base/ShardBase.sql" "${ACE_DB_SHARD_NAME:-ace_shard}"
