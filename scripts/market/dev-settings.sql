-- Development shard settings: opens the marketplace (market_enabled, off by default on a server) so the local and end-to-end stacks
-- can use trade notes, the game bridge and plugin sign-in. Piped into development shard databases by scripts/market/bootstrap.sh
-- and scripts/market/fresh-databases.sh. INSERT IGNORE keeps a value a developer has since changed. Never an update script, never run on a server.
INSERT IGNORE INTO `config_properties_boolean` (`key`, `value`, `description`) VALUES ('market_enabled', 1, 'opened for local development by scripts/market/dev-settings.sql');
