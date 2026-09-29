using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    /// <summary>
    /// Reads of the Vault tables through the configured shard database. Writes go through the deposit and withdraw save-queue jobs.
    /// </summary>
    public static class VaultStore
    {
        /// <summary>
        /// Every item in the account's Vault, oldest deposit first
        /// </summary>
        public static List<VaultItem> List(uint accountId)
        {
            using (var context = new ShardDbContext())
                return context.MarketVaultItems.AsNoTracking().Where(r => r.AccountId == accountId).OrderBy(r => r.DepositedTime).ThenBy(r => r.ItemGuid).ToList();
        }

        /// <summary>
        /// The number of items in the account's Vault (a stack counts as one)
        /// </summary>
        public static int Count(uint accountId)
        {
            using (var context = new ShardDbContext())
                return context.MarketVaultItems.Count(r => r.AccountId == accountId);
        }

        /// <summary>
        /// The Vault row of an item, or null if it isn't in a Vault
        /// </summary>
        public static VaultItem Get(uint itemGuid)
        {
            using (var context = new ShardDbContext())
                return context.MarketVaultItems.AsNoTracking().FirstOrDefault(r => r.ItemGuid == itemGuid);
        }

        /// <summary>
        /// True if an admin has blocked the weenie class id from the Vault
        /// </summary>
        public static bool IsWcidBlocked(uint wcid)
        {
            using (var context = new ShardDbContext())
                return context.MarketBlockedWcids.Any(r => r.Wcid == wcid);
        }
    }
}
