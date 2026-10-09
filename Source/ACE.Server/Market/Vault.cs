using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Database;
using ACE.Database.Adapter;
using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Market
{
    /// <summary>
    /// The game side of the Vault: the one entry point the Vault chest's channel actions and the game bridge use to move an item between a pack and the account's Vault.
    /// Call Deposit and Withdraw on the world thread. The result is reported once through the callback:
    /// at once for a refusal, on the world thread after the save for a deposit or withdrawal.
    /// </summary>
    public static partial class Vault
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// False until Initialize finds every market table and trigger. Until then the Vault does nothing.
        /// </summary>
        public static bool Available { get; private set; }

        /// <summary>
        /// The market_enabled server setting, off by default. The Vault's items work either way; trade notes, the game bridge and plugin sign-in need it on.
        /// </summary>
        public static bool MarketEnabled => PropertyManager.GetBool(MarketEnabledKey).Item;

        public const string MarketEnabledKey = "market_enabled";

        /// <summary>
        /// Players with a deposit or withdrawal in flight. One at a time each, so that two queued withdrawals can't both pass the pack-space and unique checks
        /// before either has reached the pack. Only the world thread touches it.
        /// </summary>
        private static readonly HashSet<uint> inFlight = new HashSet<uint>();

        /// <summary>
        /// Startup: checks the market schema. The update runner marks even a failed script as applied, so this is the only guard against a half-installed market.
        /// </summary>
        public static void Initialize()
        {
            var check = MarketSchema.Check();

            Available = check.Status == MarketSchemaStatus.Ok;

            // the LegACEy client's Vault window; the actions answer "not available" while the market is disabled
            VaultChannelActions.Register();

            if (Available)
            {
                log.Info($"[VAULT] Market schema check: {check.Report}");

                // tickets this server was working on when it stopped; must run before the release below, which ends their channels' marks
                GameBridge.Recover();

                // a withdrawal channel marks its row withdrawing; none survives a restart, so any mark left is from a crash
                var released = VaultStore.ReleaseAllWithdrawing();

                if (released > 0)
                    log.Warn($"[VAULT] Released {released:N0} Vault item(s) left marked withdrawing by the last shutdown");
            }
            else
                log.Error($"[VAULT] Market schema check failed ({check.Report}). The Vault and the market are disabled until the market tables are installed and the server restarted.");
        }

        /// <summary>
        /// One page of the player's account Vault, with a search, and the counts around it (see VaultStore.Page)
        /// </summary>
        public static VaultPage Page(Player player, string search, int offset, int count)
        {
            return Available ? VaultStore.Page(player.Character.AccountId, search, offset, count) : new VaultPage(new List<VaultItem>(), 0, 0);
        }

        /// <summary>
        /// Moves an item from the player's pack into their account's Vault.
        /// The item is taken out of the pack in memory only. The networking removal saves an ownerless item, and destroying the item would delete its row.
        /// The deposit job then saves the item and the Vault row together. On success the object is forgotten; on a failure that saved nothing it goes back to the pack.
        /// </summary>
        public static void Deposit(Player player, uint itemGuid, Action<VaultResult> completed = null)
        {
            Deposit(player, itemGuid, completed, null);
        }

        /// <summary>
        /// Deposits an item for a game bridge ticket, completing the ticket with the item and Vault row in the same save.
        /// </summary>
        public static void Deposit(Player player, uint itemGuid, Action<VaultResult> completed, long? ticketId)
        {
            VaultOutcome? refusal;
            WorldObject item;

            try
            {
                refusal = CheckDeposit(player, itemGuid, out item);
            }
            catch (Exception ex)
            {
                // the checks read the database; nothing has moved yet
                log.Error($"[VAULT] Deposit of 0x{itemGuid:X8} for {player.Name} failed in its checks: {ex}");
                Finish(player, VaultOutcome.SaveFailed, null, itemGuid, completed);
                return;
            }

            if (refusal != null)
            {
                Finish(player, refusal.Value, item?.Name, itemGuid, completed);
                return;
            }

            var guid = item.Guid;
            var accountId = player.Character.AccountId;
            var vaultSize = (int)MarketSettings.Get(MarketSettings.VaultSize);
            var name = item.Name;
            var vaultItem = NewVaultItem(item, accountId, player.Guid.Full);

            // where the item is now, which the in-memory removal clears
            var containerId = item.ContainerId;
            var ownerId = item.OwnerId;
            var placement = item.PlacementPosition;

            // what the Vault saves: the item out of every pack, without cast-on enchantments (they don't tick on an escrowed item, and the buyer should get what the listing shows)
            var escrowed = EscrowCopy(item);

            if (!player.TryRemoveFromInventoryForVault(guid, out _))
            {
                Finish(player, VaultOutcome.NotInPack, name, itemGuid, completed);
                return;
            }

            // Saves the save queue already holds (a player save collects every changed possession) keep a reference to the item's live biota
            // and read it when they run, which may be just before the deposit job. Left cleared, the item would be saved without a container,
            // and a crash before the deposit job commits would leave an ownerless row for the startup purge to delete.
            // So the live item keeps saying where it was until the job is done; the job saves the escrowed copy.
            item.ContainerId = containerId;
            item.OwnerId = ownerId;
            item.PlacementPosition = placement;

            inFlight.Add(player.Guid.Full);

            if (player.CurrentAppraisalTarget == itemGuid)
                player.CurrentAppraisalTarget = null;

            var ticket = ticketId is long id ? new TicketCompletion(id, VaultMessages.DepositedByTicket(name, player.Name)) : null;

            DatabaseManager.Shard.DepositToVault(escrowed, new ReaderWriterLockSlim(), vaultItem, vaultSize, result =>
            {
                // this runs on the save thread
                WorldManager.EnqueueAction(new ActionEventDelegate(() => OnDeposited(player, item, name, result, completed)));
            }, ticket);
        }

        /// <summary>
        /// A copy of the item's biota as the Vault keeps it: no container, owner or pack slot, and no enchantments.
        /// The item's own spells are in its spell book and stay.
        /// </summary>
        private static ACE.Entity.Models.Biota EscrowCopy(WorldObject item)
        {
            ACE.Entity.Models.Biota copy;

            item.BiotaDatabaseLock.EnterReadLock();
            try
            {
                copy = BiotaConverter.ConvertToEntityBiota(BiotaConverter.ConvertFromEntityBiota(item.Biota));
            }
            finally
            {
                item.BiotaDatabaseLock.ExitReadLock();
            }

            copy.PropertiesIID?.Remove(PropertyInstanceId.Container);
            copy.PropertiesIID?.Remove(PropertyInstanceId.Owner);
            copy.PropertiesInt?.Remove(PropertyInt.PlacementPosition);
            copy.PropertiesEnchantmentRegistry?.Clear();

            return copy;
        }

        /// <summary>
        /// Moves an item from the account's Vault into the player's pack.
        /// The item is loaded from the database and pointed at the player in memory, but only added to the pack once the job has removed the Vault row,
        /// so that no save of the pack can ever put the item in a pack while its Vault row still exists.
        /// </summary>
        public static void Withdraw(Player player, uint itemGuid, Action<VaultResult> completed = null)
        {
            Withdraw(player, itemGuid, null, completed);
        }

        /// <summary>
        /// Withdraws an item whose Vault row the caller has already marked withdrawing (the channel does this when it starts).
        /// The row must still have the version the mark gave it. On a refusal or failure the row stays marked: the caller releases it.
        /// A game bridge ticket given as ticketId is marked done in the same save as the item.
        /// </summary>
        public static void Withdraw(Player player, uint itemGuid, uint markedRowVersion, Action<VaultResult> completed = null, long? ticketId = null)
        {
            Withdraw(player, itemGuid, (uint?)markedRowVersion, completed, ticketId);
        }

        private static void Withdraw(Player player, uint itemGuid, uint? markedRowVersion, Action<VaultResult> completed, long? ticketId = null)
        {
            VaultOutcome? refusal;
            VaultItem row;
            WorldObject item;

            try
            {
                refusal = CheckWithdraw(player, itemGuid, markedRowVersion, out row, out item);
            }
            catch (Exception ex)
            {
                // the checks read the database; nothing has moved yet, and the caller releases a channel's mark on any refusal
                log.Error($"[VAULT] Withdrawal of 0x{itemGuid:X8} for {player.Name} failed in its checks: {ex}");
                Finish(player, VaultOutcome.SaveFailed, null, itemGuid, completed);
                return;
            }

            if (refusal != null)
            {
                Finish(player, refusal.Value, row?.Name, itemGuid, completed);
                return;
            }

            var accountId = player.Character.AccountId;
            var name = item.Name;

            item.OwnerId = player.Guid.Full;
            item.ContainerId = player.Guid.Full;
            item.PlacementPosition = 0;

            inFlight.Add(player.Guid.Full);

            var ticket = ticketId == null ? null : new TicketCompletion(ticketId.Value, VaultMessages.WithdrawnByTicket(name, player.Name));

            DatabaseManager.Shard.WithdrawFromVault(item.Biota, item.BiotaDatabaseLock, accountId, player.Guid.Full, row.RowVersion, result =>
            {
                // this runs on the save thread
                WorldManager.EnqueueAction(new ActionEventDelegate(() => OnWithdrawn(player, item, name, result, completed)));
            }, ticket);
        }

        /// <summary>
        /// Withdraws a set of items in one save: all of them or none. It is instant, with no channel, and refused while a transfer is active, as a
        /// single withdrawal's channel start is. The rows and item biotas are read off the world thread, since reading a full page of them takes
        /// hundreds of milliseconds; the rules, the pack room and the in-memory move stay on the world thread. The result is reported once, on the world thread.
        /// </summary>
        public static void WithdrawMany(Player player, IReadOnlyList<uint> itemGuids, Action<VaultResult> completed)
        {
            var refusal = Available ? BatchRefusal(player, itemGuids) : VaultOutcome.NotAvailable;

            if (refusal != null)
            {
                FinishBatch(player, refusal.Value, null, itemGuids, completed);
                return;
            }

            // the player's other transfers wait until this batch's save is done, and that includes the reads below
            inFlight.Add(player.Guid.Full);

            var accountId = player.Character.AccountId;

            Task.Run(() =>
            {
                try
                {
                    var rows = VaultStore.Owned(accountId, itemGuids);
                    var biotas = rows.Keys.ToDictionary(guid => guid, guid => DatabaseManager.Shard.BaseDatabase.GetBiota(guid, doNotAddToCache: true));
                    WorldManager.EnqueueAction(new ActionEventDelegate(() => CheckAndWithdrawBatch(player, itemGuids, rows, biotas, completed)));
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] Batch withdrawal of {itemGuids.Count} items for {player.Name} could not read the Vault: {ex}");
                    WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        inFlight.Remove(player.Guid.Full);
                        FinishBatch(player, VaultOutcome.SaveFailed, null, itemGuids, completed);
                    }));
                }
            });
        }

        /// <summary>
        /// The refusals a batch needs no read for: a selection that is not one to a page of different items, and a transfer already under way
        /// </summary>
        private static VaultOutcome? BatchRefusal(Player player, IReadOnlyList<uint> itemGuids)
        {
            if (itemGuids.Count == 0 || itemGuids.Count > VaultChannelActions.PageSize || itemGuids.Distinct().Count() != itemGuids.Count)
                return VaultOutcome.BadSelection;

            if (inFlight.Contains(player.Guid.Full))
                return VaultOutcome.Busy;

            return VaultChannel.CheckStart(player);
        }

        /// <summary>
        /// The world thread's half of a batch, once its rows and biotas are read: the start rules again, since the player may have changed while
        /// they were read; then every rule, the pack room and the uniques for the set; then the save. A refusal here clears the batch's inFlight mark.
        /// </summary>
        private static void CheckAndWithdrawBatch(Player player, IReadOnlyList<uint> itemGuids, Dictionary<uint, VaultItem> rows, Dictionary<uint, ACE.Database.Models.Shard.Biota> biotas, Action<VaultResult> completed)
        {
            var entries = new List<(VaultItem Row, WorldObject Item)>(itemGuids.Count);
            string refusedName = null;
            VaultOutcome? refusal;

            try
            {
                refusal = VaultChannel.CheckStart(player) ?? CheckWithdrawSet(player, itemGuids, rows, biotas, entries, out refusedName);
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] Batch withdrawal of {itemGuids.Count} items for {player.Name} failed in its checks: {ex}");
                refusal = VaultOutcome.SaveFailed;
            }

            if (refusal != null)
            {
                inFlight.Remove(player.Guid.Full);
                FinishBatch(player, refusal.Value, refusedName, itemGuids, completed);
                return;
            }

            // the same in-memory move a single withdrawal makes: the item is the player's, and the save sets it in the pack
            foreach (var (_, item) in entries)
            {
                item.OwnerId = player.Guid.Full;
                item.ContainerId = player.Guid.Full;
                item.PlacementPosition = 0;
            }

            var withdrawals = entries.Select(entry => (entry.Item.Biota, entry.Item.BiotaDatabaseLock, entry.Row.RowVersion)).ToList();
            var items = entries.Select(entry => entry.Item).ToList();

            DatabaseManager.Shard.WithdrawManyFromVault(withdrawals, player.Character.AccountId, player.Guid.Full, result =>
            {
                // this runs on the save thread
                WorldManager.EnqueueAction(new ActionEventDelegate(() => OnWithdrawnMany(player, items, result, itemGuids, completed)));
            });
        }

        private static void OnWithdrawnMany(Player player, List<WorldObject> items, MarketJobResult result, IReadOnlyList<uint> itemGuids, Action<VaultResult> completed)
        {
            inFlight.Remove(player.Guid.Full);

            if (result != MarketJobResult.Saved)
            {
                // the job saved every item or none, so nothing is in the pack
                if (result == MarketJobResult.Unknown)
                    log.Error($"[VAULT] Batch withdrawal of {items.Count} items for {player.Name} may or may not have been saved; the database decides at the next login");

                FinishBatch(player, FailedOutcome(result), items[0].Name, itemGuids, completed);
                return;
            }

            // the database has every item in this character's pack; each goes into the live pack, or waits for the next login as one withdrawal does
            var atLogin = 0;

            foreach (var item in items)
            {
                if (!AddWithdrawnToPack(player, item))
                {
                    atLogin++;
                    log.Warn($"[VAULT] Withdrawn {item.Name} (0x{item.Guid.Full:X8}) for {player.Name} could not be added to the pack; the database has it in the pack for the next login");
                }
            }

            FinishBatch(player, atLogin == 0 ? VaultOutcome.Withdrawn : VaultOutcome.WithdrawnAtLogin, items[0].Name, itemGuids, completed);
        }

        /// <summary>
        /// The outcome of a withdrawal save that did not happen: a banned account, an unknown save (which the next login settles), or any other failure
        /// </summary>
        private static VaultOutcome FailedOutcome(MarketJobResult result) => result switch
        {
            MarketJobResult.Banned => VaultOutcome.Banned,
            MarketJobResult.Unknown => VaultOutcome.Unconfirmed,
            _ => VaultOutcome.SaveFailed,
        };

        /// <summary>
        /// Puts a withdrawn item in the live pack, once the database has it there. False if the pack has no room now, and then the item is in the pack at the next login.
        /// A player who has logged out has nothing to put it into, so the database's copy is their pack's.
        /// </summary>
        private static bool AddWithdrawnToPack(Player player, WorldObject item) => player.IsLoggingOut || player.TryCreateInInventoryWithNetworking(item);

        /// <summary>
        /// Every deposit refusal rule, in order, without changing anything. Null if the item can go in the Vault.
        /// The item is set whenever the player has it, so a refusal can name it.
        /// </summary>
        public static VaultOutcome? CheckDeposit(Player player, uint itemGuid, out WorldObject item)
        {
            item = null;

            if (!Available)
                return VaultOutcome.NotAvailable;

            if (inFlight.Contains(player.Guid.Full))
                return VaultOutcome.Busy;

            var guid = new ObjectGuid(itemGuid);

            if (player.EquippedObjects.TryGetValue(guid, out item))
                return VaultOutcome.Worn;

            item = player.GetInventoryItem(guid);

            if (item == null)
                return VaultOutcome.NotInPack;

            if (player.ItemsInTradeWindow.Contains(guid))
                return VaultOutcome.InTrade;

            var refusal = CheckDepositRules(player, item);

            if (refusal != null)
                return refusal;

            if (VaultStore.Count(player.Character.AccountId) >= (int)MarketSettings.Get(MarketSettings.VaultSize))
                return VaultOutcome.VaultFull;

            return null;
        }

        /// <summary>
        /// Every withdrawal refusal rule, in order. Null if the item can be withdrawn. Nothing is changed, but the item is read from the database
        /// and created (not added anywhere) once the row checks pass, for the pack-space and unique checks. The row is set whenever it is the player's account's.
        /// With no marked version the row must be held; with one, it must be withdrawing with exactly that version.
        /// </summary>
        public static VaultOutcome? CheckWithdraw(Player player, uint itemGuid, uint? markedRowVersion, out VaultItem row, out WorldObject item)
        {
            var refusal = CheckWithdrawRow(player, itemGuid, markedRowVersion, out row, out item);

            if (refusal != null)
                return refusal;

            if (!player.CanAddToInventory(item))
                return VaultOutcome.NoPackSpace;

            if (item.IsUniqueOrContainsUnique && !player.CheckUniques(item))
                return VaultOutcome.UniqueLimit;

            return null;
        }

        /// <summary>
        /// Every withdrawal refusal rule for a set, in order, on the rows and biotas already read: each item's own rules come first, so the first refusal
        /// is the one reported; then the pack room and the uniques for the whole set, so a set that fits only item by item is refused.
        /// Null if the set can be withdrawn; the entries are then each item's row and item, in the order given.
        /// </summary>
        private static VaultOutcome? CheckWithdrawSet(Player player, IReadOnlyList<uint> itemGuids, Dictionary<uint, VaultItem> rows, Dictionary<uint, ACE.Database.Models.Shard.Biota> biotas,
            List<(VaultItem Row, WorldObject Item)> entries, out string refusedName)
        {
            refusedName = null;

            foreach (var itemGuid in itemGuids)
            {
                rows.TryGetValue(itemGuid, out var row);
                var refusal = RowRefusal(row, null);

                if (refusal != null)
                {
                    refusedName = row?.Name;
                    return refusal;
                }

                if (!biotas.TryGetValue(itemGuid, out var biota) || biota == null)
                {
                    log.Error($"[VAULT] {player.Name} tried to withdraw 0x{itemGuid:X8}, which has a Vault row but no item row");
                    return VaultOutcome.NotInVault;
                }

                var item = WorldObjectFactory.CreateWorldObject(biota);

                if (item == null)
                {
                    log.Error($"[VAULT] {player.Name} tried to withdraw 0x{itemGuid:X8}, which could not be created from its biota");
                    return VaultOutcome.SaveFailed;
                }

                entries.Add((row, item));
            }

            var items = entries.Select(entry => entry.Item).ToList();

            if (!player.CanAddToInventory(items, out var tooHeavy, out _))
                return tooHeavy ? VaultOutcome.TooHeavy : VaultOutcome.NoPackSpace;

            if (!player.CheckUniques(items))
                return VaultOutcome.UniqueLimit;

            return null;
        }

        /// <summary>
        /// The rules a Vault row must pass for its item to be withdrawn: the row exists and is held, or, when the channel marked it, still carries exactly that mark.
        /// Null if it passes. The caller has already limited the row to the player's account.
        /// </summary>
        private static VaultOutcome? RowRefusal(VaultItem row, uint? markedRowVersion)
        {
            if (row == null)
                return VaultOutcome.NotInVault;

            if (row.State == VaultItemState.Listed)
                return VaultOutcome.Listed;

            // a plain withdrawal needs a held row; one the channel marked must still carry the channel's mark
            if (markedRowVersion == null && row.State == VaultItemState.Withdrawing)
                return VaultOutcome.Withdrawing;

            if (markedRowVersion != null && (row.State != VaultItemState.Withdrawing || row.RowVersion != markedRowVersion))
                return VaultOutcome.Withdrawing;

            return null;
        }

        /// <summary>
        /// The rules every single withdrawal of an item shares: the account's Vault row, its state and marked version, and the item read from the database
        /// and created (not added anywhere). Null if they pass. The pack-space and unique checks are the caller's.
        /// </summary>
        private static VaultOutcome? CheckWithdrawRow(Player player, uint itemGuid, uint? markedRowVersion, out VaultItem row, out WorldObject item)
        {
            row = null;
            item = null;

            if (!Available)
                return VaultOutcome.NotAvailable;

            if (inFlight.Contains(player.Guid.Full))
                return VaultOutcome.Busy;

            row = VaultStore.Get(itemGuid);

            // another account's row is as good as none, and its name is not told
            if (row != null && row.AccountId != player.Character.AccountId)
                row = null;

            var refusal = RowRefusal(row, markedRowVersion);

            if (refusal != null)
                return refusal;

            var biota = DatabaseManager.Shard.BaseDatabase.GetBiota(itemGuid, doNotAddToCache: true);

            if (biota == null)
            {
                log.Error($"[VAULT] {player.Name} tried to withdraw 0x{itemGuid:X8}, which has a Vault row but no item row");
                return VaultOutcome.NotInVault;
            }

            item = WorldObjectFactory.CreateWorldObject(biota);

            if (item == null)
            {
                log.Error($"[VAULT] {player.Name} tried to withdraw 0x{itemGuid:X8}, which could not be created from its biota");
                return VaultOutcome.SaveFailed;
            }

            return null;
        }

        /// <summary>
        /// The trade rules (attuned or containing attuned, a pet device with its pet out), plus containers holding anything and blocked WCIDs
        /// </summary>
        private static VaultOutcome? CheckDepositRules(Player player, WorldObject item)
        {
            if (item.IsAttunedOrContainsAttuned)
                return item.Attuned >= AttunedStatus.Attuned ? VaultOutcome.Attuned : VaultOutcome.ContainsAttuned;

            if (item is PetDevice petDevice && petDevice.Pet is not null)
                return VaultOutcome.PetOut;

            if (item is Container container && container.Inventory.Count > 0)
                return VaultOutcome.ContainerNotEmpty;

            if (VaultStore.IsWcidBlocked(item.WeenieClassId))
                return VaultOutcome.BlockedWcid;

            return null;
        }

        private static void OnDeposited(Player player, WorldObject item, string name, MarketJobResult result, Action<VaultResult> completed)
        {
            inFlight.Remove(player.Guid.Full);

            if (result == MarketJobResult.Saved)
            {
                // The object is forgotten, never destroyed: destroying it would delete its row. Cleared as the Vault has it,
                // so that a save of it from anything still holding it can't put it back in a pack.
                item.ContainerId = null;
                item.OwnerId = null;
                item.PlacementPosition = null;

                Finish(player, VaultOutcome.Deposited, name, item.Guid.Full, completed);
                return;
            }

            if (result == MarketJobResult.Unknown)
            {
                // the database has the item either in the pack or in the Vault; putting it back here could make a second copy, so the next login shows which
                log.Error($"[VAULT] Deposit of {name} (0x{item.Guid.Full:X8}) for {player.Name} may or may not have been saved; the object is dropped and the database decides at the next login");
                Finish(player, VaultOutcome.Unconfirmed, name, item.Guid.Full, completed);
                return;
            }

            log.Warn($"[VAULT] Deposit of {name} (0x{item.Guid.Full:X8}) for {player.Name} failed ({result}); nothing was saved");

            // the job refuses without saving when the Vault filled up meanwhile (another character of the account, or the setting was lowered)
            var outcome = VaultOutcome.SaveFailed;

            try
            {
                if (VaultStore.Count(player.Character.AccountId) >= (int)MarketSettings.Get(MarketSettings.VaultSize))
                    outcome = VaultOutcome.VaultFull;
            }
            catch (Exception ex)
            {
                log.Warn($"[VAULT] Could not read the Vault size for {player.Name}'s failed deposit: {ex.Message}");
            }

            // the database still has the item in the pack (the live item kept its container throughout). A player who has gone gets it back when they log in, so the object is just discarded.
            if (!player.IsLoggingOut && !player.TryCreateInInventoryWithNetworking(item))
                log.Warn($"[VAULT] Deposit of {name} (0x{item.Guid.Full:X8}) for {player.Name} failed and the pack has no room for it; the database has it in the pack for the next login");

            Finish(player, outcome, name, item.Guid.Full, completed);
        }

        private static void OnWithdrawn(Player player, WorldObject item, string name, MarketJobResult result, Action<VaultResult> completed)
        {
            inFlight.Remove(player.Guid.Full);

            if (result != MarketJobResult.Saved)
            {
                // The object was never added anywhere: forget it. After a failure the Vault row is still there.
                // After Unknown the item is either still in the Vault or already in this character's pack in the database, and the next login shows which.
                if (result == MarketJobResult.Unknown)
                    log.Error($"[VAULT] Withdrawal of {name} (0x{item.Guid.Full:X8}) for {player.Name} may or may not have been saved; the database decides at the next login");

                Finish(player, FailedOutcome(result), name, item.Guid.Full, completed);
                return;
            }

            // the database now has the item in this character's pack, so a player who has gone gets it at the next login
            var inPack = AddWithdrawnToPack(player, item);

            if (!inPack)
                log.Warn($"[VAULT] Withdrawn {name} (0x{item.Guid.Full:X8}) for {player.Name} could not be added to the pack; the database has it in the pack for the next login");

            Finish(player, inPack ? VaultOutcome.Withdrawn : VaultOutcome.WithdrawnAtLogin, name, item.Guid.Full, completed);
        }

        /// <summary>
        /// Tells the player the outcome and reports it to the caller
        /// </summary>
        internal static void Finish(Player player, VaultOutcome outcome, string itemName, uint itemGuid, Action<VaultResult> completed)
        {
            Report(player, new VaultResult(outcome, VaultMessages.For(outcome, itemName), itemGuid), completed, push: true);
        }

        /// <summary>
        /// The batch's outcome, as <see cref="Finish"/> reports a single one. Only an outcome that may have moved items is pushed: a refused batch
        /// pushes nothing, so the window keeps its selection and only shows the reason.
        /// </summary>
        private static void FinishBatch(Player player, VaultOutcome outcome, string firstName, IReadOnlyList<uint> itemGuids, Action<VaultResult> completed)
        {
            var mayHaveMoved = outcome is VaultOutcome.Withdrawn or VaultOutcome.WithdrawnAtLogin or VaultOutcome.Unconfirmed;
            var firstGuid = itemGuids.Count > 0 ? itemGuids[0] : 0u;
            Report(player, new VaultResult(outcome, VaultMessages.ForBatch(outcome, firstName, itemGuids.Count), firstGuid), completed, push: mayHaveMoved);
        }

        private static void Report(Player player, VaultResult result, Action<VaultResult> completed, bool push)
        {
            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(result.Message, ChatMessageType.Broadcast));

            if (push)
                VaultChannelActions.PushChanged(player, result);

            completed?.Invoke(result);
        }

        /// <summary>
        /// A Vault row for the item, with the search columns copied from it. Escrowed items don't change, so the copy stays true.
        /// </summary>
        public static VaultItem NewVaultItem(WorldObject item, uint accountId, uint characterId)
        {
            return new VaultItem
            {
                ItemGuid = item.Guid.Full,
                AccountId = accountId,
                CharacterId = characterId,
                State = VaultItemState.Held,

                Wcid = item.WeenieClassId,
                Name = item.Name,
                ItemType = (int)item.ItemType,
                StackSize = item.StackSize ?? 1,
                Value = item.Value ?? 0,
                IconUnderlay = item.IconUnderlayId,
                Icon = item.IconId,
                IconOverlay = item.IconOverlayId,
                IconOverlaySecondary = item.IconOverlaySecondary,
                UiEffects = (int?)item.UiEffects,
                PaletteTemplate = item.PaletteTemplate,
                ClothingBase = item.ClothingBase,
                Workmanship = item.ItemWorkmanship,
                ArcaneLore = item.GetProperty(PropertyInt.ItemDifficulty),
                WieldRequirements = (int)item.WieldRequirements,
                WieldSkillType = item.WieldSkillType,
                WieldDifficulty = item.WieldDifficulty,
                ArmorLevel = item.ArmorLevel,
                Damage = item.Damage,
                DamageMod = item.DamageMod,
                MaterialType = item.GetProperty(PropertyInt.MaterialType),
                EquipmentSetId = item.GetProperty(PropertyInt.EquipmentSetId),
                ImbuedEffect = item.GetProperty(PropertyInt.ImbuedEffect),
            };
        }
    }
}
