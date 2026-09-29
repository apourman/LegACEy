-- Marketplace: every table the market stores, and the escrow and money invariants MySQL enforces.
-- Safe to run more than once: every statement is create-if-not-exists.
-- The update runner sends this file as one command without DELIMITER support, so each trigger body is a single statement,
-- and nothing here may use @variables (the runner's connection doesn't allow them).
-- Creating triggers while binary logging is on needs log_bin_trust_function_creators=1 (set in the docker-compose files) or SUPER.
-- The triggers come last: if they fail, the market startup check (ACE.Database.Market.MarketSchema) reports them missing.
-- Recovery when the triggers failed (the runner still marks this script applied): enable log_bin_trust_function_creators
-- (or run as a SUPER user), remove this file's line from the shard applied_updates.txt, and apply it again. It is idempotent.

-- An escrowed item. The item keeps its biota row, with no container, wielder or location; this row is the only record of its owner.
-- The foreign key refuses to delete or renumber the biota row while the item is in a Vault.
CREATE TABLE IF NOT EXISTS `market_vault_item` (
  `item_Guid` int unsigned NOT NULL COMMENT 'biota.id of the escrowed item',
  `account_Id` int unsigned NOT NULL,
  `character_Id` int unsigned NOT NULL COMMENT 'the character that deposited it',
  `state` varchar(16) NOT NULL DEFAULT 'held',
  `deposited_Time` datetime(6) NOT NULL,
  `row_Version` int unsigned NOT NULL DEFAULT 0,
  `wcid` int unsigned NOT NULL,
  `name` varchar(255) NOT NULL,
  `item_Type` int NOT NULL,
  `stack_Size` int NOT NULL DEFAULT 1,
  `value` int NOT NULL DEFAULT 0,
  `icon_Underlay` int unsigned DEFAULT NULL,
  `icon` int unsigned DEFAULT NULL,
  `icon_Overlay` int unsigned DEFAULT NULL,
  `icon_Overlay_Secondary` int unsigned DEFAULT NULL,
  `ui_Effects` int DEFAULT NULL,
  `palette_Template` int DEFAULT NULL,
  `clothing_Base` int unsigned DEFAULT NULL,
  `workmanship` int DEFAULT NULL,
  `arcane_Lore` int DEFAULT NULL,
  `wield_Requirements` int DEFAULT NULL,
  `wield_Skill_Type` int DEFAULT NULL,
  `wield_Difficulty` int DEFAULT NULL,
  `armor_Level` int DEFAULT NULL,
  `damage` int DEFAULT NULL,
  `damage_Mod` double DEFAULT NULL,
  `material_Type` int DEFAULT NULL,
  `equipment_Set_Id` int DEFAULT NULL,
  `imbued_Effect` int DEFAULT NULL,
  PRIMARY KEY (`item_Guid`),
  KEY `market_vault_item_account_state_idx` (`account_Id`,`state`),
  CONSTRAINT `market_vault_item_biota` FOREIGN KEY (`item_Guid`) REFERENCES `biota` (`id`) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT `market_vault_item_state_chk` CHECK (`state` IN ('held','listed','withdrawing'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: items held in escrow';

CREATE TABLE IF NOT EXISTS `market_listing` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `item_Guid` int unsigned NOT NULL,
  `seller_Account_Id` int unsigned NOT NULL,
  `seller_Character_Id` int unsigned NOT NULL,
  `price` bigint NOT NULL COMMENT 'whole MMD for the whole stack',
  `status` varchar(16) NOT NULL DEFAULT 'active',
  `buyer_Account_Id` int unsigned DEFAULT NULL,
  `buyer_Character_Id` int unsigned DEFAULT NULL,
  `created_Time` datetime(6) NOT NULL,
  `closed_Time` datetime(6) DEFAULT NULL,
  `row_Version` int unsigned NOT NULL DEFAULT 0,
  PRIMARY KEY (`id`),
  KEY `market_listing_item_idx` (`item_Guid`),
  KEY `market_listing_status_created_idx` (`status`,`created_Time`),
  KEY `market_listing_status_price_idx` (`status`,`price`),
  KEY `market_listing_seller_status_idx` (`seller_Account_Id`,`status`),
  CONSTRAINT `market_listing_price_chk` CHECK (`price` > 0),
  CONSTRAINT `market_listing_status_chk` CHECK (`status` IN ('active','sold','delisted','expired','ban_returned'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: listings of Vault items';

-- One row per player account. System accounts (NOTES, FEES, ADMIN) have none; their totals are summed from their entries.
CREATE TABLE IF NOT EXISTS `market_balance` (
  `account_Id` int unsigned NOT NULL,
  `balance` bigint NOT NULL DEFAULT 0,
  `last_Sequence` bigint NOT NULL DEFAULT 0 COMMENT 'the last per-account ledger sequence used',
  `row_Version` int unsigned NOT NULL DEFAULT 0,
  PRIMARY KEY (`account_Id`),
  CONSTRAINT `market_balance_nonnegative_chk` CHECK (`balance` >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: MMD balance per player account';

-- Append-only (see the triggers at the end). Corrections are new reversal or admin_adjust transfers.
CREATE TABLE IF NOT EXISTS `market_transfer` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `kind` varchar(16) NOT NULL,
  `actor_Account_Id` int unsigned DEFAULT NULL,
  `actor_Character_Id` int unsigned DEFAULT NULL,
  `listing_Id` bigint DEFAULT NULL,
  `ticket_Id` bigint DEFAULT NULL,
  `request_Key` varchar(64) COLLATE utf8mb4_bin DEFAULT NULL COMMENT 'idempotency key of the request that made it',
  `reverses_Transfer_Id` bigint DEFAULT NULL,
  `memo` varchar(512) DEFAULT NULL,
  `created_Time` datetime(6) NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `market_transfer_reverses_uidx` (`reverses_Transfer_Id`),
  KEY `market_transfer_listing_idx` (`listing_Id`),
  CONSTRAINT `market_transfer_reverses` FOREIGN KEY (`reverses_Transfer_Id`) REFERENCES `market_transfer` (`id`) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT `market_transfer_kind_chk` CHECK (`kind` IN ('note_deposit','note_withdraw','purchase','admin_adjust','reversal')),
  CONSTRAINT `market_transfer_memo_chk` CHECK (`kind` NOT IN ('admin_adjust','reversal') OR (`memo` IS NOT NULL AND `memo` <> ''))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: double-entry ledger transfers (append-only)';

-- Each entry belongs to a player account (with a sequence number and the balance after it) or a system account.
-- A transfer's entries add up to zero; the ledger audit checks that.
CREATE TABLE IF NOT EXISTS `market_ledger_entry` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `transfer_Id` bigint NOT NULL,
  `account_Id` int unsigned DEFAULT NULL,
  `system_Account` varchar(8) DEFAULT NULL,
  `amount` bigint NOT NULL,
  `sequence` bigint DEFAULT NULL COMMENT 'per-account sequence, the history cursor',
  `balance_After` bigint DEFAULT NULL,
  `memo` varchar(255) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `market_ledger_entry_transfer_idx` (`transfer_Id`),
  UNIQUE KEY `market_ledger_entry_account_sequence_uidx` (`account_Id`,`sequence`),
  KEY `market_ledger_entry_system_idx` (`system_Account`),
  CONSTRAINT `market_ledger_entry_transfer` FOREIGN KEY (`transfer_Id`) REFERENCES `market_transfer` (`id`) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT `market_ledger_entry_owner_chk` CHECK ((`account_Id` IS NULL) <> (`system_Account` IS NULL)),
  CONSTRAINT `market_ledger_entry_system_chk` CHECK (`system_Account` IS NULL OR `system_Account` IN ('NOTES','FEES','ADMIN')),
  CONSTRAINT `market_ledger_entry_player_chk` CHECK (`account_Id` IS NULL OR (`sequence` IS NOT NULL AND `balance_After` IS NOT NULL AND `balance_After` >= 0)),
  CONSTRAINT `market_ledger_entry_sysfields_chk` CHECK (`account_Id` IS NOT NULL OR (`sequence` IS NULL AND `balance_After` IS NULL))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: ledger entries (append-only)';

CREATE TABLE IF NOT EXISTS `market_item_event` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `item_Guid` int unsigned NOT NULL,
  `account_Id` int unsigned NOT NULL,
  `character_Id` int unsigned DEFAULT NULL,
  `kind` varchar(16) NOT NULL,
  `listing_Id` bigint DEFAULT NULL,
  `transfer_Id` bigint DEFAULT NULL COMMENT 'the transfer this event belongs to, such as the note_deposit that destroyed the notes',
  `quantity` int DEFAULT NULL COMMENT 'stack size moved, where it matters',
  `event_Time` datetime(6) NOT NULL,
  PRIMARY KEY (`id`),
  KEY `market_item_event_item_idx` (`item_Guid`),
  KEY `market_item_event_account_idx` (`account_Id`,`id`),
  KEY `market_item_event_transfer_idx` (`transfer_Id`),
  CONSTRAINT `market_item_event_kind_chk` CHECK (`kind` IN ('deposit','withdraw','list','delist','expire','sold','bought','ban_return','admin'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: item movements';

-- The game bridge queue: WAITING -> CLAIMED -> DONE or FAILED
CREATE TABLE IF NOT EXISTS `market_ticket` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `kind` varchar(32) NOT NULL,
  `account_Id` int unsigned NOT NULL,
  `character_Id` int unsigned DEFAULT NULL,
  `payload` json DEFAULT NULL,
  `status` varchar(8) NOT NULL DEFAULT 'WAITING',
  `result_Code` varchar(32) DEFAULT NULL,
  `result_Message` varchar(512) DEFAULT NULL,
  `idempotency_Key` varchar(64) COLLATE utf8mb4_bin NOT NULL COMMENT 'compared exactly: keys differing only in case are different',
  `created_Time` datetime(6) NOT NULL,
  `claimed_Time` datetime(6) DEFAULT NULL,
  `finished_Time` datetime(6) DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `market_ticket_account_key_uidx` (`account_Id`,`idempotency_Key`),
  KEY `market_ticket_status_idx` (`status`,`id`),
  KEY `market_ticket_finished_idx` (`finished_Time`),
  CONSTRAINT `market_ticket_status_chk` CHECK (`status` IN ('WAITING','CLAIMED','DONE','FAILED'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: game bridge tickets';

-- Stored API results for idempotent replays, kept 30 days
CREATE TABLE IF NOT EXISTS `market_request` (
  `account_Id` int unsigned NOT NULL,
  `idempotency_Key` varchar(64) COLLATE utf8mb4_bin NOT NULL COMMENT 'compared exactly: keys differing only in case are different',
  `kind` varchar(32) NOT NULL,
  `result` json DEFAULT NULL,
  `created_Time` datetime(6) NOT NULL,
  PRIMARY KEY (`account_Id`,`idempotency_Key`),
  KEY `market_request_created_idx` (`created_Time`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: idempotent request results';

CREATE TABLE IF NOT EXISTS `market_link_code` (
  `code_Hash` varbinary(64) NOT NULL,
  `account_Id` int unsigned NOT NULL,
  `character_Id` int unsigned NOT NULL,
  `expires_Time` datetime(6) NOT NULL,
  `used_Time` datetime(6) DEFAULT NULL,
  PRIMARY KEY (`code_Hash`),
  KEY `market_link_code_account_idx` (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: one-time /vault link codes';

CREATE TABLE IF NOT EXISTS `market_plugin_token` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `token_Hash` varbinary(64) NOT NULL,
  `account_Id` int unsigned NOT NULL,
  `label` varchar(64) DEFAULT NULL,
  `created_Time` datetime(6) NOT NULL,
  `last_Used_Time` datetime(6) DEFAULT NULL,
  `expires_Time` datetime(6) NOT NULL,
  `revoked_Time` datetime(6) DEFAULT NULL,
  `password_Fingerprint` varbinary(64) NOT NULL COMMENT 'fingerprint of the account password hash at issue',
  PRIMARY KEY (`id`),
  UNIQUE KEY `market_plugin_token_hash_uidx` (`token_Hash`),
  KEY `market_plugin_token_account_idx` (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: UtilityBelt plugin tokens';

CREATE TABLE IF NOT EXISTS `market_blocked_wcid` (
  `wcid` int unsigned NOT NULL,
  `reason` varchar(255) NOT NULL,
  `added_By_Account_Id` int unsigned NOT NULL,
  `added_Time` datetime(6) NOT NULL,
  PRIMARY KEY (`wcid`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: item types the Vault refuses';

-- The ledger is append-only, even for hand-written SQL
CREATE TRIGGER IF NOT EXISTS `market_transfer_no_update` BEFORE UPDATE ON `market_transfer`
  FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'market_transfer is append-only: add a reversal or admin_adjust transfer instead';

CREATE TRIGGER IF NOT EXISTS `market_transfer_no_delete` BEFORE DELETE ON `market_transfer`
  FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'market_transfer is append-only: add a reversal or admin_adjust transfer instead';

CREATE TRIGGER IF NOT EXISTS `market_ledger_entry_no_update` BEFORE UPDATE ON `market_ledger_entry`
  FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'market_ledger_entry is append-only: add a reversal or admin_adjust transfer instead';

CREATE TRIGGER IF NOT EXISTS `market_ledger_entry_no_delete` BEFORE DELETE ON `market_ledger_entry`
  FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'market_ledger_entry is append-only: add a reversal or admin_adjust transfer instead';
