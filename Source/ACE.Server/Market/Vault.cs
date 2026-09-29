using System;
using System.Collections.Generic;

using log4net;

using ACE.Database;
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
    /// The game side of the Vault: the one entry point the /vault commands (and later the game bridge) use to move an item between a pack and the account's Vault.
    /// Call Deposit and Withdraw on the world thread. The result is reported once through the callback:
    /// at once for a refusal, on the world thread after the save for a deposit or withdrawal.
    /// </summary>
    public static class Vault
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// False until Initialize finds every market table and trigger. Until then the Vault does nothing.
        /// </summary>
        public static bool Available { get; private set; }

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

            if (Available)
                log.Info($"[VAULT] Market schema check: {check.Report}");
            else
                log.Error($"[VAULT] Market schema check failed ({check.Report}). The Vault and the market are disabled until the market tables are installed and the server restarted.");
        }

        /// <summary>
        /// The player's account Vault, oldest deposit first
        /// </summary>
        public static List<VaultItem> List(Player player)
        {
            return Available ? VaultStore.List(player.Character.AccountId) : new List<VaultItem>();
        }

        /// <summary>
        /// Moves an item from the player's pack into their account's Vault.
        /// The item is taken out of the pack in memory only. The networking removal saves an ownerless item, and destroying the item would delete its row.
        /// The deposit job then saves the item and the Vault row together. On success the object is forgotten; on failure it goes back to the pack.
        /// </summary>
        public static void Deposit(Player player, uint itemGuid, Action<VaultResult> completed = null)
        {
            if (!Available)
            {
                Finish(player, VaultOutcome.NotAvailable, null, itemGuid, completed);
                return;
            }

            if (inFlight.Contains(player.Guid.Full))
            {
                Finish(player, VaultOutcome.Busy, null, itemGuid, completed);
                return;
            }

            var guid = new ObjectGuid(itemGuid);

            if (player.EquippedObjects.TryGetValue(guid, out var worn))
            {
                Finish(player, VaultOutcome.Worn, worn.Name, itemGuid, completed);
                return;
            }

            var item = player.GetInventoryItem(guid);

            if (item == null)
            {
                Finish(player, VaultOutcome.NotInPack, null, itemGuid, completed);
                return;
            }

            if (player.ItemsInTradeWindow.Contains(guid))
            {
                Finish(player, VaultOutcome.InTrade, item.Name, itemGuid, completed);
                return;
            }

            var refusal = CheckDepositRules(player, item);

            if (refusal != null)
            {
                Finish(player, refusal.Value, item.Name, itemGuid, completed);
                return;
            }

            var accountId = player.Character.AccountId;
            var vaultSize = (int)MarketSettings.Get(MarketSettings.VaultSize);

            if (VaultStore.Count(accountId) >= vaultSize)
            {
                Finish(player, VaultOutcome.VaultFull, item.Name, itemGuid, completed);
                return;
            }

            var name = item.Name;
            var vaultItem = NewVaultItem(item, accountId, player.Guid.Full);

            if (!player.TryRemoveFromInventoryForVault(guid, out _))
            {
                Finish(player, VaultOutcome.NotInPack, name, itemGuid, completed);
                return;
            }

            // cast-on enchantments don't tick on an escrowed item, and the buyer should get what the listing shows; the save deletes the rows no longer present
            item.EnchantmentManager.DispelAllEnchantments();

            inFlight.Add(player.Guid.Full);

            if (player.CurrentAppraisalTarget == itemGuid)
                player.CurrentAppraisalTarget = null;

            DatabaseManager.Shard.DepositToVault(item.Biota, item.BiotaDatabaseLock, vaultItem, vaultSize, saved =>
            {
                // this runs on the save thread
                WorldManager.EnqueueAction(new ActionEventDelegate(() => OnDeposited(player, item, name, saved, completed)));
            });
        }

        /// <summary>
        /// Moves an item from the account's Vault into the player's pack.
        /// The item is loaded from the database and pointed at the player in memory, but only added to the pack once the job has removed the Vault row,
        /// so that no save of the pack can ever put the item in a pack while its Vault row still exists.
        /// </summary>
        public static void Withdraw(Player player, uint itemGuid, Action<VaultResult> completed = null)
        {
            if (!Available)
            {
                Finish(player, VaultOutcome.NotAvailable, null, itemGuid, completed);
                return;
            }

            if (inFlight.Contains(player.Guid.Full))
            {
                Finish(player, VaultOutcome.Busy, null, itemGuid, completed);
                return;
            }

            var accountId = player.Character.AccountId;
            var row = VaultStore.Get(itemGuid);

            if (row == null || row.AccountId != accountId)
            {
                Finish(player, VaultOutcome.NotInVault, null, itemGuid, completed);
                return;
            }

            if (row.State == VaultItemState.Listed)
            {
                Finish(player, VaultOutcome.Listed, row.Name, itemGuid, completed);
                return;
            }

            if (row.State == VaultItemState.Withdrawing)
            {
                Finish(player, VaultOutcome.Withdrawing, row.Name, itemGuid, completed);
                return;
            }

            var biota = DatabaseManager.Shard.BaseDatabase.GetBiota(itemGuid, doNotAddToCache: true);

            if (biota == null)
            {
                log.Error($"[VAULT] {player.Name} tried to withdraw 0x{itemGuid:X8}, which has a Vault row but no item row");
                Finish(player, VaultOutcome.NotInVault, row.Name, itemGuid, completed);
                return;
            }

            var item = WorldObjectFactory.CreateWorldObject(biota);

            if (item == null)
            {
                log.Error($"[VAULT] {player.Name} tried to withdraw 0x{itemGuid:X8}, which could not be created from its biota");
                Finish(player, VaultOutcome.SaveFailed, row.Name, itemGuid, completed);
                return;
            }

            if (!player.CanAddToInventory(item))
            {
                Finish(player, VaultOutcome.NoPackSpace, row.Name, itemGuid, completed);
                return;
            }

            if (item.IsUniqueOrContainsUnique && !player.CheckUniques(item))
            {
                Finish(player, VaultOutcome.UniqueLimit, row.Name, itemGuid, completed);
                return;
            }

            var name = item.Name;

            item.OwnerId = player.Guid.Full;
            item.ContainerId = player.Guid.Full;
            item.PlacementPosition = 0;

            inFlight.Add(player.Guid.Full);

            DatabaseManager.Shard.WithdrawFromVault(item.Biota, item.BiotaDatabaseLock, accountId, player.Guid.Full, row.RowVersion, saved =>
            {
                // this runs on the save thread
                WorldManager.EnqueueAction(new ActionEventDelegate(() => OnWithdrawn(player, item, name, saved, completed)));
            });
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

        private static void OnDeposited(Player player, WorldObject item, string name, bool saved, Action<VaultResult> completed)
        {
            inFlight.Remove(player.Guid.Full);

            if (saved)
            {
                // the object is forgotten, never saved or destroyed: a later save would restore its container, and destroying it would delete its row
                Finish(player, VaultOutcome.Deposited, name, item.Guid.Full, completed);
                return;
            }

            log.Warn($"[VAULT] Deposit of {name} (0x{item.Guid.Full:X8}) for {player.Name} failed; nothing was saved");

            // the job refuses without saving when the Vault filled up meanwhile (another character of the account, or the setting was lowered)
            var outcome = VaultStore.Count(player.Character.AccountId) >= (int)MarketSettings.Get(MarketSettings.VaultSize) ? VaultOutcome.VaultFull : VaultOutcome.SaveFailed;

            // the database still has the item in the pack. A player who has gone gets it back when they log in, so the object is just discarded.
            if (!player.IsLoggingOut && !player.TryCreateInInventoryWithNetworking(item))
                log.Warn($"[VAULT] Deposit of {name} (0x{item.Guid.Full:X8}) for {player.Name} failed and the pack has no room for it; the database has it in the pack for the next login");

            Finish(player, outcome, name, item.Guid.Full, completed);
        }

        private static void OnWithdrawn(Player player, WorldObject item, string name, bool saved, Action<VaultResult> completed)
        {
            inFlight.Remove(player.Guid.Full);

            if (!saved)
            {
                // the Vault row is still there and the object was never added anywhere: forget it
                Finish(player, VaultOutcome.SaveFailed, name, item.Guid.Full, completed);
                return;
            }

            // the database now has the item in this character's pack, so a player who has gone gets it at the next login
            var inPack = player.IsLoggingOut || player.TryCreateInInventoryWithNetworking(item);

            if (!inPack)
                log.Warn($"[VAULT] Withdrawn {name} (0x{item.Guid.Full:X8}) for {player.Name} could not be added to the pack; the database has it in the pack for the next login");

            Finish(player, inPack ? VaultOutcome.Withdrawn : VaultOutcome.WithdrawnAtLogin, name, item.Guid.Full, completed);
        }

        private static void Finish(Player player, VaultOutcome outcome, string itemName, uint itemGuid, Action<VaultResult> completed)
        {
            var result = new VaultResult(outcome, VaultMessages.For(outcome, itemName), itemGuid);

            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(result.Message, ChatMessageType.Broadcast));

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
