using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Server.ClientChannel;
using ACE.Server.Command.Handlers;
using ACE.Server.Market;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// The Vault's actions on the LegACEy in-band channel, sent as the plugin sends them and read back from the queued game events
    /// </summary>
    public partial class VaultTests
    {
        [TestMethod]
        public void ChannelList_RepliesWithTheAccountsItemsBalanceAndIcons()
        {
            var (player, guid) = DepositedItem();
            VaultTestWorld.TakeSent(player);

            var reply = Request(player, VaultChannelActions.List, Array.Empty<byte>());

            Assert.AreEqual(ChannelStatus.Ok, reply.Status, Text(reply.Body));
            var body = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            Assert.AreEqual(1, body.ReadByte(), "available");
            Assert.AreEqual(0L, body.ReadInt64(), "balance");
            Assert.AreEqual((int)MarketSettings.Get(MarketSettings.VaultSize), body.ReadInt32());
            Assert.AreEqual(1, body.ReadInt32(), "count");
            Assert.AreEqual(guid, body.ReadUInt32());
            Assert.AreEqual(VaultStore.Get(guid).Name, ChannelWire.ReadString(body));
        }

        [TestMethod]
        public void ChannelWithdraw_StartsTheChannel_ThenPushesTheOutcome_OnlyToChannelClients()
        {
            var (player, guid) = DepositedItem();
            var (bystander, otherGuid) = DepositedItem();

            using (ChannelSeconds(1))
            {
                var reply = Request(player, VaultChannelActions.Withdraw, BitConverter.GetBytes(guid));
                var started = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
                Assert.AreEqual(1, started.ReadByte(), "the withdrawal started: " + ChannelWire.ReadString(started));
                Assert.IsTrue(player.IsVaultChannelling);

                var push = WaitForEvent(player, e => e.Kind == ChannelEventKind.Push);
                Assert.AreEqual(VaultChannelActions.Changed, push.Topic);
                var changed = new BinaryReader(new MemoryStream(push.Body), Encoding.UTF8);
                Assert.AreEqual(1, changed.ReadByte(), "success");
                Assert.AreEqual(nameof(VaultOutcome.Withdrawn), ChannelWire.ReadString(changed));
                ChannelWire.ReadString(changed);
                Assert.AreEqual(guid, changed.ReadUInt32());
                Assert.IsNotNull(player.GetInventoryItem(guid));

                // a client that never used the channel gets no LegACEy events from a /vault withdrawal
                VaultTestWorld.TakeSent(bystander);
                VaultTestWorld.OnWorldThread(() => VaultCommands.HandleVault(bystander.Session, "withdraw", $"0x{otherGuid:X8}"));
                VaultTestWorld.WaitUntil(() => bystander.GetInventoryItem(otherGuid) != null, "the bystander's withdrawal");
                Assert.IsFalse(VaultTestWorld.TakeSent(bystander).OfType<GameEventLegaceyChannel>().Any());
            }
        }

        [TestMethod]
        public void ChannelRequest_ForAnUnknownAction_RepliesUnknownAction()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            var reply = Request(player, "no.such.action", Array.Empty<byte>());

            Assert.AreEqual(ChannelStatus.UnknownAction, reply.Status);
            StringAssert.Contains(ChannelWire.ReadString(new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8)), "no.such.action");
        }

        private static string Text(byte[] body) => body.Length >= 2 ? ChannelWire.ReadString(new BinaryReader(new MemoryStream(body), Encoding.UTF8)) : string.Empty;

        private sealed class ChannelEvent
        {
            public ChannelEventKind Kind;
            public ChannelStatus Status;
            public uint RequestId;
            public string Topic;
            public byte[] Body;
        }

        private static uint nextRequestId;

        private static ChannelEvent Request(Player player, string action, byte[] body)
        {
            var id = ++nextRequestId;
            var payload = ChannelWire.Body(w =>
            {
                w.Write(ChannelWire.Version);
                w.Write(id);
                ChannelWire.WriteString(w, action);
                w.Write((uint)body.Length);
                w.Write(body);
            });

            VaultTestWorld.OnWorldThread(() => ServerChannel.Receive(player.Session, new BinaryReader(new MemoryStream(payload))));

            return WaitForEvent(player, e => e.Kind == ChannelEventKind.Reply && e.RequestId == id);
        }

        private static ChannelEvent WaitForEvent(Player player, Func<ChannelEvent, bool> match)
        {
            ChannelEvent found = null;
            var seen = new List<ChannelEvent>();

            VaultTestWorld.WaitUntil(() =>
            {
                seen.AddRange(VaultTestWorld.TakeSent(player).OfType<GameEventLegaceyChannel>().Select(Decode));
                found = seen.FirstOrDefault(match);
                return found != null;
            }, "a LegACEy channel event");

            return found;
        }

        private static ChannelEvent Decode(GameMessage message)
        {
            // opcode, character, event sequence, event type, then the channel payload
            var reader = new BinaryReader(new MemoryStream(message.Data.ToArray()), Encoding.UTF8);
            reader.ReadBytes(16);
            Assert.AreEqual(ChannelWire.Version, reader.ReadUInt16());
            var e = new ChannelEvent { Kind = (ChannelEventKind)reader.ReadByte(), Status = (ChannelStatus)reader.ReadByte(), RequestId = reader.ReadUInt32(), Topic = ChannelWire.ReadString(reader) };
            e.Body = reader.ReadBytes((int)reader.ReadUInt32());
            return e;
        }
    }
}
