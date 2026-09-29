using System;
using System.Collections.Generic;

using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// Listing upkeep that runs when a request is about to read or change listings, so nothing depends on a timer:
    /// listings past their lifetime expire back to the Vault, and a ban is noticed and the account's listings returned.
    /// </summary>
    public static class MarketUpkeep
    {
        /// <summary>
        /// Expires overdue listings
        /// </summary>
        public static void Expire(MarketDatabase database, DateTime now)
        {
            using var shard = database.CreateShard();
            ListingStore.ExpireOverdue(shard, now);
        }

        /// <summary>
        /// Expires overdue listings, then returns the listings of every banned account. Returns the banned accounts, whose listings must stay hidden
        /// even if returning them lost a race this time.
        /// </summary>
        public static HashSet<uint> ExpireAndReturnBanned(MarketDatabase database, DateTime now)
        {
            Expire(database, now);

            var banned = database.BannedAccountIds(now);

            ReturnListings(database, banned, now);

            return banned;
        }

        /// <summary>
        /// Returns the accounts' active listings to their Vaults (ban_returned)
        /// </summary>
        public static void ReturnListings(MarketDatabase database, IReadOnlyCollection<uint> accountIds, DateTime now)
        {
            if (accountIds.Count == 0)
                return;

            using var shard = database.CreateShard();
            ListingStore.ReturnListings(shard, accountIds, now);
        }
    }
}
