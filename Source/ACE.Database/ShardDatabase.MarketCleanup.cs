using System;

using ACE.Common.Extensions;
using ACE.Database.Market;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// The market's housekeeping job. Run it on the serialized shard save queue (SerializedShardDatabase), the game's database thread.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Deletes market rows nothing reads any more (see MarketCleanup.Run). Null if the delete failed (logged).
        /// </summary>
        public MarketCleanupReport DeleteExpiredMarketRows()
        {
            try
            {
                using (var context = new ShardDbContext())
                    return MarketCleanup.Run(context, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][MARKET] Deleting expired market rows failed: {ex.GetFullMessage()}");
                return null;
            }
        }
    }
}
