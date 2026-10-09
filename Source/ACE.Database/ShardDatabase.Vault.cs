using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Market;
using ACE.Database.Models.Auth;
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
        /// Loads a biota through a new context, never from or into the cache, so it shows what the database holds now (for example after a shard SQL update).
        /// Null if there is no such row.
        /// </summary>
        public Biota GetBiotaUncached(uint id)
        {
            using (var context = new ShardDbContext())
                return GetBiotaFromDatabase(context, id);
        }

        /// <summary>
        /// Saves an item the world thread has taken out of a pack (container cleared, cast-on enchantments removed) into the Vault:
        /// evicts the item from the cache, loads a fresh copy, applies the in-memory changes, inserts the Vault row and a deposit item event, and saves once.
        /// Refuses, saving nothing, if the item still has a container, wielder or location, or if the account's Vault already holds maxItems
        /// (checked in the job, which is serialized with every other Vault job). Failed means nothing was saved; Unknown means the save may have committed.
        /// The job sets vaultItem.DepositedTime; after a refusal or failure, pass a new VaultItem to try again.
        /// </summary>
        public MarketJobResult DepositToVault(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, VaultItem vaultItem, int maxItems = int.MaxValue, TicketCompletion ticket = null)
        {
            if (vaultItem.ItemGuid != biota.Id || vaultItem.State != VaultItemState.Held)
            {
                log.Warn($"[DATABASE][VAULT] DepositToVault 0x{biota.Id:X8} refused: Vault row for 0x{vaultItem.ItemGuid:X8} in state {vaultItem.State}");
                return MarketJobResult.Refused;
            }

            rwLock.EnterReadLock();
            try
            {
                if (IsInWorld(biota))
                {
                    log.Warn($"[DATABASE][VAULT] DepositToVault 0x{biota.Id:X8} refused: the item still has a container, wielder or location");
                    return MarketJobResult.Refused;
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }

            return SaveVaultJob(nameof(DepositToVault), biota, rwLock, ItemEventKind.Deposit, context =>
            {
                if (context.MarketVaultItems.Count(r => r.AccountId == vaultItem.AccountId) >= maxItems)
                {
                    log.Warn($"[DATABASE][VAULT] DepositToVault 0x{biota.Id:X8} refused: the Vault of account {vaultItem.AccountId} already holds {maxItems} items");
                    return MarketJobResult.Refused;
                }

                vaultItem.DepositedTime = DateTime.UtcNow;

                if (ticket != null)
                    TicketStore.Complete(context, ticket, vaultItem.DepositedTime);

                context.MarketVaultItems.Add(vaultItem);
                context.MarketItemEvents.Add(NewItemEvent(biota.Id, vaultItem.AccountId, vaultItem.CharacterId, ItemEventKind.Deposit, vaultItem.DepositedTime));

                return MarketJobResult.Saved;
            });
        }

        /// <summary>
        /// Saves an item the world thread has pointed back at a character's pack out of the Vault:
        /// evicts the item from the cache, loads a fresh copy, applies the in-memory changes, deletes the Vault row, writes a withdraw item event, and saves once.
        /// Refuses, saving nothing, if the item has no container, if its Vault row is missing, belongs to another account, is listed,
        /// or no longer has expectedRowVersion (someone changed it since the caller read it). Banned, saving nothing, if the account is banned just before the save.
        /// Failed means nothing was saved; Unknown means the save may have committed.
        /// A game bridge ticket passed as ticket is marked done in the same save, so the item can't move without its ticket finishing.
        /// </summary>
        public MarketJobResult WithdrawFromVault(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, uint accountId, uint characterId, uint expectedRowVersion, TicketCompletion ticket = null)
        {
            return WithdrawManyFromVault(new[] { (biota, rwLock, expectedRowVersion) }, accountId, characterId, ticket);
        }

        /// <summary>
        /// Saves items the world thread has pointed back at a character's pack out of the Vault, as one save: every Vault row is removed, every item is saved
        /// and every withdraw item event written, or none of it. Refuses, saving nothing, if an item has no container, if an item's Vault row is missing,
        /// belongs to another account, is listed, or no longer has its expected row version. Banned, saving nothing, if the account is banned just before the save.
        /// Failed means nothing was saved; Unknown means the save may have committed.
        /// A game bridge ticket passed as ticket is marked done in the same save, so the item can't move without its ticket finishing.
        /// </summary>
        public MarketJobResult WithdrawManyFromVault(IReadOnlyList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, uint expectedRowVersion)> items, uint accountId, uint characterId, TicketCompletion ticket = null)
        {
            foreach (var (biota, rwLock, _) in items)
            {
                rwLock.EnterReadLock();
                try
                {
                    if (biota.PropertiesIID == null || !biota.PropertiesIID.ContainsKey(PropertyInstanceId.Container))
                    {
                        log.Warn($"[DATABASE][VAULT] WithdrawFromVault 0x{biota.Id:X8} refused: the item has no container");
                        return MarketJobResult.Refused;
                    }
                }
                finally
                {
                    rwLock.ExitReadLock();
                }
            }

            return SaveVaultJob(nameof(WithdrawFromVault), items.Select(item => (item.biota, item.rwLock)).ToList(), ItemEventKind.Withdraw, context =>
            {
                // every row is checked before any is removed, so one refusal refuses the set
                var vaultItems = new List<VaultItem>(items.Count);

                foreach (var (biota, _, expectedRowVersion) in items)
                {
                    var vaultItem = context.MarketVaultItems.FirstOrDefault(r => r.ItemGuid == biota.Id);

                    if (vaultItem == null || vaultItem.AccountId != accountId || vaultItem.State == VaultItemState.Listed || vaultItem.RowVersion != expectedRowVersion)
                    {
                        log.Warn($"[DATABASE][VAULT] WithdrawFromVault 0x{biota.Id:X8} refused for account {accountId}: Vault row {(vaultItem == null ? "missing" : $"account {vaultItem.AccountId}, state {vaultItem.State}, version {vaultItem.RowVersion}, expected {expectedRowVersion}")}");
                        return MarketJobResult.Refused;
                    }

                    vaultItems.Add(vaultItem);
                }

                // a ban freezes the Vault. It may have landed after the channel started or the ticket was written, so it is read here, just before the save.
                if (IsAccountBanned(accountId))
                {
                    log.Warn($"[DATABASE][VAULT] WithdrawFromVault 0x{items[0].biota.Id:X8} refused: account {accountId} is banned");
                    return MarketJobResult.Banned;
                }

                // the row version is a concurrency token: the delete only succeeds if nobody changed the row since it was read here
                foreach (var vaultItem in vaultItems)
                    context.MarketVaultItems.Remove(vaultItem);

                var now = DateTime.UtcNow;

                if (ticket != null)
                    TicketStore.Complete(context, ticket, now);

                foreach (var (biota, _, _) in items)
                    context.MarketItemEvents.Add(NewItemEvent(biota.Id, accountId, characterId, ItemEventKind.Withdraw, now));

                return MarketJobResult.Saved;
            });
        }

        /// <summary>
        /// The body both jobs share: evict, load a fresh copy through a new context (never the cache, which another thread could have refilled),
        /// apply the in-memory item, let vaultChange change the Vault row and add the item event, and save once.
        /// vaultChange returns anything but Saved to refuse, and then nothing is saved.
        /// No exception escapes, so the caller's callback always runs. A failure before SaveChanges saved nothing. A failure in SaveChanges may still have
        /// committed (the commit's acknowledgement can be lost, and a retry then fails on the rows the first try wrote), so the database is asked whether
        /// the job's item event is there: the event is written in the same save as everything else.
        /// </summary>
        private MarketJobResult SaveVaultJob(string job, ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, string eventKind, Func<ShardDbContext, MarketJobResult> vaultChange)
        {
            return SaveVaultJob(job, new[] { (biota, rwLock) }, eventKind, vaultChange);
        }

        /// <summary>
        /// The body the jobs share, for one item or a set saved together: evict, load fresh copies through a new context, let vaultChange change the Vault rows
        /// and add the item events, apply the in-memory items, and save once. One SaveChanges is one transaction, so a set is saved entirely or not at all.
        /// </summary>
        private MarketJobResult SaveVaultJob(string job, IReadOnlyList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> items, string eventKind, Func<ShardDbContext, MarketJobResult> vaultChange)
        {
            // the job's own events are the only ones of their kind for these items written at or after this time
            var started = ListingStore.Truncate(DateTime.UtcNow);
            var ids = items.Select(item => item.biota.Id).ToList();
            var subject = items.Count == 1 ? $"0x{ids[0]:X8}" : $"{items.Count} items";
            var saving = false;

            try
            {
                foreach (var id in ids)
                    EvictBiota(id);

                using (var context = new ShardDbContext())
                {
                    var existingBiotas = new List<ACE.Database.Models.Shard.Biota>(items.Count);

                    foreach (var (biota, _) in items)
                    {
                        var existingBiota = GetBiotaFromDatabase(context, biota.Id);

                        if (existingBiota == null)
                        {
                            log.Warn($"[DATABASE][VAULT] {job} 0x{biota.Id:X8} refused: the item is not in the database");
                            return MarketJobResult.Refused;
                        }

                        existingBiotas.Add(existingBiota);
                    }

                    var change = vaultChange(context);

                    if (change != MarketJobResult.Saved)
                        return change;

                    for (var index = 0; index < items.Count; index++)
                    {
                        var (biota, rwLock) = items[index];

                        rwLock.EnterReadLock();
                        try
                        {
                            ACE.Database.Adapter.BiotaUpdater.UpdateDatabaseBiota(context, biota, existingBiotas[index]);
                        }
                        finally
                        {
                            rwLock.ExitReadLock();
                        }

                        SetBiotaPopulatedCollections(existingBiotas[index]);
                    }

                    // Unlike DoSaveBiota there's no second attempt: the context's retry-on-failure strategy already retries transient errors,
                    // and anything else (a constraint, a concurrency conflict) would fail the same way again.
                    saving = true;
                    context.SaveChanges();

                    return MarketJobResult.Saved;
                }
            }
            catch (Exception ex) when (!saving)
            {
                log.Error($"[DATABASE][VAULT] {job} {subject} failed before saving, nothing was saved: {ex.GetFullMessage()}");
                return MarketJobResult.Failed;
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][VAULT] {job} {subject} failed while saving: {ex.GetFullMessage()}");

                return Reconcile(job, subject, context => context.MarketItemEvents.Any(e => ids.Contains(e.ItemGuid) && e.Kind == eventKind && e.EventTime >= started));
            }
        }

        /// <summary>
        /// After a save threw: Saved if the database shows the job's changes, Failed if it doesn't, Unknown if it can't be read
        /// </summary>
        private static MarketJobResult Reconcile(string job, string subject, Func<ShardDbContext, bool> committed)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    if (committed(context))
                    {
                        log.Warn($"[DATABASE][VAULT] {job} {subject} committed although its save reported a failure; treating it as saved");
                        return MarketJobResult.Saved;
                    }

                    log.Warn($"[DATABASE][VAULT] {job} {subject}: the database shows nothing of the failed save");
                    return MarketJobResult.Failed;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][VAULT] {job} {subject}: could not tell whether the failed save committed: {ex.GetFullMessage()}");
                return MarketJobResult.Unknown;
            }
        }

        /// <summary>
        /// True if the account is banned now (the game login's rule). Read from the auth database just before a save that moves value out of the market.
        /// An account the auth database doesn't have isn't banned: the game only runs these for a character that is logged in.
        /// </summary>
        private static bool IsAccountBanned(uint accountId)
        {
            using (var auth = new AuthDbContext())
            {
                var account = auth.Account.AsNoTracking().FirstOrDefault(a => a.AccountId == accountId);

                return account != null && account.IsBanned(DateTime.UtcNow);
            }
        }

        private static ItemEvent NewItemEvent(uint itemGuid, uint accountId, uint characterId, string kind, DateTime time)
        {
            return new ItemEvent
            {
                ItemGuid = itemGuid,
                AccountId = accountId,
                CharacterId = characterId,
                Kind = kind,
                EventTime = time,
            };
        }

        private static bool IsInWorld(ACE.Entity.Models.Biota biota)
        {
            if (biota.PropertiesIID != null && (biota.PropertiesIID.ContainsKey(PropertyInstanceId.Container) || biota.PropertiesIID.ContainsKey(PropertyInstanceId.Wielder)))
                return true;

            return biota.PropertiesPosition != null && biota.PropertiesPosition.ContainsKey(PositionType.Location);
        }
    }
}
