-- The order of an account Vault, as the player arranges it in the LegACEy client. NULL (every existing row, and new deposits) sorts after
-- the arranged items, oldest deposit first. It runs only when the table exists and the column does not, so it is safe to apply again
-- and before the market schema exists.
SET @market_vault_position_sql = IF(
  (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_vault_item') = 1
    AND (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_vault_item' AND COLUMN_NAME = 'position') = 0,
  'ALTER TABLE `market_vault_item` ADD COLUMN `position` int DEFAULT NULL AFTER `row_Version`',
  'SELECT 1');
PREPARE market_vault_position_statement FROM @market_vault_position_sql;
EXECUTE market_vault_position_statement;
DEALLOCATE PREPARE market_vault_position_statement;
