using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Market;
using ACE.Server.WorldObjects;

using EntityEnchantment = ACE.Entity.Models.PropertiesEnchantmentRegistry;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: the game-side Vault. Every test drives the one Vault entry point (the one the /vault commands use) with a test player in a started world.
    /// </summary>
    [TestClass]
    public class VaultTests
    {
        private const string Db = VaultTestWorld.Db;

        [ClassInitialize]
        public static void TestSetup(TestContext context) => VaultTestWorld.Start();

        [ClassCleanup]
        public static void TestCleanup() => VaultTestWorld.Stop();

        // ---- deposit and withdraw

        [TestMethod]
        public void Deposit_PackItem_LeavesPackAndListsForEveryCharacterOnAccount()
        {
            var account = VaultTestWorld.NewAccountId();
            var depositor = VaultTestWorld.NewPlayer(account);
            var sibling = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(depositor, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;
            var burdenBefore = depositor.EncumbranceVal ?? 0;

            var result = VaultTestWorld.Deposit(depositor, guid);

            Assert.AreEqual(VaultOutcome.Deposited, result.Outcome, result.Message);
            Assert.IsNull(depositor.GetInventoryItem(guid), "the item left the pack");
            Assert.IsTrue((depositor.EncumbranceVal ?? 0) < burdenBefore, "the burden went down");
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"), "the item row has no container");

            foreach (var player in new[] { depositor, sibling })
            {
                var listed = Vault.List(player).Single(r => r.ItemGuid == guid);
                Assert.AreEqual(account, listed.AccountId);
                Assert.AreEqual(depositor.Guid.Full, listed.CharacterId);
                Assert.AreEqual(VaultItemState.Held, listed.State);
                Assert.AreEqual(VaultTestWorld.SwordWcid, listed.Wcid);
                Assert.AreEqual(item.Name, listed.Name);
            }
        }

        [TestMethod]
        public void Withdraw_OtherCharacter_GetsSameItemWithSameProperties()
        {
            var account = VaultTestWorld.NewAccountId();
            var depositor = VaultTestWorld.NewPlayer(account);
            var withdrawer = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            item.SetProperty(PropertyString.Inscription, "for the vault");
            item.SetProperty(PropertyInt.NumTimesTinkered, 3);
            VaultTestWorld.Give(depositor, item);
            var guid = item.Guid.Full;
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(depositor, guid).Outcome);

            var result = VaultTestWorld.Withdraw(withdrawer, guid);

            Assert.AreEqual(VaultOutcome.Withdrawn, result.Outcome, result.Message);
            var back = withdrawer.GetInventoryItem(guid);
            Assert.IsNotNull(back, "the item is in the withdrawing character's pack");
            Assert.AreEqual(item.WeenieClassId, back.WeenieClassId);
            Assert.AreEqual("for the vault", back.GetProperty(PropertyString.Inscription));
            Assert.AreEqual(3, back.GetProperty(PropertyInt.NumTimesTinkered));
            Assert.AreEqual(item.Name, back.Name);
            Assert.IsNull(depositor.GetInventoryItem(guid));
            Assert.AreEqual(0, Vault.List(withdrawer).Count(r => r.ItemGuid == guid), "the Vault row is gone");
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
            VaultTestWorld.OnWorldThread(() => { }); // let any follow-up save settle
            Assert.AreEqual(withdrawer.Guid.Full, (uint)Count($"SELECT value FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"), "the database has the item in the withdrawing character's pack");
        }

        // ---- deposit refusals

        [TestMethod]
        public void Deposit_AttunedItem_IsRefusedAndStaysInPack()
        {
            var carried = NewItem(i => i.Attuned = AttunedStatus.Attuned);
            AssertDepositRefused(carried, VaultOutcome.Attuned);
        }

        [TestMethod]
        public void Deposit_ContainerHoldingAttunedItem_IsRefusedAsContainsAttuned()
        {
            var carried = NewPackHolding(inner => inner.Attuned = AttunedStatus.Attuned);
            AssertDepositRefused(carried, VaultOutcome.ContainsAttuned);
        }

        [TestMethod]
        public void Deposit_PetDeviceWithPetOut_IsRefused()
        {
            var carried = NewItem(i => ((PetDevice)i).Pet = 0x80009999, VaultTestWorld.PetDeviceWcid);
            AssertDepositRefused(carried, VaultOutcome.PetOut);
        }

        [TestMethod]
        public void Deposit_NonEmptyContainer_IsRefused()
        {
            var carried = NewPackHolding(null);
            AssertDepositRefused(carried, VaultOutcome.ContainerNotEmpty);
        }

        [TestMethod]
        public void Deposit_WornItem_IsRefused()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            Assert.IsTrue(player.TryEquipObject(item, EquipMask.MeleeWeapon));

            var result = VaultTestWorld.Deposit(player, item.Guid.Full);

            Assert.AreEqual(VaultOutcome.Worn, result.Outcome, result.Message);
            Assert.IsTrue(player.EquippedObjects.ContainsKey(item.Guid), "still worn");
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {item.Guid.Full};"));
        }

        [TestMethod]
        public void Deposit_BlockedWcid_IsRefused()
        {
            var carried = NewItem(null, 35); // basinetchainmail: any weenie will do, the block is by class id
            var wcid = carried.item.WeenieClassId;
            MarketTestDatabase.Execute(Db, $"INSERT INTO market_blocked_wcid (wcid, reason, added_By_Account_Id, added_Time) VALUES ({wcid}, 'test', 1, UTC_TIMESTAMP(6));");

            try
            {
                AssertDepositRefused(carried, VaultOutcome.BlockedWcid);
            }
            finally
            {
                MarketTestDatabase.Execute(Db, $"DELETE FROM market_blocked_wcid WHERE wcid = {wcid};");
            }
        }

        [TestMethod]
        public void Deposit_VaultFull_IsRefused()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var first = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var second = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));

            MarketTestDatabase.Execute(Db, $"REPLACE INTO config_properties_long (`key`, `value`, description) VALUES ('{MarketSettings.VaultSize.Key}', 1, 'test');");

            try
            {
                Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, first.Guid.Full).Outcome);

                var result = VaultTestWorld.Deposit(player, second.Guid.Full);

                Assert.AreEqual(VaultOutcome.VaultFull, result.Outcome, result.Message);
                Assert.IsNotNull(player.GetInventoryItem(second.Guid.Full), "the second item stays in the pack");
                Assert.AreEqual(1, VaultStore.Count(account));
            }
            finally
            {
                MarketTestDatabase.Execute(Db, $"DELETE FROM config_properties_long WHERE `key` = '{MarketSettings.VaultSize.Key}';");
            }
        }

        [TestMethod]
        public void Deposit_EachRefusalHasItsOwnMessage()
        {
            // the messages differ so a player can tell what to fix
            var outcomes = new[] { VaultOutcome.NotAvailable, VaultOutcome.NotInPack, VaultOutcome.Worn, VaultOutcome.Attuned, VaultOutcome.ContainsAttuned, VaultOutcome.PetOut, VaultOutcome.ContainerNotEmpty, VaultOutcome.BlockedWcid, VaultOutcome.VaultFull };
            var messages = outcomes.Select(o => VaultMessages.For(o, "Sword")).ToList();

            Assert.AreEqual(messages.Count, messages.Distinct().Count());
            Assert.IsTrue(messages.All(m => !string.IsNullOrWhiteSpace(m)));
        }

        // ---- withdrawal refusals

        [TestMethod]
        public void Withdraw_NoPackSpace_IsRefusedAndItemStaysInVault()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, guid).Outcome);

            player.ItemCapacity = 0; // the pack has no room

            var result = VaultTestWorld.Withdraw(player, guid);

            Assert.AreEqual(VaultOutcome.NoPackSpace, result.Outcome, result.Message);
            Assert.IsNull(player.GetInventoryItem(guid));
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
        }

        [TestMethod]
        public void Withdraw_ListedItem_IsRefused()
        {
            var (player, guid) = DepositedItem();
            MarketTestDatabase.Execute(Db, $"UPDATE market_vault_item SET state = '{VaultItemState.Listed}', row_Version = row_Version + 1 WHERE item_Guid = {guid};");

            var result = VaultTestWorld.Withdraw(player, guid);

            Assert.AreEqual(VaultOutcome.Listed, result.Outcome, result.Message);
            Assert.IsNull(player.GetInventoryItem(guid));
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
        }

        [TestMethod]
        public void Withdraw_WithdrawingItem_IsRefused()
        {
            var (player, guid) = DepositedItem();
            MarketTestDatabase.Execute(Db, $"UPDATE market_vault_item SET state = '{VaultItemState.Withdrawing}', row_Version = row_Version + 1 WHERE item_Guid = {guid};");

            var result = VaultTestWorld.Withdraw(player, guid);

            Assert.AreEqual(VaultOutcome.Withdrawing, result.Outcome, result.Message);
            Assert.IsNull(player.GetInventoryItem(guid));
        }

        [TestMethod]
        public void Withdraw_UniqueLimitExceeded_IsRefused()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var stored = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            stored.Unique = 1;
            VaultTestWorld.Give(player, stored);
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, stored.Guid.Full).Outcome);

            var carried = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid); // the player picks up a second one of the same unique weenie
            carried.Unique = 1;
            VaultTestWorld.Give(player, carried);

            var result = VaultTestWorld.Withdraw(player, stored.Guid.Full);

            Assert.AreEqual(VaultOutcome.UniqueLimit, result.Outcome, result.Message);
            Assert.IsNull(player.GetInventoryItem(stored.Guid.Full));
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {stored.Guid.Full};"));
        }

        [TestMethod]
        public void Withdraw_AnotherAccountsItem_IsNotInVault()
        {
            var (_, guid) = DepositedItem();
            var stranger = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            var result = VaultTestWorld.Withdraw(stranger, guid);

            Assert.AreEqual(VaultOutcome.NotInVault, result.Outcome, result.Message);
            Assert.IsNull(stranger.GetInventoryItem(guid));
        }

        // ---- enchantments

        [TestMethod]
        public void Deposit_RemovesCastOnEnchantments_KeepsOwnSpells()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            item.Biota.PropertiesSpellBook ??= new System.Collections.Generic.Dictionary<int, float>();
            item.Biota.PropertiesSpellBook[2] = 2.0f; // the item's own spell
            item.Biota.PropertiesEnchantmentRegistry ??= new System.Collections.Generic.List<EntityEnchantment>();
            item.Biota.PropertiesEnchantmentRegistry.Add(new EntityEnchantment { SpellId = 1234, LayerId = 1, CasterObjectId = 0x50000099, Duration = 1800, PowerLevel = 6, StatModType = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat, StatModKey = 1, StatModValue = 1.5f });
            item.ChangesDetected = true;
            VaultTestWorld.Give(player, item);
            var guid = item.Guid.Full;
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM biota_properties_enchantment_registry WHERE object_Id = {guid};"), "the test item starts with a cast-on enchantment");

            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, guid).Outcome);

            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota_properties_enchantment_registry WHERE object_Id = {guid};"), "the cast-on enchantment is gone");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM biota_properties_spell_book WHERE object_Id = {guid} AND spell = 2;"), "the item's own spell remains");
        }

        // ---- failure

        [TestMethod]
        public void Deposit_SaveFails_ItemBackInPackVaultUnchanged()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;

            VaultResult result;
            using (FailInsertsInto("market_item_event"))
                result = VaultTestWorld.Deposit(player, guid);

            Assert.AreEqual(VaultOutcome.SaveFailed, result.Outcome, result.Message);
            Assert.IsNotNull(player.GetInventoryItem(guid), "the item is back in the pack");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"), "the database still has it in the pack");
            Assert.AreEqual(0, VaultStore.Count(account), "the Vault is unchanged");

            // and it can be deposited once the failure is gone
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, guid).Outcome);
        }

        [TestMethod]
        public void Deposit_SaveFailsAfterLogout_ObjectIsDiscarded()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;

            player.IsLoggingOut = true;

            VaultResult result;
            using (FailInsertsInto("market_item_event"))
                result = VaultTestWorld.Deposit(player, guid);

            Assert.AreEqual(VaultOutcome.SaveFailed, result.Outcome, result.Message);
            Assert.IsNull(player.GetInventoryItem(guid), "a player who left doesn't get the object back");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"), "the database still has it in the pack");
            Assert.AreEqual(0, VaultStore.Count(account));
        }

        // ---- restart

        [TestMethod]
        public void Deposit_ThenRestart_ItemStaysInVaultAndIsNeverLoaded()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, guid).Outcome);

            // what a restart does: the startup check, then loads of the character's possessions and of landblock objects
            Vault.Initialize();
            Assert.IsTrue(Vault.Available);
            Assert.AreEqual(1, VaultStore.List(account).Count(r => r.ItemGuid == guid), "still in the Vault");

            var possessed = DatabaseManager.Shard.BaseDatabase.GetPossessedBiotasInParallel(player.Guid.Full);
            Assert.IsFalse(possessed.Inventory.Concat(possessed.WieldedItems).Any(b => b.Id == guid), "the character's load doesn't bring it back");

            // a landblock loads objects that have a location and no container or wielder; the item has none of the three
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type IN ({(int)PropertyInstanceId.Container}, {(int)PropertyInstanceId.Wielder});"));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota_properties_position WHERE object_Id = {guid};"));
        }

        [TestMethod]
        public void Deposit_ForgetsTheObject_AndNeverSavesIt()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, guid).Outcome);

            // the player's own save covers only what they still possess
            VaultTestWorld.OnWorldThread(() => player.SavePlayerToDatabase());
            VaultTestWorld.OnWorldThread(() => { });

            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"));
            Assert.AreEqual(1, VaultStore.List(account).Count(r => r.ItemGuid == guid));
        }

        // ---- one operation at a time, trade window

        [TestMethod]
        public void Vault_SecondOperationBeforeFirstFinishes_IsBusy()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var first = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var second = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));

            VaultResult firstResult = null, secondResult = null;
            var done = new System.Threading.ManualResetEventSlim();

            VaultTestWorld.OnWorldThread(() =>
            {
                Vault.Deposit(player, first.Guid.Full, r => { firstResult = r; done.Set(); });
                Vault.Deposit(player, second.Guid.Full, r => secondResult = r);
            });

            Assert.AreEqual(VaultOutcome.Busy, secondResult.Outcome, secondResult.Message);
            Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)));
            Assert.AreEqual(VaultOutcome.Deposited, firstResult.Outcome);
            Assert.IsNotNull(player.GetInventoryItem(second.Guid.Full), "the refused item stays in the pack");

            // once the first is done the player can go on
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, second.Guid.Full).Outcome);
        }

        [TestMethod]
        public void Deposit_ItemInOpenTradeWindow_IsRefused()
        {
            var carried = NewItem(null);
            carried.player.ItemsInTradeWindow.Add(carried.item.Guid);

            AssertDepositRefused(carried, VaultOutcome.InTrade);
        }

        // ---- commands

        [TestMethod]
        public void VaultCommands_DepositLastAppraised_ListAndWithdrawById()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;
            player.CurrentAppraisalTarget = guid;

            VaultTestWorld.OnWorldThread(() => Command.Handlers.VaultCommands.HandleVault(player.Session, "deposit"));
            VaultTestWorld.WaitUntil(() => VaultStore.Get(guid) != null, "the deposit command's job");
            VaultTestWorld.OnWorldThread(() => { });

            Assert.IsNull(player.GetInventoryItem(guid), "/vault deposit took the last appraised item");
            Assert.AreEqual(1, Vault.List(player).Count(r => r.ItemGuid == guid));

            VaultTestWorld.OnWorldThread(() => Command.Handlers.VaultCommands.HandleVault(player.Session, "list"));
            VaultTestWorld.OnWorldThread(() => Command.Handlers.VaultCommands.HandleVault(player.Session, "withdraw", $"0x{guid:X8}"));
            VaultTestWorld.WaitUntil(() => VaultStore.Get(guid) == null, "the withdraw command's job");
            VaultTestWorld.OnWorldThread(() => { });

            Assert.IsNotNull(player.GetInventoryItem(guid), "/vault withdraw <id> brought it back");
        }

        // ---- helpers

        private static (Player player, uint guid) DepositedItem()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, item.Guid.Full).Outcome);
            return (player, item.Guid.Full);
        }

        private static (Player player, WorldObject item) NewItem(Action<WorldObject> configure, uint wcid = VaultTestWorld.SwordWcid)
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.NewItem(wcid);
            configure?.Invoke(item);
            VaultTestWorld.Give(player, item);
            return (player, item);
        }

        private static (Player player, WorldObject item) NewPackHolding(Action<WorldObject> configureInner)
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var pack = VaultTestWorld.NewItem(VaultTestWorld.PackWcid);
            VaultTestWorld.Give(player, pack);
            var inner = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            configureInner?.Invoke(inner);
            VaultTestWorld.Give(player, inner, (Container)pack);
            return (player, pack);
        }

        private static void AssertDepositRefused((Player player, WorldObject item) carried, VaultOutcome expected)
        {
            var (player, item) = carried;
            var guid = item.Guid.Full;
            var vaultBefore = VaultStore.Count(player.Character.AccountId);

            var result = VaultTestWorld.Deposit(player, guid);

            Assert.AreEqual(expected, result.Outcome, result.Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Message));
            Assert.IsNotNull(player.GetInventoryItem(guid), "the item stays in the pack");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"), "the database still has it in the pack");
            Assert.AreEqual(vaultBefore, VaultStore.Count(player.Character.AccountId), "the Vault is unchanged");
        }

        private static long Count(string sql) => MarketTestDatabase.Scalar(Db, sql);

        private static IDisposable FailInsertsInto(string table)
        {
            var trigger = $"test_fail_{table}";
            MarketTestDatabase.Execute(Db, $"CREATE TRIGGER `{trigger}` BEFORE INSERT ON `{table}` FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'injected failure';");

            return new Cleanup(() => MarketTestDatabase.Execute(Db, $"DROP TRIGGER IF EXISTS `{trigger}`;"));
        }

        private sealed class Cleanup : IDisposable
        {
            private readonly Action action;
            public Cleanup(Action action) => this.action = action;
            public void Dispose() => action();
        }
    }
}
