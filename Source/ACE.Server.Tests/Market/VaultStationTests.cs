using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.ClientChannel;
using ACE.Server.Entity;
using ACE.Server.Market;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Tests.ClientChannel;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ClientChannel.ChannelTestClient;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Station sessions, in the Vault's test world (its lifecycle is per class, so these share the Vault class): a player uses a station object,
    /// the session opens and closes with its pushes, and gated channel actions check it.
    /// </summary>
    public partial class VaultTests
    {
        // the linkable item generator: a generic object, the class a station is hooked on
        private const uint GenericWcid = 4142;

        private const string VaultStation = VaultChannelActions.Station;

        private const string GatedAction = "test.gated";

        // stands in for the landblock a station is in: the station code only checks that it isn't null, and building a real one reads terrain the test doesn't need
        private static readonly Landblock stationLandblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        [TestMethod]
        public void Use_StartsTheSession_AndPushesStationOpen()
        {
            var player = ConnectedPlayer();
            var station = NewStation(player, VaultStation);

            Use(player, station);

            Assert.IsTrue(player.HasStation(VaultStation));
            AssertStationPush(WaitForPushes(player, 1).Single(), StationChannelActions.Open, VaultStation, station);
        }

        [TestMethod]
        public void UsingAnotherStation_ClosesTheFirst_ThenOpensTheSecond()
        {
            var player = ConnectedPlayer();
            var first = NewStation(player, VaultStation);
            var second = NewStation(player, "other");
            Use(player, first);
            WaitForPushes(player, 1);

            Use(player, second);

            var pushes = WaitForPushes(player, 2);
            AssertStationPush(pushes[0], StationChannelActions.Close, VaultStation, first);
            AssertStationPush(pushes[1], StationChannelActions.Open, "other", second);
            Assert.IsFalse(player.HasStation(VaultStation));
            Assert.IsTrue(player.HasStation("other"));
        }

        [TestMethod]
        public void UsingTheSameStationAgain_PushesOpenAgain_WithoutClose()
        {
            var player = ConnectedPlayer();
            var station = NewStation(player, VaultStation);
            Use(player, station);
            WaitForPushes(player, 1);

            Use(player, station);

            // a close would be queued before the open, so the first push back is the open
            AssertStationPush(WaitForPushes(player, 1).First(), StationChannelActions.Open, VaultStation, station);
            Assert.IsTrue(player.HasStation(VaultStation));
        }

        [TestMethod]
        public void StationLeave_EndsTheSession_AndPushesStationClose()
        {
            var player = ConnectedPlayer();
            var station = NewStation(player, VaultStation);
            Use(player, station);
            WaitForPushes(player, 1);

            var id = Send(player, StationChannelActions.Leave, Array.Empty<byte>());
            var seen = WaitForEvents(player, e => e.Kind == ChannelEventKind.Reply && e.RequestId == id);

            Assert.AreEqual(ChannelStatus.Ok, seen.Last().Status);
            Assert.IsFalse(player.HasStation(VaultStation));
            AssertStationPush(seen.Single(e => e.Kind == ChannelEventKind.Push), StationChannelActions.Close, VaultStation, station);
        }

        [TestMethod]
        [DataRow("range")]
        [DataRow("station removed")]
        [DataRow("teleport")]
        [DataRow("logout")]
        [DataRow("death")]
        public void Session_EndsWithStationClose_When(string trigger)
        {
            var player = ConnectedPlayer();
            var station = NewStation(player, VaultStation);
            Use(player, station);
            WaitForPushes(player, 1);

            switch (trigger)
            {
                case "range":
                    MoveAway(player, 10f);
                    VaultTestWorld.OnWorldThread(() => player.CheckStationRange());
                    break;
                case "station removed":
                    VaultTestWorld.OnWorldThread(() =>
                    {
                        station.CurrentLandblock = null;
                        player.CheckStationRange();
                    });
                    break;
                case "teleport":
                    VaultTestWorld.OnWorldThread(() => player.Teleport(Away(player, 10f)));
                    break;
                case "logout":
                    VaultTestWorld.OnWorldThread(() => player.LogOut());
                    break;
                case "death":
                    VaultTestWorld.OnWorldThread(() => player.Die());
                    break;
            }

            AssertStationPush(WaitForPushes(player, 1).Single(), StationChannelActions.Close, VaultStation, station);
            Assert.IsFalse(player.HasStation(VaultStation));
        }

        [TestMethod]
        public void CheckStationRange_KeepsTheSession_WhileThePlayerIsAtTheStation()
        {
            var player = ConnectedPlayer();
            var station = NewStation(player, VaultStation);
            Use(player, station);
            WaitForPushes(player, 1);

            VaultTestWorld.OnWorldThread(() => player.CheckStationRange());

            Assert.IsTrue(player.HasStation(VaultStation));
        }

        [TestMethod]
        public void GatedAction_IsRefusedWithoutTheStation_AndRunsWithIt()
        {
            ServerChannel.Register(GatedAction, context => context.Reply(new byte[] { 7 }), "gate");
            var player = ConnectedPlayer();

            var refused = Request(player, GatedAction, Array.Empty<byte>());
            Assert.AreEqual(ChannelStatus.NoStation, refused.Status);
            Assert.AreEqual("Use the gate station to do that.", Text(refused.Body));

            Use(player, NewStation(player, "other"));
            WaitForPushes(player, 1);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, GatedAction, Array.Empty<byte>()).Status, "a session at another station isn't enough");

            Use(player, NewStation(player, "gate"));
            var ran = Request(player, GatedAction, Array.Empty<byte>());
            Assert.AreEqual(ChannelStatus.Ok, ran.Status, Text(ran.Body));
            CollectionAssert.AreEqual(new byte[] { 7 }, ran.Body);
        }

        [TestMethod]
        public void VaultActions_NeedTheVaultStation_AndRunWithIt()
        {
            var player = ConnectedPlayer();
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            // an item id and an index: the body every vault action reads the start of
            var body = ChannelWire.Body(w => { w.Write(item.Guid.Full); w.Write(0); });
            var actions = new[] { VaultChannelActions.List, VaultChannelActions.Deposit, VaultChannelActions.Withdraw, VaultChannelActions.Check, VaultChannelActions.Move };

            foreach (var action in actions)
                Assert.AreEqual(ChannelStatus.NoStation, Request(player, action, body).Status, action);
            Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full), "a refused deposit moved nothing");
            Assert.IsFalse(player.IsVaultChannelling, "a refused deposit started no channel");

            using (ChannelSeconds(1))
            {
                AtTheVault(player);
                foreach (var action in actions)
                {
                    // vault.list takes a page request; an empty body is the first page
                    var reply = Request(player, action, action == VaultChannelActions.List ? Array.Empty<byte>() : body);
                    Assert.AreEqual(ChannelStatus.Ok, reply.Status, $"{action}: {Text(reply.Body)}");
                }

                // the deposit above started a channel: wait for its outcome, so its save isn't still running when the test's database goes
                WaitForEvent(player, e => e.Kind == ChannelEventKind.Push && e.Topic == VaultChannelActions.Changed);
            }
        }

        /// <summary>
        /// A player whose client has used the channel, so it is sent pushes
        /// </summary>
        private static Player ConnectedPlayer()
        {
            ChannelHelloActions.Register();
            StationChannelActions.Register();

            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            Request(player, ChannelHelloActions.Hello, Array.Empty<byte>());
            return player;
        }

        /// <summary>
        /// A generic object where the player stands, in a landblock, that can be used
        /// </summary>
        private static WorldObject NewStation(Player player, string station)
        {
            var item = VaultTestWorld.NewItem(GenericWcid);
            Assert.IsInstanceOfType(item, typeof(GenericObject));
            item.LegaceyStation = station;
            item.InitPhysicsObj();
            item.PhysicsObj.Position = new ACE.Server.Physics.Common.Position(player.PhysicsObj.Position);
            item.CurrentLandblock = stationLandblock;
            return item;
        }

        private static void Use(Player player, WorldObject station)
        {
            VaultTestWorld.OnWorldThread(() => station.OnActivate(player));
        }

        /// <summary>
        /// Starts the player's session at the Vault, as using the chest does
        /// </summary>
        private static void AtTheVault(Player player) => Use(player, NewStation(player, VaultStation));

        /// <summary>
        /// Moves the player along x, physics included
        /// </summary>
        private static void MoveAway(Player player, float meters)
        {
            VaultTestWorld.OnWorldThread(() =>
            {
                player.Location = Away(player, meters);
                player.PhysicsObj.Position = new ACE.Server.Physics.Common.Position(player.Location);
            });
        }

        private static ACE.Entity.Position Away(Player player, float meters)
        {
            var position = new ACE.Entity.Position(player.Location);
            position.PositionX += meters;
            return position;
        }

        /// <summary>
        /// Takes pushes off the player's client until there are the given number of them
        /// </summary>
        private static List<ChannelEvent> WaitForPushes(Player player, int count)
        {
            var pushes = new List<ChannelEvent>();

            VaultTestWorld.WaitUntil(() =>
            {
                pushes.AddRange(VaultTestWorld.TakeSent(player).OfType<GameEventLegaceyChannel>().Select(Decode).Where(e => e.Kind == ChannelEventKind.Push));
                return pushes.Count >= count;
            }, $"{count} station push(es)");

            return pushes;
        }

        private static void AssertStationPush(ChannelEvent push, string topic, string station, WorldObject item)
        {
            Assert.AreEqual(topic, push.Topic);
            Assert.AreEqual(ChannelStatus.Ok, push.Status);

            var body = new BinaryReader(new MemoryStream(push.Body), Encoding.UTF8);
            Assert.AreEqual(station, ChannelWire.ReadString(body), "the station name");
            Assert.AreEqual(item.Guid.Full, body.ReadUInt32(), "the station object's guid");
        }
    }
}
