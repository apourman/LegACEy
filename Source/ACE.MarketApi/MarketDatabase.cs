using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

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

        public MarketDatabase(DatabaseConfiguration config, string authDatabase = null, string shardDatabase = null)
        {
            authOptions = Options<AuthDbContext>(config.Authentication, authDatabase);
            shardOptions = Options<ShardDbContext>(config.Shard, shardDatabase);
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
        /// Configured like the generated contexts' OnConfiguring, retry on failure included
        /// </summary>
        private static DbContextOptions<T> Options<T>(MySqlConfiguration config, string databaseOverride) where T : DbContext
        {
            var database = string.IsNullOrWhiteSpace(databaseOverride) ? config.Database : databaseOverride;

            var connectionString = $"server={config.Host};port={config.Port};user={config.Username};password={config.Password};database={database};{config.ConnectionOptions}";

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
