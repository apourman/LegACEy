-- Web sessions for the BFF: the Market API issues an opaque token at sign-in and stores only its SHA-256 hash.
-- A session ends when it is revoked (sign-out, or a request that sees a ban), when it has been idle too long, at its absolute expiry,
-- or when the account's password hash no longer matches the fingerprint taken at sign-in. Plugin tokens (market_plugin_token) are separate.
-- Idempotent: the local bootstrap applies it before the game's update runner, which may apply it again.
CREATE TABLE IF NOT EXISTS `market_web_session` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `token_Hash` varbinary(64) NOT NULL,
  `account_Id` int unsigned NOT NULL,
  `created_Time` datetime(6) NOT NULL,
  `last_Used_Time` datetime(6) NOT NULL,
  `idle_Expires_Time` datetime(6) NOT NULL COMMENT 'slides with use, never past absolute_Expires_Time',
  `absolute_Expires_Time` datetime(6) NOT NULL,
  `password_Fingerprint` varbinary(64) NOT NULL COMMENT 'fingerprint of the account password hash at sign-in',
  `revoked_Time` datetime(6) DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `market_web_session_hash_uidx` (`token_Hash`),
  KEY `market_web_session_account_idx` (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Market: web sessions issued to the BFF';
