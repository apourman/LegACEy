-- Purchase attempts per account, for the purchases-per-minute limit that the Market API and the game server share. Only the last minute is read;
-- each attempt deletes its account's older rows.
-- Idempotent: the local bootstrap applies it before the game's update runner, which may apply it again.
CREATE TABLE IF NOT EXISTS `market_purchase_attempt` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `account_Id` int unsigned NOT NULL,
  `created_Time` datetime(6) NOT NULL,
  PRIMARY KEY (`id`),
  KEY `market_purchase_attempt_account_idx` (`account_Id`,`created_Time`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: purchase attempts in the last minute, for the rate limit';
