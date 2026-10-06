using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// Whether purchases (and MMD withdrawals) are paused server-wide, as a failed ledger audit does
    /// </summary>
    public interface IMarketPause
    {
        bool IsPaused { get; }
    }

    /// <summary>
    /// The pause as the shard database holds it, so the API sees what the audit and the game's /market resume write, as soon as they write it
    /// </summary>
    public sealed class DatabaseMarketPause : IMarketPause
    {
        private readonly MarketDatabase database;

        public DatabaseMarketPause(MarketDatabase database)
        {
            this.database = database;
        }

        public bool IsPaused
        {
            get
            {
                using var shard = database.CreateShard();
                return MarketPause.IsPaused(shard);
            }
        }
    }
}
