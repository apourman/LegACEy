using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database;
using ACE.Database.Tests.Market;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.Network;
using ACE.Server.Network.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// A started world for seam 2: the real database manager (shard on a scratch database), property and GUID managers, and the world thread,
    /// with test players that have a session but no socket.
    /// </summary>
    internal static class VaultTestWorld
    {
        public const string Db = "ace_shard_market_vault";

        // world database weenies
        public const uint SwordWcid = 12758; // swordacademy
        public const uint PackWcid = 136; // backpack, a side pack
        public const uint PetDeviceWcid = 48886; // petdevicegolemmud

        private static string originalShardDatabase;

        private static uint nextAccountId = 900000;

        public static void Start()
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;

            originalShardDatabase = ConfigManager.Config.MySql.Shard.Database;
            ConfigManager.Config.MySql.Shard.Database = Db;
            ConfigManager.Config.Server.LandblockPreloading = false;

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); // as Program.Main does, for the DAT strings

            DatManager.Initialize(ConfigManager.Config.Server.DatFilesDirectory, true);

            DatabaseManager.Initialize();
            Assert.IsFalse(DatabaseManager.InitializationFailure);
            DatabaseManager.Start();

            PropertyManager.Initialize();
            GuidManager.Initialize();

            PlayerManager.Initialize();
            HouseManager.Initialize();

            WorldManager.Initialize();

            Vault.Initialize();
            Assert.IsTrue(Vault.Available, "the scratch shard has the market tables");
        }

        public static void Stop()
        {
            WorldManager.StopWorld();
            DatabaseManager.Stop();

            ConfigManager.Config.MySql.Shard.Database = originalShardDatabase;

            MarketTestDatabase.Drop(Db);
        }

        public static uint NewAccountId() => Interlocked.Increment(ref nextAccountId);

        /// <summary>
        /// A player with a session that has no socket, so everything sent to the client is just queued
        /// </summary>
        public static Player NewPlayer(uint accountId)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie("human");
            var template = new Player(weenie, GuidManager.NewPlayerGuid(), accountId);

            // a session takes its matching listener from the socket manager, which isn't started here: give it an empty list
            typeof(SocketManager).GetField("listeners", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, new ConnectionListener[0]);

            var session = new Session(null, new IPEndPoint(IPAddress.Loopback, 0), 1, 1);

            var player = new Player(template.Biota, new List<ACE.Database.Models.Shard.Biota>(), new List<ACE.Database.Models.Shard.Biota>(), template.Character, session);

            // login does this; commands find the player through the session
            typeof(Session).GetProperty(nameof(Session.Player)).SetMethod.Invoke(session, new object[] { player });

            return player;
        }

        public static WorldObject NewItem(uint wcid)
        {
            return WorldObjectFactory.CreateNewWorldObject(wcid);
        }

        /// <summary>
        /// Puts the item in the player's pack and saves it, as if the player had carried it since a save
        /// </summary>
        public static WorldObject Give(Player player, WorldObject item, Container into = null)
        {
            Assert.IsTrue((into ?? player).TryAddToInventory(item, out _), "the pack has room for the test item");

            Save(item);

            return item;
        }

        public static void Save(WorldObject item)
        {
            var done = new ManualResetEventSlim();
            var ok = false;

            item.SaveBiotaToDatabase(false);
            DatabaseManager.Shard.SaveBiota(item.Biota, item.BiotaDatabaseLock, success => { ok = success; done.Set(); });

            Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "the save queue ran the save");
            Assert.IsTrue(ok, "the test item was saved");
        }

        public static void OnWorldThread(Action action)
        {
            Exception failure = null;
            var done = new ManualResetEventSlim();

            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            }));

            Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "the world thread ran the action");

            if (failure != null)
                throw new AssertFailedException("the action failed on the world thread: " + failure, failure);
        }

        /// <summary>
        /// Waits for a condition that a command's queued database job makes true
        /// </summary>
        public static void WaitUntil(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);

            while (!condition())
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, "timed out waiting for " + what);
                Thread.Sleep(20);
            }
        }

        public static VaultResult Deposit(Player player, uint itemGuid) => Run(player, itemGuid, Vault.Deposit);

        public static VaultResult Withdraw(Player player, uint itemGuid) => Run(player, itemGuid, Vault.Withdraw);

        private static VaultResult Run(Player player, uint itemGuid, Action<Player, uint, Action<VaultResult>> entryPoint)
        {
            VaultResult result = null;
            var done = new ManualResetEventSlim();

            OnWorldThread(() => entryPoint(player, itemGuid, r => { result = r; done.Set(); }));

            Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "the Vault reported a result");

            return result;
        }
    }
}
