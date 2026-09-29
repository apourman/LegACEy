using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    /// <summary>
    /// Reads of the Vault tables through the configured shard database. Item moves go through the deposit and withdraw save-queue jobs;
    /// the only writes here are the withdrawal channel's marks on a Vault row, which never touch an item.
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

        /// <summary>
        /// Marks a held row as being withdrawn, if it still has the expected version and belongs to the account.
        /// Returns the row's new version, or null if the row was changed or is gone.
        /// </summary>
        public static uint? TryMarkWithdrawing(uint itemGuid, uint accountId, uint expectedRowVersion)
        {
            using (var context = new ShardDbContext())
            {
                var changed = context.MarketVaultItems
                    .Where(r => r.ItemGuid == itemGuid && r.AccountId == accountId && r.State == VaultItemState.Held && r.RowVersion == expectedRowVersion)
                    .ExecuteUpdate(s => s.SetProperty(r => r.State, VaultItemState.Withdrawing).SetProperty(r => r.RowVersion, r => r.RowVersion + 1));

                return changed == 1 ? expectedRowVersion + 1 : null;
            }
        }

        /// <summary>
        /// Puts a row marked for withdrawal back to held, if nothing else changed it since it was marked
        /// </summary>
        public static bool TryReleaseWithdrawing(uint itemGuid, uint markedRowVersion)
        {
            using (var context = new ShardDbContext())
            {
                var changed = context.MarketVaultItems
                    .Where(r => r.ItemGuid == itemGuid && r.State == VaultItemState.Withdrawing && r.RowVersion == markedRowVersion)
                    .ExecuteUpdate(s => s.SetProperty(r => r.State, VaultItemState.Held).SetProperty(r => r.RowVersion, r => r.RowVersion + 1));

                return changed == 1;
            }
        }

        /// <summary>
        /// Puts every row marked for withdrawal back to held. Only for startup: no withdrawal channel survives a restart.
        /// </summary>
        public static int ReleaseAllWithdrawing()
        {
            using (var context = new ShardDbContext())
            {
                return context.MarketVaultItems
                    .Where(r => r.State == VaultItemState.Withdrawing)
                    .ExecuteUpdate(s => s.SetProperty(r => r.State, VaultItemState.Held).SetProperty(r => r.RowVersion, r => r.RowVersion + 1));
            }
        }
    }
}
