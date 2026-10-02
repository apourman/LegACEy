-- Add the information the website needs to present the current stage of a bridge ticket.
-- The columns are nullable so existing and finished tickets remain valid.
ALTER TABLE `market_ticket`
  ADD COLUMN `progress` varchar(32) DEFAULT NULL AFTER `claimed_Time`,
  ADD COLUMN `progress_Time` datetime(6) DEFAULT NULL AFTER `progress`,
  ADD COLUMN `progress_Until` datetime(6) DEFAULT NULL AFTER `progress_Time`,
  ADD COLUMN `result` json DEFAULT NULL AFTER `result_Message`;
