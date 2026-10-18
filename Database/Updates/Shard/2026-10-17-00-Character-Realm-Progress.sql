-- Character realm progress: one value per character, realm and key.
-- The table is generic key-value and defines no game rules: the game decides what the keys mean (completion is one key).
-- character_Id matches character.id (bigint unsigned, widened by the 2026-10-16 migration) and is deleted with its character.
-- Idempotent: CREATE TABLE IF NOT EXISTS, so the bootstrap and the update runner may both apply it.

CREATE TABLE IF NOT EXISTS `character_realm_progress` (
  `character_Id` bigint unsigned NOT NULL,
  `realm_Id` smallint unsigned NOT NULL COMMENT 'Realm id (0..32767); 0 is the base world',
  `key` varchar(64) NOT NULL,
  `value` bigint NOT NULL,
  `last_Modified` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`character_Id`, `realm_Id`, `key`),
  CONSTRAINT `FK_character_realm_progress_character_Id` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Realm progress per character: generic key-value, no game rules';
