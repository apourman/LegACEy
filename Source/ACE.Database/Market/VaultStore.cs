using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    /// <summary>
    /// Reads of the Vault tables, through the configured shard database or a context the caller gives. Item moves go through the deposit and withdraw save-queue jobs;
    /// the only writes here never touch an item: the withdrawal channel's marks on a Vault row, the admin's search-column refresh, and the WCID blocklist.
    /// </summary>
    public static class VaultStore
    {
        /// <summary>
        /// The longest reason a WCID block can record (the column's size)
        /// </summary>
        public const int MaxBlockReasonLength = 255;

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
        /// The GUIDs of every item in any Vault, or only of items of one weenie class
        /// </summary>
        public static List<uint> ItemGuids(ShardDbContext context, uint? wcid = null)
        {
            var rows = context.MarketVaultItems.AsNoTracking();

            if (wcid != null)
                rows = rows.Where(r => r.Wcid == wcid.Value);

            return rows.OrderBy(r => r.ItemGuid).Select(r => r.ItemGuid).ToList();
        }

        /// <summary>
        /// Overwrites a Vault row's search columns (wcid, name, type, stack size, value, icon layers, stats) with the ones given, in one UPDATE.
        /// The owner, state, deposit time and row version are left alone: the columns only describe the item, so a refresh never makes a listing,
        /// purchase or withdrawal in flight fail its row-version check. False if the item has no Vault row.
        /// </summary>
        public static bool UpdateSearchColumns(ShardDbContext context, VaultItem columns)
        {
            var changed = context.MarketVaultItems
                .Where(r => r.ItemGuid == columns.ItemGuid)
                .ExecuteUpdate(s => s
                    .SetProperty(r => r.Wcid, columns.Wcid)
                    .SetProperty(r => r.Name, columns.Name)
                    .SetProperty(r => r.ItemType, columns.ItemType)
                    .SetProperty(r => r.StackSize, columns.StackSize)
                    .SetProperty(r => r.Value, columns.Value)
                    .SetProperty(r => r.IconUnderlay, columns.IconUnderlay)
                    .SetProperty(r => r.Icon, columns.Icon)
                    .SetProperty(r => r.IconOverlay, columns.IconOverlay)
                    .SetProperty(r => r.IconOverlaySecondary, columns.IconOverlaySecondary)
                    .SetProperty(r => r.UiEffects, columns.UiEffects)
                    .SetProperty(r => r.PaletteTemplate, columns.PaletteTemplate)
                    .SetProperty(r => r.ClothingBase, columns.ClothingBase)
                    .SetProperty(r => r.Workmanship, columns.Workmanship)
                    .SetProperty(r => r.ArcaneLore, columns.ArcaneLore)
                    .SetProperty(r => r.WieldRequirements, columns.WieldRequirements)
                    .SetProperty(r => r.WieldSkillType, columns.WieldSkillType)
                    .SetProperty(r => r.WieldDifficulty, columns.WieldDifficulty)
                    .SetProperty(r => r.ArmorLevel, columns.ArmorLevel)
                    .SetProperty(r => r.Damage, columns.Damage)
                    .SetProperty(r => r.DamageMod, columns.DamageMod)
                    .SetProperty(r => r.MaterialType, columns.MaterialType)
                    .SetProperty(r => r.EquipmentSetId, columns.EquipmentSetId)
                    .SetProperty(r => r.ImbuedEffect, columns.ImbuedEffect));

            return changed == 1;
        }

        /// <summary>
        /// Every blocked weenie class, lowest id first
        /// </summary>
        public static List<BlockedWcid> ListBlocked(ShardDbContext context)
        {
            return context.MarketBlockedWcids.AsNoTracking().OrderBy(r => r.Wcid).ToList();
        }

        /// <summary>
        /// Blocks the weenie class from new deposits, recording the reason, the admin and the time.
        /// Returns the block now in force: the new one, or the earlier one if the class was already blocked (that one is kept unchanged).
        /// </summary>
        public static BlockedWcid Block(ShardDbContext context, uint wcid, string reason, uint adminAccountId, DateTime now, out bool added)
        {
            added = false;

            var existing = context.MarketBlockedWcids.AsNoTracking().FirstOrDefault(r => r.Wcid == wcid);

            if (existing != null)
                return existing;

            var block = new BlockedWcid { Wcid = wcid, Reason = reason, AddedByAccountId = adminAccountId, AddedTime = now };

            context.MarketBlockedWcids.Add(block);

            try
            {
                context.SaveChanges();
            }
            catch (DbUpdateException ex) when (Ledger.IsLostRace(ex))
            {
                // another admin blocked it at the same moment
                context.ChangeTracker.Clear();
                return context.MarketBlockedWcids.AsNoTracking().First(r => r.Wcid == wcid);
            }

            added = true;
            return block;
        }

        /// <summary>
        /// Lets the weenie class into the Vault again. False if it wasn't blocked.
        /// </summary>
        public static bool Unblock(ShardDbContext context, uint wcid)
        {
            return context.MarketBlockedWcids.Where(r => r.Wcid == wcid).ExecuteDelete() > 0;
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
