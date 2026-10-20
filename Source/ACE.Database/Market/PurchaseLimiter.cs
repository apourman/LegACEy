using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    /// <summary>
    /// Purchase attempts per account in the last minute, counted in the shard database (market_purchase_attempt), so the Market API and the game server
    /// share one limit. An attempt the limit refuses isn't counted.
    /// </summary>
    public static class PurchaseLimiter
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Counts an attempt, or returns false if the account already made perMinute attempts within the last minute.
        /// The account's attempts from before the last minute are deleted.
        /// </summary>
        public static bool TryAcquire(ShardDbContext shard, uint accountId, DateTime now, long perMinute)
        {
            var windowStart = now - Window;

            shard.MarketPurchaseAttempts.Where(a => a.AccountId == accountId && a.CreatedTime <= windowStart).ExecuteDelete();

            // ponytail: count then insert, so attempts racing from both front doors can each pass at the limit and overshoot it by the number racing
            if (shard.MarketPurchaseAttempts.Count(a => a.AccountId == accountId) >= perMinute)
                return false;

            shard.MarketPurchaseAttempts.Add(new PurchaseAttempt { AccountId = accountId, CreatedTime = now });
            shard.SaveChanges();

            return true;
        }
    }
}
