-- The development marker the development guard (ACE.Database.Market.DevelopmentGuard) looks for. The one copy of it: embedded in ACE.Database
-- for the interactive "scripts/market/dev.sh mark", and piped by scripts/market/bootstrap.sh into the databases it creates.
-- Never an update script, never run on a server.
CREATE TABLE IF NOT EXISTS `legacey_dev_marker` (
  `id` tinyint unsigned NOT NULL,
  `purpose` varchar(32) NOT NULL,
  `marked_Time` datetime(6) NOT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='This database is a local development database: development tools may write it. Never create this on a server.';
INSERT IGNORE INTO `legacey_dev_marker` (`id`, `purpose`, `marked_Time`) VALUES (1, 'development', UTC_TIMESTAMP(6));
