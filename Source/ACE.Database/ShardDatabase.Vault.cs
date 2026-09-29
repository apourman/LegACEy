using System;
using System.Linq;
using System.Threading;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Database
{
    /// <summary>
    /// Escrow custody: the jobs that move an item between a pack and the Vault.
    /// Each is one SaveChanges (no explicit transaction), so the item change and the Vault change are saved together or not at all.
    /// Run them on the serialized shard save queue (SerializedShardDatabase), which orders them after any earlier save of the item.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Forgets any cached copy of the biota so the next load reads the database. There is no cache here.
        /// </summary>
        public virtual bool EvictBiota(uint id)
        {
            return false;
        }

        /// <summary>
        /// Saves an item the world thread has taken out of a pack (container cleared, cast-on enchantments removed) into the Vault:
        /// evicts the item from the cache, loads a fresh copy, applies the in-memory changes, inserts the Vault row and a deposit item event, and saves once.
        /// Returns false, having saved nothing, if the item still has a container, wielder or location, or if the save fails.
        /// </summary>
        public bool DepositToVault(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, VaultItem vaultItem)
        {
            if (vaultItem.ItemGuid != biota.Id || vaultItem.State != VaultItemState.Held)
            {
                log.Error($"[DATABASE][VAULT] DepositToVault 0x{biota.Id:X8} refused: Vault row for 0x{vaultItem.ItemGuid:X8} in state {vaultItem.State}");
                return false;
            }

            rwLock.EnterReadLock();
            try
            {
                if (IsInWorld(biota))
                {
                    log.Error($"[DATABASE][VAULT] DepositToVault 0x{biota.Id:X8} refused: the item still has a container, wielder or location");
                    return false;
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }

            EvictBiota(biota.Id);

            using (var context = new ShardDbContext())
            {
                var existingBiota = GetBiota(context, biota.Id, true);

                if (existingBiota == null)
                {
                    log.Error($"[DATABASE][VAULT] DepositToVault 0x{biota.Id:X8} refused: the item is not in the database");
                    return false;
                }

                rwLock.EnterReadLock();
                try
                {
                    ACE.Database.Adapter.BiotaUpdater.UpdateDatabaseBiota(context, biota, existingBiota);
                }
                finally
                {
                    rwLock.ExitReadLock();
                }

                SetBiotaPopulatedCollections(existingBiota);

                var now = DateTime.UtcNow;

                vaultItem.DepositedTime = now;

                context.MarketVaultItems.Add(vaultItem);
                context.MarketItemEvents.Add(new ItemEvent
                {
                    ItemGuid = biota.Id,
                    AccountId = vaultItem.AccountId,
                    CharacterId = vaultItem.CharacterId,
                    Kind = ItemEventKind.Deposit,
                    EventTime = now,
                });

                return TrySaveVaultChange(context, nameof(DepositToVault), biota.Id);
            }
        }

        /// <summary>
        /// Saves an item the world thread has pointed back at a character's pack out of the Vault:
        /// evicts the item from the cache, loads a fresh copy, applies the in-memory changes, deletes the Vault row, writes a withdraw item event, and saves once.
        /// Returns false, having saved nothing, if the item has no container, if its Vault row is missing, belongs to another account, is listed,
        /// or no longer has expectedRowVersion (someone changed it since the caller read it), or if the save fails.
        /// </summary>
        public bool WithdrawFromVault(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, uint accountId, uint characterId, uint expectedRowVersion)
        {
            rwLock.EnterReadLock();
            try
            {
                if (biota.PropertiesIID == null || !biota.PropertiesIID.ContainsKey(PropertyInstanceId.Container))
                {
                    log.Error($"[DATABASE][VAULT] WithdrawFromVault 0x{biota.Id:X8} refused: the item has no container");
                    return false;
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }

            EvictBiota(biota.Id);

            using (var context = new ShardDbContext())
            {
                var vaultItem = context.MarketVaultItems.FirstOrDefault(r => r.ItemGuid == biota.Id);

                if (vaultItem == null || vaultItem.AccountId != accountId || vaultItem.State == VaultItemState.Listed || vaultItem.RowVersion != expectedRowVersion)
                {
                    log.Warn($"[DATABASE][VAULT] WithdrawFromVault 0x{biota.Id:X8} refused for account {accountId}: Vault row {(vaultItem == null ? "missing" : $"account {vaultItem.AccountId}, state {vaultItem.State}, version {vaultItem.RowVersion}, expected {expectedRowVersion}")}");
                    return false;
                }

                var existingBiota = GetBiota(context, biota.Id, true);

                if (existingBiota == null)
                {
                    log.Error($"[DATABASE][VAULT] WithdrawFromVault 0x{biota.Id:X8} refused: the item is not in the database");
                    return false;
                }

                rwLock.EnterReadLock();
                try
                {
                    ACE.Database.Adapter.BiotaUpdater.UpdateDatabaseBiota(context, biota, existingBiota);
                }
                finally
                {
                    rwLock.ExitReadLock();
                }

                SetBiotaPopulatedCollections(existingBiota);

                // the row version is a concurrency token: the delete only succeeds if nobody changed the row since it was read here
                context.MarketVaultItems.Remove(vaultItem);
                context.MarketItemEvents.Add(new ItemEvent
                {
                    ItemGuid = biota.Id,
                    AccountId = accountId,
                    CharacterId = characterId,
                    Kind = ItemEventKind.Withdraw,
                    EventTime = DateTime.UtcNow,
                });

                return TrySaveVaultChange(context, nameof(WithdrawFromVault), biota.Id);
            }
        }

        private static bool IsInWorld(ACE.Entity.Models.Biota biota)
        {
            if (biota.PropertiesIID != null && (biota.PropertiesIID.ContainsKey(PropertyInstanceId.Container) || biota.PropertiesIID.ContainsKey(PropertyInstanceId.Wielder)))
                return true;

            return biota.PropertiesPosition != null && biota.PropertiesPosition.ContainsKey(PositionType.Location);
        }

        private static bool TrySaveVaultChange(ShardDbContext context, string job, uint id)
        {
            try
            {
                context.SaveChanges();

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][VAULT] {job} 0x{id:X8} failed, nothing was saved: {ex.GetFullMessage()}");

                return false;
            }
        }
    }
}
