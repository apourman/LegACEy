using System;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum.Properties;
using ACE.Server.Market;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: custody when other work is already on the save queue. A save queued before a deposit keeps a reference to the item's live biota
    /// and reads it when it runs, so what the deposit does to the live item reaches the database before the deposit job does.
    /// </summary>
    public partial class VaultTests
    {
        [TestMethod]
        public void Deposit_PlayerSaveAlreadyQueued_NeverWritesTheItemOutOfThePackBeforeTheVaultHasIt()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;

            long containerRowsBetween = -1;
            long vaultRowsBetween = -1;
            VaultResult result = null;
            var done = new ManualResetEventSlim();

            using (var gate = HoldSaveQueue())
            {
                VaultTestWorld.OnWorldThread(() =>
                {
                    // a routine player save, queued behind other work: it collects the item, still in the pack
                    item.ChangesDetected = true;
                    player.SavePlayerToDatabase();

                    // what a crash right after that save, before the deposit job, would leave in the database
                    DatabaseManager.Shard.GetCurrentQueueWaitTime(_ =>
                    {
                        containerRowsBetween = ContainerRows(guid);
                        vaultRowsBetween = Count($"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};");
                    });

                    Vault.Deposit(player, guid, r => { result = r; done.Set(); });
                });
            }

            Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "the Vault reported a result");
            Assert.AreEqual(VaultOutcome.Deposited, result.Outcome, result.Message);

            Assert.AreEqual(0, vaultRowsBetween, "the probe ran before the deposit job");
            Assert.AreEqual(1, containerRowsBetween, "between the earlier save and the deposit job the item is still in the pack, not ownerless (the startup purge deletes ownerless items)");

            Assert.AreEqual(0, ContainerRows(guid), "the deposit took it out of the pack");
            Assert.AreEqual(1, VaultStore.Count(account));
        }

        [TestMethod]
        public void Deposit_FailsWithAPlayerSaveAlreadyQueued_DatabaseStillHasTheItemInThePack()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;

            // the player is gone by the time the job fails, so the object is discarded and only the database keeps the item
            player.IsLoggingOut = true;

            VaultResult result = null;
            var done = new ManualResetEventSlim();

            using (FailInsertsInto("market_item_event"))
            {
                using (var gate = HoldSaveQueue())
                {
                    VaultTestWorld.OnWorldThread(() =>
                    {
                        item.ChangesDetected = true;
                        player.SavePlayerToDatabase();

                        Vault.Deposit(player, guid, r => { result = r; done.Set(); });
                    });
                }

                Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "the Vault reported a result");
            }

            Assert.AreEqual(VaultOutcome.SaveFailed, result.Outcome, result.Message);
            Assert.AreEqual(1, ContainerRows(guid), "the database still has the item in the pack, so it is there at the next login");
            Assert.AreEqual(0, VaultStore.Count(account));
        }

        [TestMethod]
        public void Deposit_SaveFails_ReturnedItemKeepsItsCastOnEnchantments()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            item.Biota.PropertiesEnchantmentRegistry ??= new System.Collections.Generic.List<ACE.Entity.Models.PropertiesEnchantmentRegistry>();
            item.Biota.PropertiesEnchantmentRegistry.Add(new ACE.Entity.Models.PropertiesEnchantmentRegistry { SpellId = 1234, LayerId = 1, CasterObjectId = 0x50000099, Duration = 1800, PowerLevel = 6, StatModType = ACE.Entity.Enum.EnchantmentTypeFlags.Float | ACE.Entity.Enum.EnchantmentTypeFlags.SingleStat, StatModKey = 1, StatModValue = 1.5f });
            item.ChangesDetected = true;
            VaultTestWorld.Give(player, item);
            var guid = item.Guid.Full;

            VaultResult result;
            using (FailInsertsInto("market_item_event"))
                result = VaultTestWorld.Deposit(player, guid);

            Assert.AreEqual(VaultOutcome.SaveFailed, result.Outcome, result.Message);
            Assert.HasCount(1, player.GetInventoryItem(guid).Biota.PropertiesEnchantmentRegistry, "a deposit that didn't happen takes nothing off the item");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM biota_properties_enchantment_registry WHERE object_Id = {guid};"));
        }

        [TestMethod]
        public void Withdraw_AccountBannedWhileTheJobWaits_IsRefusedAndTheItemStaysInTheVault()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, guid).Outcome);

            MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"INSERT INTO account (accountId, accountName, passwordHash, passwordSalt, accessLevel) VALUES ({account}, 'vaultban{account}', 'x', 'use bcrypt', 0);");

            VaultResult result = null;
            var done = new ManualResetEventSlim();

            using (var gate = HoldSaveQueue())
            {
                VaultTestWorld.OnWorldThread(() => Vault.Withdraw(player, guid, r => { result = r; done.Set(); }));

                // the ban lands after the withdrawal passed its checks, while its job waits on the queue (an admin's SQL, or a ban the game hasn't booted for yet)
                MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"UPDATE account SET banned_Time = UTC_TIMESTAMP(), ban_Expire_Time = UTC_TIMESTAMP() + INTERVAL 1 DAY WHERE accountId = {account};");
            }

            Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "the Vault reported a result");
            Assert.AreEqual(VaultOutcome.Banned, result.Outcome, result.Message);
            Assert.AreEqual("banned", GameBridge.ResultCode(result.Outcome));
            Assert.IsNull(player.GetInventoryItem(guid));
            Assert.AreEqual(0, ContainerRows(guid), "the item is still in escrow");
            Assert.AreEqual(1, VaultStore.Count(account));
        }

        private static long ContainerRows(uint guid) => Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};");

        /// <summary>
        /// Blocks the save queue until disposed: work queued meanwhile waits behind it, in order
        /// </summary>
        private static IDisposable HoldSaveQueue()
        {
            var gate = new ManualResetEventSlim();

            DatabaseManager.Shard.GetCurrentQueueWaitTime(_ => gate.Wait(TimeSpan.FromSeconds(60)));

            return new Release(gate);
        }

        private sealed class Release : IDisposable
        {
            private readonly ManualResetEventSlim gate;
            public Release(ManualResetEventSlim gate) => this.gate = gate;
            public void Dispose() => gate.Set();
        }
    }
}
