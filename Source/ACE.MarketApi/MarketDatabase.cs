using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using MySqlConnector;

using ACE.Common;
using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Database.Models.Shard;

namespace ACE.MarketApi
{
    /// <summary>
    /// Plain auth and shard contexts for the API, built from ACE's MySQL configuration.
    /// Never the game's caching or serialized shard classes: those belong to the game process.
    /// </summary>
    public sealed class MarketDatabase
    {
        private readonly DbContextOptions<AuthDbContext> authOptions;
        private readonly DbContextOptions<ShardDbContext> shardOptions;

        /// <param name="username">replaces the configured MySQL user when set</param>
        /// <param name="password">replaces the configured MySQL password when set</param>
        public MarketDatabase(DatabaseConfiguration config, string authDatabase = null, string shardDatabase = null, string username = null, string password = null)
        {
            authOptions = Options<AuthDbContext>(config.Authentication, authDatabase, username, password);
            shardOptions = Options<ShardDbContext>(config.Shard, shardDatabase, username, password);
        }

        public AuthDbContext CreateAuth() => new AuthDbContext(authOptions);

        public ShardDbContext CreateShard() => new ShardDbContext(shardOptions);

        /// <summary>
        /// The account, read without tracking, or null
        /// </summary>
        public async Task<Account> FindAccountAsync(uint accountId)
        {
            using var auth = CreateAuth();
            return await auth.Account.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == accountId);
        }

        /// <summary>
        /// The account by name (the column is case-insensitive), read without tracking, or null
        /// </summary>
        public async Task<Account> FindAccountAsync(string accountName)
        {
            using var auth = CreateAuth();
            return await auth.Account.AsNoTracking().FirstOrDefaultAsync(a => a.AccountName == accountName);
        }

        /// <summary>
        /// True if the account is banned now, or no longer exists. Read just before a save; bans are never read inside one.
        /// </summary>
        public bool IsBanned(uint accountId, DateTime utcNow)
        {
            using var auth = CreateAuth();
            var account = auth.Account.AsNoTracking().FirstOrDefault(a => a.AccountId == accountId);

            return account == null || account.IsBanned(utcNow);
        }

        /// <summary>
        /// Every account banned now (the game login's rule: the ban's expiry is still ahead)
        /// </summary>
        public HashSet<uint> BannedAccountIds(DateTime utcNow)
        {
            using var auth = CreateAuth();

            return auth.Account.AsNoTracking().Where(a => a.BanExpireTime > utcNow).Select(a => a.AccountId).ToHashSet();
        }

        /// <summary>
        /// Configured like the generated contexts' OnConfiguring, retry on failure included
        /// </summary>
        private static DbContextOptions<T> Options<T>(MySqlConfiguration config, string databaseOverride, string usernameOverride, string passwordOverride) where T : DbContext
        {
            var database = string.IsNullOrWhiteSpace(databaseOverride) ? config.Database : databaseOverride;

            // built, not concatenated, so a password from docker.env may contain ';' or quotes
            var connectionString = new MySqlConnectionStringBuilder(config.ConnectionOptions)
            {
                Server = config.Host,
                Port = config.Port,
                UserID = string.IsNullOrEmpty(usernameOverride) ? config.Username : usernameOverride,
                Password = string.IsNullOrEmpty(passwordOverride) ? config.Password : passwordOverride,
                Database = database,
            }.ConnectionString;

            var builder = new DbContextOptionsBuilder<T>()
                .UseMySql(connectionString, DatabaseManager.CachedServerVersionAutoDetect(database, connectionString), mysql =>
                {
                    mysql.EnableRetryOnFailure(10);
                });

            if (config.EnableDetailedErrors)
                builder.EnableDetailedErrors();

            return builder.Options;
        }
    }
}
