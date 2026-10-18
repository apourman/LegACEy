-- Instancing: 64-bit object ids and the instance of a saved position.
-- A static object in a landblock copy keeps its 32-bit guid in the low half of its id and the copy's instance in the high half,
-- so biota ids and every column that stores an object or character id widen to bigint unsigned. Existing values don't change:
-- base-world statics have instance 0, so their 64-bit id equals their old 32-bit id. A saved position gains a nullable
-- instance (NULL is the base world).
-- MySQL refuses to change a column's type while a foreign key of the old type points at it or from it, so each child table drops
-- its keys in the same statement that widens it, the parents widen next, and the keys are added back last.
-- Foreign key checks are off for the run so the keys are re-added without re-checking every row (the values are unchanged).
-- Safe to run more than once: a table is widened only while its column is still int, a key is added only while it is missing, and
-- the instance column only while it is absent. The market tables are skipped when the market schema isn't installed.
-- Back up the shard database before applying this: each widened table is rebuilt.

SET @instancing_foreign_key_checks = @@FOREIGN_KEY_CHECKS;
SET FOREIGN_KEY_CHECKS = 0;
-- children: drop their keys and widen
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_allegiance' AND COLUMN_NAME = 'allegiance_Id') = 'int',
  'ALTER TABLE `biota_properties_allegiance` DROP FOREIGN KEY `FK_allegiance_biota_Id`, DROP FOREIGN KEY `FK_allegiance_character_Id`, MODIFY `allegiance_Id` bigint unsigned NOT NULL, MODIFY `character_Id` bigint unsigned NOT NULL',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_anim_part' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_anim_part` DROP FOREIGN KEY `wcid_animpart`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_attribute' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_attribute` DROP FOREIGN KEY `wcid_attribute`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_attribute_2nd' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_attribute_2nd` DROP FOREIGN KEY `wcid_attribute2nd`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_body_part' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_body_part` DROP FOREIGN KEY `wcid_bodypart`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_book' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_book` DROP FOREIGN KEY `wcid_bookdata`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_book_page_data' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_book_page_data` DROP FOREIGN KEY `wcid_pagedata`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the Book object this page belongs to'', MODIFY `author_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the Author of this page''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_bool' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_bool` DROP FOREIGN KEY `wcid_bool`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_create_list' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_create_list` DROP FOREIGN KEY `wcid_createlist`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_d_i_d' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_d_i_d` DROP FOREIGN KEY `wcid_did`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_emote' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_emote` DROP FOREIGN KEY `wcid_emote`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_enchantment_registry' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_enchantment_registry` DROP FOREIGN KEY `wcid_enchantmentregistry`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to'', MODIFY `caster_Object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object that cast this spell''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_event_filter' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_event_filter` DROP FOREIGN KEY `wcid_eventfilter`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_float' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_float` DROP FOREIGN KEY `wcid_float`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_generator' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_generator` DROP FOREIGN KEY `wcid_generator`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_i_i_d' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_i_i_d` DROP FOREIGN KEY `wcid_iid`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to'', MODIFY `value` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Value of this Property''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_int' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_int` DROP FOREIGN KEY `wcid_int`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_int64' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_int64` DROP FOREIGN KEY `wcid_int64`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_palette' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_palette` DROP FOREIGN KEY `wcid_palette`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_position' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_position` DROP FOREIGN KEY `wcid_position`, MODIFY `object_Id` bigint unsigned NOT NULL COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_skill' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_skill` DROP FOREIGN KEY `wcid_skill`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_spell_book' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_spell_book` DROP FOREIGN KEY `wcid_spellbook`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_string' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_string` DROP FOREIGN KEY `wcid_string`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_texture_map' AND COLUMN_NAME = 'object_Id') = 'int',
  'ALTER TABLE `biota_properties_texture_map` DROP FOREIGN KEY `wcid_texturemap`, MODIFY `object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the object this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'house_permission' AND COLUMN_NAME = 'house_Id') = 'int',
  'ALTER TABLE `house_permission` DROP FOREIGN KEY `biota_Id_house_Id`, MODIFY `house_Id` bigint unsigned NOT NULL COMMENT ''GUID of House Biota Object'', MODIFY `player_Guid` bigint unsigned NOT NULL COMMENT ''GUID of Player Biota Object being granted permission to this house''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_contract_registry' AND COLUMN_NAME = 'character_Id') = 'int',
  'ALTER TABLE `character_properties_contract_registry` DROP FOREIGN KEY `wcid_contract`, MODIFY `character_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the character this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_fill_comp_book' AND COLUMN_NAME = 'character_Id') = 'int',
  'ALTER TABLE `character_properties_fill_comp_book` DROP FOREIGN KEY `wcid_fillcompbook`, MODIFY `character_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the character this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_friend_list' AND COLUMN_NAME = 'character_Id') = 'int',
  'ALTER TABLE `character_properties_friend_list` DROP FOREIGN KEY `wcid_friend`, MODIFY `character_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the character this property belongs to'', MODIFY `friend_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of Friend''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_quest_registry' AND COLUMN_NAME = 'character_Id') = 'int',
  'ALTER TABLE `character_properties_quest_registry` DROP FOREIGN KEY `wcid_questbook`, MODIFY `character_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the character this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_shortcut_bar' AND COLUMN_NAME = 'character_Id') = 'int',
  'ALTER TABLE `character_properties_shortcut_bar` DROP FOREIGN KEY `wcid_shortcutbar`, MODIFY `character_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the character this property belongs to'', MODIFY `shortcut_Object_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Guid of the object at this Slot''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_spell_bar' AND COLUMN_NAME = 'character_Id') = 'int',
  'ALTER TABLE `character_properties_spell_bar` DROP FOREIGN KEY `characterId_spellbar`, MODIFY `character_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the character this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_squelch' AND COLUMN_NAME = 'character_Id') = 'int',
  'ALTER TABLE `character_properties_squelch` DROP FOREIGN KEY `squelch_character_Id_constraint`, MODIFY `character_Id` bigint unsigned NOT NULL, MODIFY `squelch_Character_Id` bigint unsigned NOT NULL',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_title_book' AND COLUMN_NAME = 'character_Id') = 'int',
  'ALTER TABLE `character_properties_title_book` DROP FOREIGN KEY `wcid_titlebook`, MODIFY `character_Id` bigint unsigned NOT NULL DEFAULT ''0'' COMMENT ''Id of the character this property belongs to''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_vault_item' AND COLUMN_NAME = 'item_Guid') = 'int',
  'ALTER TABLE `market_vault_item` DROP FOREIGN KEY `market_vault_item_biota`, MODIFY `item_Guid` bigint unsigned NOT NULL COMMENT ''biota.id of the escrowed item''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_listing' AND COLUMN_NAME = 'item_Guid') = 'int',
  'ALTER TABLE `market_listing` MODIFY `item_Guid` bigint unsigned NOT NULL',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_item_event' AND COLUMN_NAME = 'item_Guid') = 'int',
  'ALTER TABLE `market_item_event` MODIFY `item_Guid` bigint unsigned NOT NULL',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;

