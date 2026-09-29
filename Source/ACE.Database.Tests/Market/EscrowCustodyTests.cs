using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;

using ACE.Common;
using ACE.Database.Adapter;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

using EntityBiota = ACE.Entity.Models.Biota;
using PopulatedCollectionFlags = ACE.Database.ShardDatabase.PopulatedCollectionFlags;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Seam 3: escrow custody. The deposit and withdraw save-queue jobs, the cache eviction they rely on, and the orphan purge's Vault rule.
    /// ShardDatabase and the purge build their contexts from config, so the configured shard database points at a scratch one for this class.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class EscrowCustodyTests
    {
        private const string Db = "ace_shard_market_escrow";

        private const uint AccountId = 7;
        private const uint CharacterId = 0x50000001;
        private const uint OtherCharacterId = 0x50000002;

        private const int GenericWeenieType = (int)WeenieType.Generic;

        private static string originalShardDatabase;

        private static uint nextGuid = 0x80001000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;

            originalShardDatabase = ConfigManager.Config.MySql.Shard.Database;
            ConfigManager.Config.MySql.Shard.Database = Db;

            // the characters items are deposited from and withdrawn to, so the purge sees their containers
            SeedCharacter(CharacterId);
            SeedCharacter(OtherCharacterId);
        }

        [ClassCleanup]
        public static void TestCleanup()
        {
            ConfigManager.Config.MySql.Shard.Database = originalShardDatabase;

            MarketTestDatabase.Drop(Db);
        }

        [TestMethod]
        public void Deposit_Succeeds_SavesItemChangeVaultRowAndEventTogether()
        {
            var guid = SeedPackItem();
            var shardDb = NewCachingDatabase();

            var item = LoadEntity(guid);
            RemoveFromPack(item);

            Assert.IsTrue(shardDb.DepositToVault(item, new ReaderWriterLockSlim(), NewVaultItem(guid)));

            Assert.AreEqual(0, ContainerRows(guid));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid} AND account_Id = {AccountId} AND character_Id = {CharacterId} AND state = '{VaultItemState.Held}';"));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {guid} AND account_Id = {AccountId} AND character_Id = {CharacterId} AND kind = '{ItemEventKind.Deposit}';"));
        }

        [TestMethod]
        public void Deposit_FailureDuringSave_SavesNeither()
        {
            var guid = SeedPackItem();
            var shardDb = NewCachingDatabase();

            var item = LoadEntity(guid);
            RemoveFromPack(item);

            bool result;
            using (FailInsertsInto("market_item_event"))
                result = shardDb.DepositToVault(item, new ReaderWriterLockSlim(), NewVaultItem(guid));

            Assert.IsFalse(result);
            Assert.AreEqual(1, ContainerRows(guid), "the item must still be in the pack");
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {guid};"));

            // with the failure gone, the same deposit goes through
            Assert.IsTrue(shardDb.DepositToVault(item, new ReaderWriterLockSlim(), NewVaultItem(guid)));
            Assert.AreEqual(0, ContainerRows(guid));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
        }

        [TestMethod]
        public void Deposit_ItemStillInPack_IsRefusedAndSavesNothing()
        {
            var guid = SeedPackItem();
            var shardDb = NewCachingDatabase();

            var item = LoadEntity(guid); // container not cleared

            Assert.IsFalse(shardDb.DepositToVault(item, new ReaderWriterLockSlim(), NewVaultItem(guid)));

            Assert.AreEqual(1, ContainerRows(guid));
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {guid};"));
        }

        [TestMethod]
        public void Withdraw_Succeeds_SavesItemChangeRowRemovalAndEventTogether()
        {
            var guid = SeedVaultItem(rowVersion: 3);
            var shardDb = NewCachingDatabase();

            var item = LoadEntity(guid);
            PutInPack(item, OtherCharacterId);

            Assert.IsTrue(shardDb.WithdrawFromVault(item, new ReaderWriterLockSlim(), AccountId, OtherCharacterId, expectedRowVersion: 3));

            Assert.AreEqual(OtherCharacterId, (uint)MarketTestDatabase.Scalar(Db, $"SELECT value FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"));
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {guid} AND account_Id = {AccountId} AND character_Id = {OtherCharacterId} AND kind = '{ItemEventKind.Withdraw}';"));
        }

        [TestMethod]
        public void Withdraw_FailureDuringSave_SavesNeither()
        {
            var guid = SeedVaultItem(rowVersion: 0);
            var shardDb = NewCachingDatabase();

            var item = LoadEntity(guid);
            PutInPack(item, CharacterId);

            bool result;
            using (FailInsertsInto("market_item_event"))
                result = shardDb.WithdrawFromVault(item, new ReaderWriterLockSlim(), AccountId, CharacterId, expectedRowVersion: 0);

            Assert.IsFalse(result);
            AssertStillInEscrow(guid);
        }

        [TestMethod]
        public void Withdraw_ListedOtherAccountChangedRowOrNoRow_IsRefusedAndSavesNothing()
        {
            var shardDb = NewCachingDatabase();

            // listed: must be delisted first
            var listed = SeedVaultItem(rowVersion: 0, state: VaultItemState.Listed);
            var item = LoadEntity(listed);
            PutInPack(item, CharacterId);
            Assert.IsFalse(shardDb.WithdrawFromVault(item, new ReaderWriterLockSlim(), AccountId, CharacterId, expectedRowVersion: 0));
            AssertStillInEscrow(listed);

            // another account's item
            var held = SeedVaultItem(rowVersion: 0);
            item = LoadEntity(held);
            PutInPack(item, CharacterId);
            Assert.IsFalse(shardDb.WithdrawFromVault(item, new ReaderWriterLockSlim(), AccountId + 1, CharacterId, expectedRowVersion: 0));
            AssertStillInEscrow(held);

            // the row changed since the caller read it
            Assert.IsFalse(shardDb.WithdrawFromVault(item, new ReaderWriterLockSlim(), AccountId, CharacterId, expectedRowVersion: 1));
            AssertStillInEscrow(held);

            // not pointed at a container
            item = LoadEntity(held);
            Assert.IsFalse(shardDb.WithdrawFromVault(item, new ReaderWriterLockSlim(), AccountId, CharacterId, expectedRowVersion: 0));
            AssertStillInEscrow(held);

            // no Vault row at all
            var loose = SeedPackItem();
            item = LoadEntity(loose);
            Assert.IsFalse(shardDb.WithdrawFromVault(item, new ReaderWriterLockSlim(), AccountId, CharacterId, expectedRowVersion: 0));
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {loose};"));
        }

        [TestMethod]
        public void Deposit_EvictsCachedCopy_OldCopyCannotRestoreContainer()
        {
            var guid = SeedPackItem();
            var shardDb = NewCachingDatabase();

            // the game loads the item through the cache: the cache now holds this copy and the context it was loaded with
            var oldContext = new ShardDbContext();
            var cachedCopy = shardDb.GetBiota(oldContext, guid);
            Assert.Contains(guid, shardDb.GetBiotaCacheKeys());
            Assert.AreEqual(CharacterId, cachedCopy.GetProperty(PropertyInstanceId.Container));

            var item = BiotaConverter.ConvertToEntityBiota(cachedCopy);
            RemoveFromPack(item);
            Assert.IsTrue(shardDb.DepositToVault(item, new ReaderWriterLockSlim(), NewVaultItem(guid)));

            // the entry is gone and its context is disposed: nothing can be written back through the old copy
            Assert.DoesNotContain(guid, shardDb.GetBiotaCacheKeys());
            Assert.ThrowsExactly<ObjectDisposedException>(() => oldContext.SaveChanges());

            // a later load sees the escrowed item, not the old copy, and saving what it sees keeps it out of the pack
            var reloaded = shardDb.GetBiota(guid);
            Assert.AreNotSame(cachedCopy, reloaded);
            Assert.IsNull(reloaded.GetProperty(PropertyInstanceId.Container));

            var later = BiotaConverter.ConvertToEntityBiota(reloaded);
            later.PropertiesInt[PropertyInt.Value] = 999;
            Assert.IsTrue(shardDb.SaveBiota(later, new ReaderWriterLockSlim()));

            Assert.AreEqual(0, ContainerRows(guid));
            Assert.AreEqual(999, MarketTestDatabase.Scalar(Db, $"SELECT value FROM biota_properties_int WHERE object_Id = {guid} AND type = {(int)PropertyInt.Value};"));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
        }

        [TestMethod]
        public void EvictBiota_UncachedGuid_ReturnsFalse()
        {
            var shardDb = NewCachingDatabase();

            Assert.IsFalse(shardDb.EvictBiota(0x80FFFFFF));
        }

        [TestMethod]
        public void Deposit_EnchantmentsRemovedInMemory_AreDeletedFromDatabase()
        {
            var guid = SeedPackItem();

            // two spells a mage (another player) cast onto the item
            MarketTestDatabase.Execute(Db, "INSERT INTO biota_properties_enchantment_registry (object_Id, enchantment_Category, spell_Id, layer_Id, has_Spell_Set_Id, spell_Category, power_Level, start_Time, duration, caster_Object_Id, degrade_Modifier, degrade_Limit, last_Time_Degraded, stat_Mod_Type, stat_Mod_Key, stat_Mod_Value, spell_Set_Id) VALUES " +
                $"({guid}, 1, 1483, 1, 0, 1, 300, 0, 3600, {OtherCharacterId}, 0, -666, 0, 0, 0, 1.5, 0), " +
                $"({guid}, 1, 1616, 1, 0, 2, 300, 0, 3600, {OtherCharacterId}, 0, -666, 0, 0, 0, 1.5, 0);");
            MarketTestDatabase.Execute(Db, $"UPDATE biota SET populated_Collection_Flags = populated_Collection_Flags | {(uint)PopulatedCollectionFlags.BiotaPropertiesEnchantmentRegistry} WHERE id = {guid};");

            var shardDb = NewCachingDatabase();

            var item = LoadEntity(guid);
            Assert.HasCount(2, item.PropertiesEnchantmentRegistry);
            item.PropertiesEnchantmentRegistry.Clear();
            RemoveFromPack(item);

            Assert.IsTrue(shardDb.DepositToVault(item, new ReaderWriterLockSlim(), NewVaultItem(guid)));

            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM biota_properties_enchantment_registry WHERE object_Id = {guid};"));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
        }

        [TestMethod]
        public void OrphanPurge_ItemWithVaultRow_IsKeptAndNoErrorLogged()
        {
            var escrowed = SeedVaultItem(rowVersion: 0);

            var errors = RunOrphanPurge();

            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM biota WHERE id = {escrowed};"));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {escrowed};"));

            var mentions = errors.Where(e => e.Contains($"{escrowed:X8}")).ToList();
            Assert.IsEmpty(mentions, string.Join(Environment.NewLine, mentions));
        }

        [TestMethod]
        public void OrphanPurge_OwnerlessItemWithoutVaultRow_IsPurged()
        {
            var escrowed = SeedVaultItem(rowVersion: 0);
            var inPack = SeedPackItem();
            var ownerless = SeedItem(containerId: null);

            RunOrphanPurge();

            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM biota WHERE id = {ownerless};"));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM biota WHERE id = {inPack};"));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM biota WHERE id = {escrowed};"));
        }

        [TestMethod]
        public void OrphanPurge_NoMarketTables_StillPurgesOwnerlessItems()
        {
            const string baseOnlyDb = "ace_shard_market_escrow_base";
            const uint ownerless = 0x80002001;

            MarketTestDatabase.CreateFromBase(baseOnlyDb);
            ConfigManager.Config.MySql.Shard.Database = baseOnlyDb;
            try
            {
                MarketTestDatabase.Execute(baseOnlyDb, $"INSERT INTO biota (id, weenie_Class_Id, weenie_Type) VALUES ({ownerless}, 3000, {GenericWeenieType});");

                using (var context = MarketTestDatabase.CreateContext(baseOnlyDb))
                    ShardDatabaseOfflineTools.PurgeOrphanedBiotasInParallel(context, out _);

                Assert.AreEqual(0, MarketTestDatabase.Scalar(baseOnlyDb, $"SELECT COUNT(*) FROM biota WHERE id = {ownerless};"));
            }
            finally
            {
                ConfigManager.Config.MySql.Shard.Database = Db;
                MarketTestDatabase.Drop(baseOnlyDb);
            }
        }

        // ---- helpers ----

        private static ShardDatabaseWithCaching NewCachingDatabase() => new ShardDatabaseWithCaching(TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(11));

        private static void SeedCharacter(uint id)
        {
            MarketTestDatabase.Execute(Db, $"INSERT INTO biota (id, weenie_Class_Id, weenie_Type, populated_Collection_Flags) VALUES ({id}, 1, {(int)WeenieType.Creature}, {(uint)PopulatedCollectionFlags.BiotaPropertiesPosition});");
            // a location, as every character has, so the purge keeps it
            MarketTestDatabase.Execute(Db, $"INSERT INTO biota_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES ({id}, {(int)PositionType.Location}, 0x7D64000D, 0, 0, 0, 1, 0, 0, 0);");
            MarketTestDatabase.Execute(Db, $"INSERT INTO `character` (id, account_Id, name, is_Plussed, is_Deleted, delete_Time, last_Login_Timestamp, total_Logins, character_Options_1, character_Options_2, spellbook_Filters, hair_Texture, default_Hair_Texture) VALUES ({id}, {AccountId}, 'Escrow{id:X}', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);");
        }

        /// <summary>
        /// An item row with a name and value, optionally in a container. Returns its GUID.
        /// </summary>
        private static uint SeedItem(uint? containerId)
        {
            var guid = nextGuid++;

            var flags = PopulatedCollectionFlags.BiotaPropertiesInt | PopulatedCollectionFlags.BiotaPropertiesString;
            if (containerId.HasValue)
                flags |= PopulatedCollectionFlags.BiotaPropertiesIID;

            MarketTestDatabase.Execute(Db, $"INSERT INTO biota (id, weenie_Class_Id, weenie_Type, populated_Collection_Flags) VALUES ({guid}, 3000, {GenericWeenieType}, {(uint)flags});");
            MarketTestDatabase.Execute(Db, $"INSERT INTO biota_properties_int (object_Id, type, value) VALUES ({guid}, {(int)PropertyInt.Value}, 100);");
            MarketTestDatabase.Execute(Db, $"INSERT INTO biota_properties_string (object_Id, type, value) VALUES ({guid}, {(int)PropertyString.Name}, 'Escrow Test Item');");

            if (containerId.HasValue)
                MarketTestDatabase.Execute(Db, $"INSERT INTO biota_properties_i_i_d (object_Id, type, value) VALUES ({guid}, {(int)PropertyInstanceId.Container}, {containerId.Value});");

            return guid;
        }

        private static uint SeedPackItem() => SeedItem(CharacterId);

        /// <summary>
        /// An item already in escrow: no container, with a Vault row
        /// </summary>
        private static uint SeedVaultItem(uint rowVersion, string state = VaultItemState.Held)
        {
            var guid = SeedItem(containerId: null);

            MarketTestDatabase.Execute(Db, $"INSERT INTO market_vault_item (item_Guid, account_Id, character_Id, state, deposited_Time, row_Version, wcid, name, item_Type) VALUES ({guid}, {AccountId}, {CharacterId}, '{state}', UTC_TIMESTAMP(6), {rowVersion}, 3000, 'Escrow Test Item', 1);");

            return guid;
        }

        private static VaultItem NewVaultItem(uint guid)
        {
            return new VaultItem
            {
                ItemGuid = guid,
                AccountId = AccountId,
                CharacterId = CharacterId,
                State = VaultItemState.Held,
                Wcid = 3000,
                Name = "Escrow Test Item",
                ItemType = 1,
                StackSize = 1,
                Value = 100,
            };
        }

        /// <summary>
        /// The item as the world thread holds it: a plain copy of the stored item
        /// </summary>
        private static EntityBiota LoadEntity(uint guid)
        {
            var biota = new ShardDatabase().GetBiota(guid);

            return BiotaConverter.ConvertToEntityBiota(biota);
        }

        /// <summary>
        /// What Container.TryRemoveFromInventory does in memory
        /// </summary>
        private static void RemoveFromPack(EntityBiota item)
        {
            item.PropertiesIID?.Remove(PropertyInstanceId.Container);
            item.PropertiesInt?.Remove(PropertyInt.PlacementPosition);
        }

        private static void PutInPack(EntityBiota item, uint containerId)
        {
            item.PropertiesIID ??= new Dictionary<PropertyInstanceId, uint>();
            item.PropertiesIID[PropertyInstanceId.Container] = containerId;
        }

        private static long ContainerRows(uint guid) => MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};");

        private static void AssertStillInEscrow(uint guid)
        {
            Assert.AreEqual(0, ContainerRows(guid), "the item must still be in escrow");
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {guid};"));
        }

        /// <summary>
        /// Makes every insert into the table fail inside the save (a test trigger) until disposed
        /// </summary>
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

        /// <summary>
        /// Runs the startup orphan purge on the scratch shard and returns the error messages it logged
        /// </summary>
        private static List<string> RunOrphanPurge()
        {
            var appender = new MemoryAppender();
            var repository = LogManager.GetRepository(typeof(ShardDatabaseOfflineTools).Assembly);
            BasicConfigurator.Configure(repository, appender);

            try
            {
                using var context = MarketTestDatabase.CreateContext(Db);
                ShardDatabaseOfflineTools.PurgeOrphanedBiotasInParallel(context, out _);
            }
            finally
            {
                repository.ResetConfiguration();
            }

            return appender.GetEvents().Where(e => e.Level >= Level.Error).Select(e => e.RenderedMessage).ToList();
        }
    }
}
