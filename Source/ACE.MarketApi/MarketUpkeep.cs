using System;
using System.Collections.Generic;

using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// Listing upkeep that runs when a request is about to read or change listings, so nothing depends on a timer:
    /// listings past their lifetime expire back to the Vault, and a ban is noticed (the account's listings returned, its web sessions ended).
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
        /// Expires overdue listings, then returns the listings and ends the web sessions of every banned account. Returns the banned accounts,
        /// whose listings must stay hidden even if returning them lost a race this time.
        /// </summary>
        public static HashSet<uint> ExpireAndHandleBans(MarketDatabase database, DateTime now)
        {
            Expire(database, now);

            var banned = database.BannedAccountIds(now);

            ReturnListingsAndEndWebSessions(database, banned, now);

            return banned;
        }

        /// <summary>
        /// What the market does when it sees accounts banned: their active listings go back to their Vaults (ban_returned), and every web session
        /// they have is revoked, for good (it stays refused after the ban ends).
        /// </summary>
        public static void ReturnListingsAndEndWebSessions(MarketDatabase database, IReadOnlyCollection<uint> accountIds, DateTime now)
        {
            if (accountIds.Count == 0)
                return;

            using var shard = database.CreateShard();
            WebSessions.RevokeAll(shard, accountIds, now);
            ListingStore.ReturnListings(shard, accountIds, now);
        }
    }
}