-- parents
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota' AND COLUMN_NAME = 'id') = 'int',
  'ALTER TABLE `biota` MODIFY `id` bigint unsigned NOT NULL AUTO_INCREMENT COMMENT ''Unique Object Id within the Shard''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character' AND COLUMN_NAME = 'id') = 'int',
  'ALTER TABLE `character` MODIFY `id` bigint unsigned NOT NULL COMMENT ''Id of the Biota for this Character''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;

-- the keys, back as they were
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_allegiance') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_allegiance' AND CONSTRAINT_NAME = 'FK_allegiance_biota_Id') = 0,
  'ALTER TABLE `biota_properties_allegiance` ADD CONSTRAINT `FK_allegiance_biota_Id` FOREIGN KEY (`allegiance_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_allegiance') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_allegiance' AND CONSTRAINT_NAME = 'FK_allegiance_character_Id') = 0,
  'ALTER TABLE `biota_properties_allegiance` ADD CONSTRAINT `FK_allegiance_character_Id` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_anim_part') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_anim_part' AND CONSTRAINT_NAME = 'wcid_animpart') = 0,
  'ALTER TABLE `biota_properties_anim_part` ADD CONSTRAINT `wcid_animpart` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_attribute') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_attribute' AND CONSTRAINT_NAME = 'wcid_attribute') = 0,
  'ALTER TABLE `biota_properties_attribute` ADD CONSTRAINT `wcid_attribute` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_attribute_2nd') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_attribute_2nd' AND CONSTRAINT_NAME = 'wcid_attribute2nd') = 0,
  'ALTER TABLE `biota_properties_attribute_2nd` ADD CONSTRAINT `wcid_attribute2nd` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_body_part') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_body_part' AND CONSTRAINT_NAME = 'wcid_bodypart') = 0,
  'ALTER TABLE `biota_properties_body_part` ADD CONSTRAINT `wcid_bodypart` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_book') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_book' AND CONSTRAINT_NAME = 'wcid_bookdata') = 0,
  'ALTER TABLE `biota_properties_book` ADD CONSTRAINT `wcid_bookdata` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_book_page_data') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_book_page_data' AND CONSTRAINT_NAME = 'wcid_pagedata') = 0,
  'ALTER TABLE `biota_properties_book_page_data` ADD CONSTRAINT `wcid_pagedata` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_bool') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_bool' AND CONSTRAINT_NAME = 'wcid_bool') = 0,
  'ALTER TABLE `biota_properties_bool` ADD CONSTRAINT `wcid_bool` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_create_list') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_create_list' AND CONSTRAINT_NAME = 'wcid_createlist') = 0,
  'ALTER TABLE `biota_properties_create_list` ADD CONSTRAINT `wcid_createlist` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_d_i_d') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_d_i_d' AND CONSTRAINT_NAME = 'wcid_did') = 0,
  'ALTER TABLE `biota_properties_d_i_d` ADD CONSTRAINT `wcid_did` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_emote') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_emote' AND CONSTRAINT_NAME = 'wcid_emote') = 0,
  'ALTER TABLE `biota_properties_emote` ADD CONSTRAINT `wcid_emote` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_enchantment_registry') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_enchantment_registry' AND CONSTRAINT_NAME = 'wcid_enchantmentregistry') = 0,
  'ALTER TABLE `biota_properties_enchantment_registry` ADD CONSTRAINT `wcid_enchantmentregistry` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_event_filter') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_event_filter' AND CONSTRAINT_NAME = 'wcid_eventfilter') = 0,
  'ALTER TABLE `biota_properties_event_filter` ADD CONSTRAINT `wcid_eventfilter` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_float') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_float' AND CONSTRAINT_NAME = 'wcid_float') = 0,
  'ALTER TABLE `biota_properties_float` ADD CONSTRAINT `wcid_float` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_generator') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_generator' AND CONSTRAINT_NAME = 'wcid_generator') = 0,
  'ALTER TABLE `biota_properties_generator` ADD CONSTRAINT `wcid_generator` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_i_i_d') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_i_i_d' AND CONSTRAINT_NAME = 'wcid_iid') = 0,
  'ALTER TABLE `biota_properties_i_i_d` ADD CONSTRAINT `wcid_iid` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_int') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_int' AND CONSTRAINT_NAME = 'wcid_int') = 0,
  'ALTER TABLE `biota_properties_int` ADD CONSTRAINT `wcid_int` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_int64') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_int64' AND CONSTRAINT_NAME = 'wcid_int64') = 0,
  'ALTER TABLE `biota_properties_int64` ADD CONSTRAINT `wcid_int64` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_palette') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_palette' AND CONSTRAINT_NAME = 'wcid_palette') = 0,
  'ALTER TABLE `biota_properties_palette` ADD CONSTRAINT `wcid_palette` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_position') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_position' AND CONSTRAINT_NAME = 'wcid_position') = 0,
  'ALTER TABLE `biota_properties_position` ADD CONSTRAINT `wcid_position` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_skill') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_skill' AND CONSTRAINT_NAME = 'wcid_skill') = 0,
  'ALTER TABLE `biota_properties_skill` ADD CONSTRAINT `wcid_skill` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_spell_book') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_spell_book' AND CONSTRAINT_NAME = 'wcid_spellbook') = 0,
  'ALTER TABLE `biota_properties_spell_book` ADD CONSTRAINT `wcid_spellbook` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_string') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_string' AND CONSTRAINT_NAME = 'wcid_string') = 0,
  'ALTER TABLE `biota_properties_string` ADD CONSTRAINT `wcid_string` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_texture_map') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_texture_map' AND CONSTRAINT_NAME = 'wcid_texturemap') = 0,
  'ALTER TABLE `biota_properties_texture_map` ADD CONSTRAINT `wcid_texturemap` FOREIGN KEY (`object_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'house_permission') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'house_permission' AND CONSTRAINT_NAME = 'biota_Id_house_Id') = 0,
  'ALTER TABLE `house_permission` ADD CONSTRAINT `biota_Id_house_Id` FOREIGN KEY (`house_Id`) REFERENCES `biota` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_contract_registry') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_contract_registry' AND CONSTRAINT_NAME = 'wcid_contract') = 0,
  'ALTER TABLE `character_properties_contract_registry` ADD CONSTRAINT `wcid_contract` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_fill_comp_book') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_fill_comp_book' AND CONSTRAINT_NAME = 'wcid_fillcompbook') = 0,
  'ALTER TABLE `character_properties_fill_comp_book` ADD CONSTRAINT `wcid_fillcompbook` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_friend_list') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_friend_list' AND CONSTRAINT_NAME = 'wcid_friend') = 0,
  'ALTER TABLE `character_properties_friend_list` ADD CONSTRAINT `wcid_friend` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_quest_registry') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_quest_registry' AND CONSTRAINT_NAME = 'wcid_questbook') = 0,
  'ALTER TABLE `character_properties_quest_registry` ADD CONSTRAINT `wcid_questbook` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_shortcut_bar') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_shortcut_bar' AND CONSTRAINT_NAME = 'wcid_shortcutbar') = 0,
  'ALTER TABLE `character_properties_shortcut_bar` ADD CONSTRAINT `wcid_shortcutbar` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_spell_bar') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_spell_bar' AND CONSTRAINT_NAME = 'characterId_spellbar') = 0,
  'ALTER TABLE `character_properties_spell_bar` ADD CONSTRAINT `characterId_spellbar` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_squelch') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_squelch' AND CONSTRAINT_NAME = 'squelch_character_Id_constraint') = 0,
  'ALTER TABLE `character_properties_squelch` ADD CONSTRAINT `squelch_character_Id_constraint` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_title_book') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'character_properties_title_book' AND CONSTRAINT_NAME = 'wcid_titlebook') = 0,
  'ALTER TABLE `character_properties_title_book` ADD CONSTRAINT `wcid_titlebook` FOREIGN KEY (`character_Id`) REFERENCES `character` (`id`) ON DELETE CASCADE',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_vault_item') = 1
    AND (SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'market_vault_item' AND CONSTRAINT_NAME = 'market_vault_item_biota') = 0,
  'ALTER TABLE `market_vault_item` ADD CONSTRAINT `market_vault_item_biota` FOREIGN KEY (`item_Guid`) REFERENCES `biota` (`id`) ON DELETE RESTRICT ON UPDATE RESTRICT',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;

-- the instance a saved position is in; NULL is the base world
SET @instancing_sql = IF((SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'biota_properties_position' AND COLUMN_NAME = 'instance') = 0,
  'ALTER TABLE `biota_properties_position` ADD COLUMN `instance` int unsigned NULL DEFAULT NULL COMMENT ''Instance of the landblock copy this position is in; NULL is the base world''',
  'SELECT 1');
PREPARE instancing_statement FROM @instancing_sql;
EXECUTE instancing_statement;
DEALLOCATE PREPARE instancing_statement;

SET FOREIGN_KEY_CHECKS = @instancing_foreign_key_checks;
