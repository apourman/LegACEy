#!/usr/bin/env bash
set -euo pipefail
source /docker-entrypoint-initdb.d/common.inc

ensure_database "${ACE_DB_WORLD_NAME:-ace_world}"
import_sql_file "$DB_ROOT/Base/WorldBase.sql" "${ACE_DB_WORLD_NAME:-ace_world}"

# WorldDatabase expects a version row before it can compare or download patches.
mysql_exec -D "${ACE_DB_WORLD_NAME:-ace_world}" <<'SQL'
INSERT INTO `version` (`id`, `base_Version`, `patch_Version`)
SELECT 1, '0.0.0', '0.0.0'
WHERE NOT EXISTS (SELECT 1 FROM `version` WHERE `id` = 1);
SQL
