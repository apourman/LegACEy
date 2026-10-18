using System.Collections.Generic;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database.Adapter;
using ACE.Database.Tests.Market;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

using EntityBiota = ACE.Entity.Models.Biota;

namespace ACE.Database.Tests
{
    /// <summary>
    /// A static object in a landblock copy keeps its 32-bit guid in the low half of its id and the copy's instance in the high half.
    /// The scratch shard is built the way a fresh server builds it: the base script, then every shard update, the instancing migration included.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class InstancedIdStorageTests
    {
        private const string Db = "ace_shard_instanced_ids";

        private const uint Instance = 5;

        // a static in landblock 0xAABB, and a chest beside it that the first object sits in
        private static readonly ulong StaticId = new ObjectGuid(0x7AABB001, Instance).Full;
        private static readonly ulong ChestId = new ObjectGuid(0x7AABB002, Instance).Full;

        private static string originalShardDatabase;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            Assert.AreEqual(0, failures.Count, string.Join("; ", failures.Keys));

            originalShardDatabase = ConfigManager.Config.MySql.Shard.Database;
            ConfigManager.Config.MySql.Shard.Database = Db;
        }

        [ClassCleanup]
        public static void TestCleanup()
        {
            ConfigManager.Config.MySql.Shard.Database = originalShardDatabase;

            MarketTestDatabase.Drop(Db);
        }

        [TestMethod]
        public void InstancedStatic_SavesAndReloadsWithItsFullIdAndInstance()
        {
            Assert.IsTrue(new ShardDatabase().SaveBiota(NewBiota(StaticId, ChestId, Instance), new ReaderWriterLockSlim()));
            Assert.IsTrue(new ShardDatabase().SaveBiota(NewBiota(new ObjectGuid(0x7AABB003, 0).Full, null, null), new ReaderWriterLockSlim()));

            var reloaded = BiotaConverter.ConvertToEntityBiota(new ShardDatabase().GetBiota(StaticId));

            Assert.AreEqual(StaticId, reloaded.Id);
            Assert.AreEqual(ChestId, reloaded.PropertiesIID[PropertyInstanceId.Container]);
            Assert.AreEqual(Instance, reloaded.PropertiesPosition[PositionType.Location].Instance);
            Assert.AreEqual(Instance, reloaded.GetPosition(PositionType.Location, new ReaderWriterLockSlim()).Instance);

            // a base-world row keeps a NULL instance
            var baseWorld = BiotaConverter.ConvertToEntityBiota(new ShardDatabase().GetBiota(0x7AABB003u));
            Assert.IsNull(baseWorld.PropertiesPosition[PositionType.Location].Instance);
            Assert.AreEqual("NULL", MarketTestDatabase.Rows(Db, "SELECT instance FROM biota_properties_position WHERE object_Id = 0x7AABB003;")[0]);
        }

        [TestMethod]
        public void InstancedStatic_IsNeitherADynamicNorASequenceGapNorABaseStatic()
        {
            var instanced = new ObjectGuid(0x7AABB010, Instance).Full;
            Assert.IsTrue(new ShardDatabase().SaveBiota(NewBiota(instanced, null, Instance), new ReaderWriterLockSlim()));

            // two dynamics, so the only gap below DynamicMax is between them
            Assert.IsTrue(new ShardDatabase().SaveBiota(NewBiota(0x80000001, null, null), new ReaderWriterLockSlim()));
            Assert.IsTrue(new ShardDatabase().SaveBiota(NewBiota(0x80000005, null, null), new ReaderWriterLockSlim()));

            var shard = new ShardDatabase();

            CollectionAssert.AreEqual(new List<(uint, uint)> { (0x80000002, 0x80000004) }, shard.GetSequenceGaps(ObjectGuid.DynamicMin, uint.MaxValue));
            Assert.AreEqual(0x80000005u, shard.GetMaxGuidFoundInRange(ObjectGuid.DynamicMin, ObjectGuid.DynamicMax));
            Assert.IsFalse(shard.GetDynamicObjectsByLandblock(0xAABB, Instance).Exists(b => b.Id == instanced));
            Assert.IsFalse(shard.GetStaticObjectsByLandblock(0xAABB, 0).Exists(b => b.Id == instanced), "the base world doesn't load another copy's static");
            Assert.IsTrue(shard.GetStaticObjectsByLandblock(0xAABB, Instance).Exists(b => b.Id == instanced), "its own copy does");
        }

        private static EntityBiota NewBiota(ulong id, ulong? containerId, uint? instance)
        {
            var biota = new EntityBiota
            {
                Id = id,
                WeenieClassId = 3000,
                WeenieType = WeenieType.Generic,
                PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>
                {
                    [PositionType.Location] = new PropertiesPosition { ObjCellId = 0xAABB0001, Instance = instance, PositionX = 10, PositionY = 20, PositionZ = 0, RotationW = 1 },
                },
            };

            if (containerId.HasValue)
                biota.PropertiesIID = new Dictionary<PropertyInstanceId, ulong> { [PropertyInstanceId.Container] = containerId.Value };

            return biota;
        }
    }
}
