-- Add the information the website needs to present the current stage of a bridge ticket.
-- The columns are nullable so existing and finished tickets remain valid. The local bootstrap can apply this before the game update runner;
-- prepared statements let a later game startup safely apply the same update again, including when only part of the schema is present.
SET @market_ticket_progress_sql = IF(
  (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_ticket' AND COLUMN_NAME = 'progress') = 0,
  'ALTER TABLE `market_ticket` ADD COLUMN `progress` varchar(32) DEFAULT NULL AFTER `claimed_Time`',
  'SELECT 1');
PREPARE market_ticket_progress_statement FROM @market_ticket_progress_sql;
EXECUTE market_ticket_progress_statement;
DEALLOCATE PREPARE market_ticket_progress_statement;

SET @market_ticket_progress_sql = IF(
  (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_ticket' AND COLUMN_NAME = 'progress_Time') = 0,
  'ALTER TABLE `market_ticket` ADD COLUMN `progress_Time` datetime(6) DEFAULT NULL AFTER `progress`',
  'SELECT 1');
PREPARE market_ticket_progress_statement FROM @market_ticket_progress_sql;
EXECUTE market_ticket_progress_statement;
DEALLOCATE PREPARE market_ticket_progress_statement;

SET @market_ticket_progress_sql = IF(
  (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_ticket' AND COLUMN_NAME = 'progress_Until') = 0,
  'ALTER TABLE `market_ticket` ADD COLUMN `progress_Until` datetime(6) DEFAULT NULL AFTER `progress_Time`',
  'SELECT 1');
PREPARE market_ticket_progress_statement FROM @market_ticket_progress_sql;
EXECUTE market_ticket_progress_statement;
DEALLOCATE PREPARE market_ticket_progress_statement;

SET @market_ticket_progress_sql = IF(
  (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_ticket' AND COLUMN_NAME = 'result') = 0,
  'ALTER TABLE `market_ticket` ADD COLUMN `result` json DEFAULT NULL AFTER `result_Message`',
  'SELECT 1');
PREPARE market_ticket_progress_statement FROM @market_ticket_progress_sql;
EXECUTE market_ticket_progress_statement;
DEALLOCATE PREPARE market_ticket_progress_statement;
