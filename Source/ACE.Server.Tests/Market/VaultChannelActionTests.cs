using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
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

            var reply = Request(player, VaultChannelActions.List, ListRequestBody(string.Empty, 0, VaultChannelActions.PageSize));

            Assert.AreEqual(ChannelStatus.Ok, reply.Status, Text(reply.Body));
            var body = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            Assert.AreEqual(1, body.ReadByte(), "available");
            Assert.AreEqual(0L, body.ReadInt64(), "balance");
            Assert.AreEqual((int)MarketSettings.Get(MarketSettings.VaultSize), body.ReadInt32());
            Assert.AreEqual(1, body.ReadInt32(), "the Vault's count");
            Assert.AreEqual(1, body.ReadInt32(), "the total matching");
            Assert.AreEqual(1, body.ReadInt32(), "count");
            Assert.AreEqual(guid, body.ReadUInt32());
            Assert.AreEqual(VaultStore.Get(guid).Name, ChannelWire.ReadString(body));
        }

        [TestMethod]
        public void ChannelList_PagesTheVaultInOrder_AndCountsTheWholeVault()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var guids = DepositedNamed(player, 105, n => $"Vault item {n:000}");
            AtTheVault(player);

            var second = ListPage(player, string.Empty, 100, 100);
            Assert.AreEqual(105, second.VaultCount, "the Vault holds every item");
            Assert.AreEqual(105, second.Total, "no search: every item matches");
            CollectionAssert.AreEqual(guids.Skip(100).ToArray(), second.Guids, "the second page is the last five, in the Vault's order");

            var first = ListPage(player, string.Empty, 0, 100);
            CollectionAssert.AreEqual(guids.Take(100).ToArray(), first.Guids, "the first page is the first hundred");
        }

        [TestMethod]
        public void ChannelList_SearchesTheWholeVault_AndCountsTheMatches()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            // the only "needle" is the 103rd item, past the first page
            var guids = DepositedNamed(player, 105, n => n == 103 ? "Needle Blade" : $"Vault item {n:000}");
            AtTheVault(player);

            var found = ListPage(player, "needle", 0, 100);
            Assert.AreEqual(1, found.Total, "one match");
            Assert.AreEqual(105, found.VaultCount, "the Vault's count is not the search's");
            CollectionAssert.AreEqual(new[] { guids[102] }, found.Guids);

            // every item but the needle matches, whatever the case; the second page holds the last four matches
            var matches = ListPage(player, "VAULT ITEM", 100, 100);
            Assert.AreEqual(104, matches.Total, "the total counts every match, not the page");
            CollectionAssert.AreEqual(new[] { guids[100], guids[101], guids[103], guids[104] }, matches.Guids);

            // a substring from the middle of the name: "item 10" is in 100, 101, 102, 104 and 105 (not the needle)
            var middle = ListPage(player, "item 10", 0, 100);
            Assert.AreEqual(5, middle.Total, "a substring from inside the name matches");
            CollectionAssert.AreEqual(new[] { guids[99], guids[100], guids[101], guids[103], guids[104] }, middle.Guids);
        }

        [TestMethod]
        public void ChannelList_ClampsTheCountAndOffset_AndIgnoresBytesAfterTheCount()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var guids = DepositedNamed(player, 105, n => $"Vault item {n:000}");
            AtTheVault(player);

            CollectionAssert.AreEqual(guids.Take(1).ToArray(), ListPage(player, string.Empty, 0, 0).Guids, "a count under 1 is 1");
            Assert.AreEqual(VaultChannelActions.PageSize, ListPage(player, string.Empty, 0, 500).Guids.Length, "a count over the page size is the page size");
            CollectionAssert.AreEqual(guids.Take(1).ToArray(), ListPage(player, string.Empty, -5, 1).Guids, "a negative offset is the first item");

            var trailing = ListRequestBody(string.Empty, 0, 1).Concat(new byte[] { 1, 2, 3 }).ToArray();
            CollectionAssert.AreEqual(guids.Take(1).ToArray(), ListReply(player, trailing).Guids, "bytes after the count are for a later version");
        }

        [TestMethod]
        public void ChannelList_ATruncatedRequest_IsBadRequest()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            AtTheVault(player);

            var noCount = ChannelWire.Body(w =>
            {
                ChannelWire.WriteString(w, "item");
                w.Write(0);
            });
            var searchCutShort = new byte[] { 0x05, 0x00, (byte)'i' };

            foreach (var body in new[] { noCount, searchCutShort })
                Assert.AreEqual(ChannelStatus.BadRequest, Request(player, VaultChannelActions.List, body).Status);
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
                reply = Request(player, VaultChannelActions.List, ListRequestBody(string.Empty, 0, VaultChannelActions.PageSize));
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
            Assert.AreEqual(1, body.ReadInt32(), "the Vault's count");
            Assert.AreEqual(1, body.ReadInt32(), "the total matching");
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

                var body = new BinaryReader(new MemoryStream(Request(player, VaultChannelActions.List, ListRequestBody(string.Empty, 0, VaultChannelActions.PageSize)).Body), Encoding.UTF8);
                body.ReadByte();
                body.ReadInt64();
                body.ReadInt32();
                body.ReadInt32();
                body.ReadInt32();
                Assert.AreEqual(1, body.ReadInt32(), "count");
                Assert.AreEqual(guid, body.ReadUInt32(), "the Vault lists it");

                Assert.AreEqual(1, Request(player, VaultChannelActions.Withdraw, BitConverter.GetBytes(guid)).Body[0], "the withdrawal started");
                AssertChanged(WaitForEvent(player, e => e.Kind == ChannelEventKind.Push), nameof(VaultOutcome.Withdrawn));
                Assert.IsNotNull(player.GetInventoryItem(guid), "the withdrawal brought it back");
            }
        }

        /// <summary>
        /// Deposits <paramref name="count"/> swords, named by <paramref name="name"/>, in order; returns their guids in that order
        /// </summary>
        private static uint[] DepositedNamed(Player player, int count, Func<int, string> name, Action<WorldObject> configure = null)
        {
            var guids = new uint[count];
            for (var number = 1; number <= count; number++)
            {
                var item = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
                item.Name = name(number);
                configure?.Invoke(item);
                VaultTestWorld.Give(player, item);
                Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, item.Guid.Full).Outcome);
                guids[number - 1] = item.Guid.Full;
            }
            return guids;
        }

        [TestMethod]
        public void ChannelBatchWithdraw_ToAPackPlace_InsertsThereAndPushesTheRestBack_AndAFullPackFallsBack()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var pack = (Container)VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.PackWcid));
            // each given item goes in at the front, so the pack holds second, then first
            var first = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid), pack);
            var second = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid), pack);
            var guids = DepositedNamed(player, 2, n => $"Placed item {n}");
            AtTheVault(player);

            // dropped on the side pack's second cell: the two go there in order, and the item that was there moves back past them
            var body = ChannelWire.Body(w =>
            {
                w.Write(guids.Length);
                foreach (var guid in guids)
                    w.Write(guid);
                w.Write(pack.Guid.Full);
                w.Write(1);
            });
            var (accepted, message) = TransferReply(Request(player, VaultChannelActions.WithdrawBatch, body));

            Assert.IsTrue(accepted, message);
            Assert.AreEqual(pack.Guid.Full, player.GetInventoryItem(guids[0]).ContainerId);
            var order = pack.Inventory.Values.OrderBy(i => i.PlacementPosition).Select(i => i.Guid.Full).ToArray();
            CollectionAssert.AreEqual(new[] { second.Guid.Full, guids[0], guids[1], first.Guid.Full }, order);

            // a full side pack: the item goes where a withdrawal always has, the main pack
            pack.ItemCapacity = (byte)pack.Inventory.Count;
            var more = DepositedNamed(player, 1, n => $"Overflow item {n}");
            var full = ChannelWire.Body(w => { w.Write(1); w.Write(more[0]); w.Write(pack.Guid.Full); w.Write(0); });
            Assert.IsTrue(TransferReply(Request(player, VaultChannelActions.WithdrawBatch, full)).Accepted);
            Assert.AreEqual(player.Guid.Full, player.GetInventoryItem(more[0]).ContainerId);
        }

        /// <summary>
        /// The body of a vault.withdraw_batch request: a count, then the item guids
        /// </summary>
        private static byte[] WithdrawBatchBody(params uint[] guids) => ChannelWire.Body(w =>
        {
            w.Write(guids.Length);
            foreach (var guid in guids)
                w.Write(guid);
        });

        /// <summary>
        /// A transfer-style reply (a byte for accepted, then the message), read back
        /// </summary>
        private static (bool Accepted, string Message) TransferReply(ChannelEvent reply)
        {
            Assert.AreEqual(ChannelStatus.Ok, reply.Status);
            var reader = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            return (reader.ReadByte() != 0, ChannelWire.ReadString(reader));
        }

        /// <summary>
        /// Sends a batch withdrawal and asserts it is refused with a message containing <paramref name="reason"/>. Then asserts that every
        /// one of the guids is still in the Vault, not in the pack, and has no container in the database (as the Vault keeps it).
        /// </summary>
        private static void AssertBatchRefusedAndNothingMoved(Player player, uint[] guids, string reason)
        {
            var (accepted, message) = TransferReply(Request(player, VaultChannelActions.WithdrawBatch, WithdrawBatchBody(guids)));

            Assert.IsFalse(accepted, "refused: " + message);
            StringAssert.Contains(message, reason);
            foreach (var guid in guids)
            {
                Assert.IsNotNull(VaultStore.Get(guid), "still in the Vault");
                Assert.IsNull(player.GetInventoryItem(guid), "not in the pack");
                Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"), "the database still has it in the Vault");
            }
        }

        /// <summary>
        /// vault.list for a page: the Vault's count, the search's total, and the page's item guids
        /// </summary>
        private static (int VaultCount, int Total, uint[] Guids) ListPage(Player player, string search, int offset, int count) =>
            ListReply(player, ListRequestBody(search, offset, count));

        /// <summary>
        /// The reply to a vault.list request body, read as <see cref="ListPage"/> reads it
        /// </summary>
        private static (int VaultCount, int Total, uint[] Guids) ListReply(Player player, byte[] body)
        {
            var reply = Request(player, VaultChannelActions.List, body);

            Assert.AreEqual(ChannelStatus.Ok, reply.Status, Text(reply.Body));
            var reader = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            Assert.AreEqual(1, reader.ReadByte(), "available");
            reader.ReadInt64(); // balance
            reader.ReadInt32(); // capacity
            var vaultCount = reader.ReadInt32();
            var total = reader.ReadInt32();
            var shown = reader.ReadInt32();
            var guids = new uint[shown];
            for (var index = 0; index < shown; index++)
                guids[index] = ReadListedGuid(reader);
            return (vaultCount, total, guids);
        }

        /// <summary>
        /// The body of a vault.list request: the search, the offset and the count
        /// </summary>
        private static byte[] ListRequestBody(string search, int offset, int count) => ChannelWire.Body(w =>
        {
            ChannelWire.WriteString(w, search);
            w.Write(offset);
            w.Write(count);
        });

        /// <summary>
        /// Reads one listed item, returning its guid and skipping the rest of its fields
        /// </summary>
        private static uint ReadListedGuid(BinaryReader reader)
        {
            var guid = reader.ReadUInt32();
            ChannelWire.ReadString(reader); // name
            reader.ReadUInt32(); // item type
            reader.ReadInt32(); // stack size
            reader.ReadInt32(); // value
            ChannelWire.ReadString(reader); // state
            ChannelWire.ReadString(reader); // deposited by
            reader.ReadInt64(); // deposited time
            for (var layer = 0; layer < 5; layer++)
                reader.ReadUInt32(); // plate, underlay, icon, overlay, overlay secondary
            reader.ReadInt32(); // UI effects
            return guid;
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
            CollectionAssert.AreEqual(new[] { first, second.Guid.Full }, VaultStore.List(player.Character.AccountId).Select(i => i.ItemGuid).ToArray());
            var versionBefore = VaultStore.Get(first).RowVersion;
            AtTheVault(player);

            var reply = Request(player, VaultChannelActions.Move, ChannelWire.Body(w => { w.Write(first); w.Write(1); }));

            Assert.AreEqual(ChannelStatus.Ok, reply.Status, Text(reply.Body));
            Assert.AreEqual(1, reply.Body[0], "moved");
            CollectionAssert.AreEqual(new[] { second.Guid.Full, first }, VaultStore.List(player.Character.AccountId).Select(i => i.ItemGuid).ToArray());
            Assert.AreEqual(versionBefore, VaultStore.Get(first).RowVersion, "a move is not a concurrency change");
        }

        [TestMethod]
        public void ChannelBatchWithdraw_MovesEveryItemAtOnce_WithNoChannelWait_AndPushesChanged()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var guids = DepositedNamed(player, 3, n => $"Batch item {n}");
            AtTheVault(player);

            // a single withdrawal would channel for a minute; the batch doesn't
            using (ChannelSeconds(60))
            {
                var sent = Send(player, VaultChannelActions.WithdrawBatch, WithdrawBatchBody(guids));
                var events = WaitForEvents(player, e => e.Kind == ChannelEventKind.Reply && e.RequestId == sent);

                var reply = events.Last();
                Assert.AreEqual(ChannelStatus.Ok, reply.Status);
                Assert.AreEqual(1, reply.Body[0], "accepted");
                Assert.IsFalse(player.IsVaultChannelling, "no channel");
                foreach (var guid in guids)
                    Assert.IsNotNull(player.GetInventoryItem(guid), "in the pack");

                // the outcome is pushed before the reply that answers the request
                AssertChanged(events.Single(e => e.Kind == ChannelEventKind.Push), nameof(VaultOutcome.Withdrawn));
            }
        }

        [TestMethod]
        public void ChannelBatchWithdraw_IfTheSetDoesNotFit_MovesNothingAndSaysWhy()
        {
            // the pack has room for one of the two items: each fits alone, but not together
            var slots = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var slotGuids = DepositedNamed(slots, 2, n => $"Batch item {n}");
            slots.ItemCapacity = (byte)(slots.ItemCapacity!.Value - slots.GetFreeInventorySlots() + 1);
            Assert.AreEqual(1, slots.GetFreeInventorySlots());
            AtTheVault(slots);

            AssertBatchRefusedAndNothingMoved(slots, slotGuids, "room in your pack");

            // the slots are there, but the two (10 burden each) are over what is left of the burden
            var burden = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var burdenGuids = DepositedNamed(burden, 2, n => $"Heavy item {n}", item => item.EncumbranceVal = 10);
            burden.EncumbranceVal = burden.GetEncumbranceCapacity() * 3 - 15;
            AtTheVault(burden);

            AssertBatchRefusedAndNothingMoved(burden, burdenGuids, "too heavy to carry all");
        }

        [TestMethod]
        public void ChannelBatchWithdraw_WhileATransferIsActive_IsRefusedAndMovesNothing()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var channelling = DepositedNamed(player, 1, n => "Channelled item")[0];
            var guids = DepositedNamed(player, 2, n => $"Batch item {n}");
            AtTheVault(player);

            using (ChannelSeconds(60))
            {
                Assert.AreEqual(1, Request(player, VaultChannelActions.Withdraw, BitConverter.GetBytes(channelling)).Body[0], "the single withdrawal started");
                Assert.IsTrue(player.IsVaultChannelling);

                var (accepted, message) = TransferReply(Request(player, VaultChannelActions.WithdrawBatch, WithdrawBatchBody(guids)));

                Assert.IsFalse(accepted, "refused");
                StringAssert.Contains(message, "already moving");
                foreach (var guid in guids)
                    Assert.IsNotNull(VaultStore.Get(guid), "still in the Vault");
                Assert.IsTrue(player.IsVaultChannelling, "the single withdrawal is still channelling");

                VaultTestWorld.OnWorldThread(() => VaultChannel.Cancel(player));
            }
        }

        [TestMethod]
        public void ChannelBatchWithdraw_ABadList_IsBadOrRefusedAndMovesNothing()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var guids = DepositedNamed(player, 2, n => $"Batch item {n}");
            var (_, stranger) = DepositedItem();
            AtTheVault(player);

            // an empty list, a duplicate and more than a page are bad requests
            Assert.AreEqual(ChannelStatus.BadRequest, Request(player, VaultChannelActions.WithdrawBatch, WithdrawBatchBody()).Status, "empty");
            Assert.AreEqual(ChannelStatus.BadRequest, Request(player, VaultChannelActions.WithdrawBatch, WithdrawBatchBody(guids[0], guids[0])).Status, "duplicate");
            var tooMany = ChannelWire.Body(w => w.Write(VaultChannelActions.PageSize + 1));
            Assert.AreEqual(ChannelStatus.BadRequest, Request(player, VaultChannelActions.WithdrawBatch, tooMany).Status, "over a page");

            // another account's item is refused by the item rules, and the batch moves none of its items
            AssertBatchRefusedAndNothingMoved(player, new[] { guids[0], stranger }, "not in your Vault");
        }

        [TestMethod]
        public void ChannelBatchWithdraw_WhenTheSaveFailsPartWay_MovesNothing()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var guids = DepositedNamed(player, 3, n => $"Batch item {n}");
            AtTheVault(player);

            // the last item's withdraw event fails, after the first two items were written in the same save: the save must roll all of them back
            var trigger = "test_fail_last_batch_event";
            MarketTestDatabase.Execute(Db, $"CREATE TRIGGER `{trigger}` BEFORE INSERT ON `market_item_event` FOR EACH ROW IF NEW.item_Guid = {guids[2]} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'injected failure'; END IF;");
            try
            {
                AssertBatchRefusedAndNothingMoved(player, guids, "could not save");
            }
            finally
            {
                MarketTestDatabase.Execute(Db, $"DROP TRIGGER IF EXISTS `{trigger}`;");
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
    }
}
