using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Server.ClientChannel;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.Tests.ClientChannel;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ClientChannel.ChannelTestClient;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// The market window's Activity actions over the channel: the ledger's history with its cursors, the week's sales and purchases from the ledger,
    /// and trade notes withdrawn into the pack. Refused without the market station, when the market is switched off, when the pack is full, or while paused.
    /// </summary>
    public partial class VaultTests
    {
        private const string HistoryAction = MarketActivityActions.History;
        private const string WithdrawNotesAction = MarketActivityActions.WithdrawNotes;

        /// <summary>The history reply, read in the order the action writes it. Cursors are -1 when there is no next page.</summary>
        private sealed class HistoryReply
        {
            public long Balance;
            public long Earned;
            public long Spent;
            public int ItemsSold;
            public int ItemsBought;
            public List<(long Sequence, long Amount, long BalanceAfter, string Text)> Lines = new();
            public long NextLinesBefore;
            public List<(long Id, string Kind, string Text)> Movements = new();
            public long NextMovementsBefore;
        }

        [TestMethod]
        public void MarketHistory_AndWithdrawNotes_NeedTheMarketStation()
        {
            var player = ConnectedPlayer();

            Assert.AreEqual(ChannelStatus.NoStation, Request(player, HistoryAction, HistoryCursors(-1, -1)).Status);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, WithdrawNotesAction, Amount(5)).Status);

            Use(player, NewStation(player, VaultStation));
            WaitForPushes(player, 1);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, HistoryAction, HistoryCursors(-1, -1)).Status, "the Vault's session isn't the market's");
        }

        [TestMethod]
        public void MarketHistory_WhileMarketEnabledIsOff_IsRefusedWithThePlainMessage()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            var refused = WhileMarketIsOff(() => Request(player, HistoryAction, HistoryCursors(-1, -1)));

            Assert.AreEqual(ChannelStatus.Unavailable, refused.Status);
            Assert.AreEqual(VaultMessages.For(VaultOutcome.MarketClosed, null), Text(refused.Body));
        }

        [TestMethod]
        public void WithdrawNotes_WhileMarketEnabledIsOff_IsRefusedWithThePlainMessage_AndNothingMoves()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            var refused = WhileMarketIsOff(() => Request(player, WithdrawNotesAction, Amount(5)));

            Assert.AreEqual(ChannelStatus.Unavailable, refused.Status);
            Assert.AreEqual(VaultMessages.For(VaultOutcome.MarketClosed, null), Text(refused.Body));
            Assert.AreEqual(10L, Ledger.GetBalance(player.Character.AccountId));
            Assert.AreEqual(0, NotesInPacks(player));
        }

        [TestMethod]
        public void MarketHistory_PagesTheLedgerNewestFirst_AndTheItemMovementsByTheirOwnCursor()
        {
            var player = ConnectedPlayer();
            var accountId = player.Character.AccountId;
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            // 55 lines: a page holds 50, so the second page is the five oldest
            SeedAdminAdjustments(accountId, 55, 1);
            DepositedNamed(player, 2, n => $"History item {n}");

            var first = ReadHistory(player, -1, -1);

            Assert.AreEqual(55L, first.Balance);
            Assert.AreEqual(50, first.Lines.Count);
            Assert.AreEqual(55L, first.Lines[0].Sequence, "newest first");
            Assert.AreEqual(6L, first.Lines[49].Sequence);
            Assert.AreEqual(6L, first.NextLinesBefore);
            Assert.IsTrue(first.Lines[0].Text.StartsWith("Admin adjustment: test · +1 MMD", StringComparison.Ordinal), first.Lines[0].Text);
            Assert.AreEqual(2, first.Movements.Count);
            Assert.AreEqual("Deposited History item 2", first.Movements[0].Text, "newest first, worded as the portal's");
            Assert.AreEqual("deposit", first.Movements[0].Kind, "the kind is on the wire, so the window can tell a sale's movement apart");
            Assert.AreEqual(-1L, first.NextMovementsBefore);

            var second = ReadHistory(player, first.NextLinesBefore, -1);

            CollectionAssert.AreEqual(new long[] { 5, 4, 3, 2, 1 }, second.Lines.Select(l => l.Sequence).ToArray());
            Assert.AreEqual(-1L, second.NextLinesBefore, "the last page");
            Assert.AreEqual(0L, second.Earned, "week figures come with the first page only");

            var older = ReadHistory(player, -1, first.Movements[0].Id);

            Assert.AreEqual(1, older.Movements.Count, "the item movements page by their own cursor");
            Assert.AreEqual("Deposited History item 1", older.Movements[0].Text);
        }

        [TestMethod]
        public void MarketHistory_WeeklyFigures_CountTheWeeksSalesAndPurchases_NotAdminAdjustmentsOrFees()
        {
            var player = ConnectedPlayer();
            var accountId = player.Character.AccountId;
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            // an admin adjustment and trade notes are MMD too, but not sales; a sale ten days ago is outside the week
            AddLedgerTransfer(accountId, 100, DateTime.UtcNow.AddDays(-10), SystemAccount.Admin, TransferKind.AdminAdjust, "test");
            AddLedgerTransfer(accountId, 40, DateTime.UtcNow, SystemAccount.Admin, TransferKind.AdminAdjust, "test");
            AddLedgerTransfer(accountId, 30, DateTime.UtcNow, SystemAccount.Notes, TransferKind.NoteDeposit);

            // this week's sale of one item for 25, with a 2 MMD fee, to another account
            var buyer = VaultTestWorld.NewAccountId();
            AddLedgerTransfer(buyer, 100, DateTime.UtcNow, SystemAccount.Admin, TransferKind.AdminAdjust, "test");
            var listingId = ListItemForSale(player, 25);
            AddSale(listingId, buyer, accountId, 25, 2);

            var history = ReadHistory(player, -1, -1);

            Assert.AreEqual(25L, history.Earned, "the sale's price, not the fee and not the admin adjustment");
            Assert.AreEqual(0L, history.Spent);
            Assert.AreEqual(1, history.ItemsSold);
            Assert.AreEqual(0, history.ItemsBought);
            // 100 (last week) + 40 + 30 (trade notes) + 25 sold - 2 fee
            Assert.AreEqual(193L, history.Balance, "the balance is the ledger's, whatever the week shows");
        }

        [TestMethod]
        public void WeeklyFlow_ABuyerSpendsTheListingsPrice_AndNotTheSellersFee()
        {
            var seller = VaultTestWorld.NewAccountId();
            var buyer = VaultTestWorld.NewAccountId();
            var listingId = ListItemForSale(VaultTestWorld.NewPlayer(seller), 40);
            AddLedgerTransfer(buyer, 100, DateTime.UtcNow, SystemAccount.Admin, TransferKind.AdminAdjust, "test");
            AddSale(listingId, buyer, seller, 40, 4);

            using var shard = new ShardDbContext();
            var flow = MarketHistory.ReadFlowAsync(shard, buyer, DateTime.UtcNow.AddDays(-7)).GetAwaiter().GetResult();

            Assert.AreEqual(40L, flow.Spent);
            Assert.AreEqual(1, flow.ItemsBought);
            Assert.AreEqual(0L, flow.Earned);
        }

        [TestMethod]
        public void WithdrawNotes_IntoAPackWithRoom_MovesMmdIntoTheCharactersPack()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);
            GiveNotes(player, 30);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            var reply = Request(player, WithdrawNotesAction, Amount(20));

            Assert.AreEqual(ChannelStatus.Ok, reply.Status);
            var body = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            Assert.AreEqual(1, body.ReadByte(), "accepted");
            Assert.AreEqual(VaultMessages.ForNotes(VaultOutcome.NotesWithdrawn, 20, 10), ChannelWire.ReadString(body));
            Assert.AreEqual(10L, Ledger.GetBalance(player.Character.AccountId));
            Assert.AreEqual(20, NotesInPacks(player));
        }

        [TestMethod]
        public void WithdrawNotes_WithAFullPack_IsRefusedAndNothingMoves()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            VaultTestWorld.OnWorldThread(() =>
            {
                while (player.TryAddToInventory(VaultTestWorld.NewItem(VaultTestWorld.SwordWcid), out _)) { }
            });

            var refused = WithdrawNotesReply(player, 5);

            Assert.IsFalse(refused.Accepted);
            Assert.AreEqual(VaultMessages.ForNotes(VaultOutcome.NoPackSpace, 5, 10), refused.Message);
            Assert.AreEqual(10L, Ledger.GetBalance(player.Character.AccountId));
            Assert.AreEqual(0, NotesInPacks(player));
        }

        [TestMethod]
        public void WithdrawNotes_WhileTheMarketIsPaused_IsRefusedAndNothingMoves()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            (bool Accepted, string Message) refused;
            using (var shard = new ShardDbContext())
                MarketPause.Pause(shard, "a test", DateTime.UtcNow);
            try
            {
                refused = WithdrawNotesReply(player, 5);
            }
            finally
            {
                using (var shard = new ShardDbContext())
                    MarketPause.Resume(shard, "a test", DateTime.UtcNow);
            }

            Assert.IsFalse(refused.Accepted);
            Assert.AreEqual(VaultMessages.ForNotes(VaultOutcome.Paused, 5, 10), refused.Message);
            Assert.AreEqual(10L, Ledger.GetBalance(player.Character.AccountId));
            Assert.AreEqual(0, NotesInPacks(player));
        }

        // ---- helpers

        private static ChannelEvent WhileMarketIsOff(Func<ChannelEvent> request)
        {
            PropertyManager.ModifyBool(Vault.MarketEnabledKey, false);
            try
            {
                return request();
            }
            finally
            {
                PropertyManager.ModifyBool(Vault.MarketEnabledKey, true);
            }
        }

        private static byte[] HistoryCursors(long transfersBefore, long itemsBefore) => ChannelWire.Body(w =>
        {
            w.Write(transfersBefore);
            w.Write(itemsBefore);
        });

        private static byte[] Amount(long amount) => ChannelWire.Body(w => w.Write(amount));

        private static HistoryReply ReadHistory(Player player, long transfersBefore, long itemsBefore)
        {
            var reply = Request(player, HistoryAction, HistoryCursors(transfersBefore, itemsBefore));
            Assert.AreEqual(ChannelStatus.Ok, reply.Status);

            var body = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            var history = new HistoryReply { Balance = body.ReadInt64(), Earned = body.ReadInt64(), Spent = body.ReadInt64() };
            history.ItemsSold = body.ReadInt32();
            history.ItemsBought = body.ReadInt32();

            for (var count = body.ReadInt32(); count > 0; count--)
            {
                var sequence = body.ReadInt64();
                var amount = body.ReadInt64();
                var balanceAfter = body.ReadInt64();
                body.ReadInt64(); // time
                history.Lines.Add((sequence, amount, balanceAfter, ChannelWire.ReadString(body)));
            }

            history.NextLinesBefore = body.ReadInt64();

            for (var count = body.ReadInt32(); count > 0; count--)
            {
                var id = body.ReadInt64();
                body.ReadInt64(); // time
                var kind = ChannelWire.ReadString(body);
                history.Movements.Add((id, kind, ChannelWire.ReadString(body)));
            }

            history.NextMovementsBefore = body.ReadInt64();
            return history;
        }

        private static (bool Accepted, string Message) WithdrawNotesReply(Player player, long amount)
        {
            var reply = Request(player, WithdrawNotesAction, Amount(amount));
            Assert.AreEqual(ChannelStatus.Ok, reply.Status);

            var body = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            return (body.ReadByte() == 1, ChannelWire.ReadString(body));
        }

        /// <summary>
        /// Lists one of the player's Vault items for the price, and returns the listing's id
        /// </summary>
        private static long ListItemForSale(Player player, long price)
        {
            var guid = DepositedNamed(player, 1, n => $"Sale item {n}")[0];
            using var shard = new ShardDbContext();
            Assert.AreEqual(ListingOutcome.Ok, ListingStore.List(shard, player.Character.AccountId, guid, price, null, DateTime.UtcNow, () => false).Outcome);
            return shard.MarketListings.Single(l => l.ItemGuid == guid).Id;
        }

        /// <summary>
        /// Writes a sale the way the purchase does: the buyer pays the price, the seller gets it and pays the fee, and the fee goes to FEES
        /// </summary>
        private static void AddSale(long listingId, uint buyerAccountId, uint sellerAccountId, long price, long fee)
        {
            using var shard = new ShardDbContext();

            var transfer = new Transfer { Kind = TransferKind.Purchase, ListingId = listingId, CreatedTime = DateTime.UtcNow };
            transfer.Entries.Add(Ledger.PlayerEntry(buyerAccountId, -price));
            transfer.Entries.Add(Ledger.PlayerEntry(sellerAccountId, price));
            transfer.Entries.Add(Ledger.PlayerEntry(sellerAccountId, -fee, "Market fee"));
            transfer.Entries.Add(Ledger.SystemEntry(SystemAccount.Fees, fee));

            if (!Ledger.TryAdd(shard, transfer))
                throw new InvalidOperationException("the seeded sale would overdraw the buyer");

            shard.SaveChanges();
        }

        /// <summary>
        /// Writes admin adjustments the way the admin commands do: through the ledger, one transfer each
        /// </summary>
        private static void SeedAdminAdjustments(uint accountId, int count, long amount)
        {
            for (var number = 0; number < count; number++)
                AddLedgerTransfer(accountId, amount, DateTime.UtcNow, SystemAccount.Admin, TransferKind.AdminAdjust, "test");
        }

        private static void AddLedgerTransfer(uint accountId, long amount, DateTime createdTime, string systemAccount, string kind, string memo = null)
        {
            using var shard = new ShardDbContext();

            var transfer = new Transfer { Kind = kind, Memo = memo, CreatedTime = createdTime };
            transfer.Entries.Add(Ledger.PlayerEntry(accountId, amount));
            transfer.Entries.Add(Ledger.SystemEntry(systemAccount, -amount));

            if (!Ledger.TryAdd(shard, transfer))
                throw new InvalidOperationException("the seeded transfer would overdraw the account");

            shard.SaveChanges();
        }
    }
}
