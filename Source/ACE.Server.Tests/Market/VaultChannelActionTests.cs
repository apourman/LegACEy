using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Entity.Enum;
using ACE.Server.ClientChannel;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Tests.ClientChannel;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ClientChannel.ChannelTestClient;

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
            AtTheVault(player);
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
        public void ChannelList_MarketClosed_SendsNoBalance()
        {
            var (player, _) = DepositedItem();
            AtTheVault(player);
            VaultTestWorld.TakeSent(player);

            PropertyManager.ModifyBool(Vault.MarketEnabledKey, false);
            ChannelEvent reply;
            try
            {
                reply = Request(player, VaultChannelActions.List, Array.Empty<byte>());
            }
            finally
            {
                PropertyManager.ModifyBool(Vault.MarketEnabledKey, true);
            }

            Assert.AreEqual(ChannelStatus.Ok, reply.Status);
            var body = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            Assert.AreEqual(1, body.ReadByte(), "the Vault itself stays available");
            Assert.AreEqual(VaultChannelActions.NoBalance, body.ReadInt64(), "balance");
            Assert.AreEqual((int)MarketSettings.Get(MarketSettings.VaultSize), body.ReadInt32());
            Assert.AreEqual(1, body.ReadInt32(), "count");
        }

        [TestMethod]
        public void ChannelWithdraw_StartsTheChannel_ThenPushesTheOutcome_OnlyToChannelClients()
        {
            var (player, guid) = DepositedItem();
            var (bystander, otherGuid) = DepositedItem();
            AtTheVault(player);

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

                // a client that never used the channel gets no LegACEy events from a withdrawal that doesn't come over it
                VaultTestWorld.TakeSent(bystander);
                VaultTestWorld.OnWorldThread(() => VaultChannel.StartWithdraw(bystander, otherGuid));
                VaultTestWorld.WaitUntil(() => bystander.GetInventoryItem(otherGuid) != null, "the bystander's withdrawal");
                Assert.IsFalse(VaultTestWorld.TakeSent(bystander).OfType<GameEventLegaceyChannel>().Any());
            }
        }

        [TestMethod]
        public void ChannelDeposit_AtTheVault_ThenListAndWithdraw_BringTheItemBack()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.Full;
            AtTheVault(player);

            using (ChannelSeconds(1))
            {
                Assert.AreEqual(1, Request(player, VaultChannelActions.Deposit, BitConverter.GetBytes(guid)).Body[0], "the deposit started");
                AssertChanged(WaitForEvent(player, e => e.Kind == ChannelEventKind.Push), nameof(VaultOutcome.Deposited));
                Assert.IsNull(player.GetInventoryItem(guid), "the deposit took the item");

                var body = new BinaryReader(new MemoryStream(Request(player, VaultChannelActions.List, Array.Empty<byte>()).Body), Encoding.UTF8);
                body.ReadByte();
                body.ReadInt64();
                body.ReadInt32();
                Assert.AreEqual(1, body.ReadInt32(), "count");
                Assert.AreEqual(guid, body.ReadUInt32(), "the Vault lists it");

                Assert.AreEqual(1, Request(player, VaultChannelActions.Withdraw, BitConverter.GetBytes(guid)).Body[0], "the withdrawal started");
                AssertChanged(WaitForEvent(player, e => e.Kind == ChannelEventKind.Push), nameof(VaultOutcome.Withdrawn));
                Assert.IsNotNull(player.GetInventoryItem(guid), "the withdrawal brought it back");
            }
        }

        private static void AssertChanged(ChannelEvent push, string outcome)
        {
            Assert.AreEqual(VaultChannelActions.Changed, push.Topic);
            var changed = new BinaryReader(new MemoryStream(push.Body), Encoding.UTF8);
            Assert.AreEqual(1, changed.ReadByte(), "success");
            Assert.AreEqual(outcome, ChannelWire.ReadString(changed));
        }

        [TestMethod]
        public void ChannelCheck_RefusesAnAttunedItemWithTheDepositMessage_AndAcceptsAPlainOne()
        {
            var (player, attuned) = NewItem(i => i.Attuned = AttunedStatus.Attuned);
            var plain = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            AtTheVault(player);

            var refused = new BinaryReader(new MemoryStream(Request(player, VaultChannelActions.Check, BitConverter.GetBytes(attuned.Guid.Full)).Body), Encoding.UTF8);
            Assert.AreEqual(attuned.Guid.Full, refused.ReadUInt32());
            Assert.AreEqual(0, refused.ReadByte(), "attuned items can't be deposited");
            Assert.AreEqual(VaultMessages.For(VaultOutcome.Attuned, attuned.Name), ChannelWire.ReadString(refused));

            var accepted = new BinaryReader(new MemoryStream(Request(player, VaultChannelActions.Check, BitConverter.GetBytes(plain.Guid.Full)).Body), Encoding.UTF8);
            accepted.ReadUInt32();
            Assert.AreEqual(1, accepted.ReadByte());
            Assert.IsNotNull(player.GetInventoryItem(attuned.Guid.Full), "a check moves nothing");
        }

        [TestMethod]
        public void ChannelMove_PutsTheItemAtTheIndex_AndTheListKeepsThatOrder()
        {
            var (player, first) = DepositedItem();
            var second = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, second.Guid.Full).Outcome);
            CollectionAssert.AreEqual(new[] { first, second.Guid.Full }, Vault.List(player).Select(i => i.ItemGuid).ToArray());
            var versionBefore = VaultStore.Get(first).RowVersion;
            AtTheVault(player);

            var reply = Request(player, VaultChannelActions.Move, ChannelWire.Body(w => { w.Write(first); w.Write(1); }));

            Assert.AreEqual(ChannelStatus.Ok, reply.Status, Text(reply.Body));
            Assert.AreEqual(1, reply.Body[0], "moved");
            CollectionAssert.AreEqual(new[] { second.Guid.Full, first }, Vault.List(player).Select(i => i.ItemGuid).ToArray());
            Assert.AreEqual(versionBefore, VaultStore.Get(first).RowVersion, "a move is not a concurrency change");
        }

        [TestMethod]
        public void ChannelRequest_ForAnUnknownAction_RepliesUnknownAction()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            var reply = Request(player, "no.such.action", Array.Empty<byte>());

            Assert.AreEqual(ChannelStatus.UnknownAction, reply.Status);
            StringAssert.Contains(ChannelWire.ReadString(new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8)), "no.such.action");
        }
    }
}
