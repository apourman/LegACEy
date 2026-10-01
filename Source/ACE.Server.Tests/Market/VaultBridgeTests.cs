using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: the game bridge. Each ticket is written with the store the Market API uses, and the world's own poller claims it,
    /// runs it through the Vault entry point the /vault commands use, and writes the result back.
    /// </summary>
    public partial class VaultTests
    {
        // ---- MMD

        [TestMethod]
        public void Bridge_MmdWithdraw_ForAnOnlineCharacter_NotesArriveInThePackAndTheTicketIsDone()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 50);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 20));

                Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}", WaitForTicket(ticket));
                StringAssert.Contains(TicketMessage(ticket), "20 trade notes");

                Assert.AreEqual(20, NotesInPacks(player), "the notes are in the character's pack");
                CollectionAssert.AreEqual(new[] { 20L }, DatabaseNoteStacks(player.Guid.Full), "the database has them in the pack");
                Assert.AreEqual(30, Ledger.GetBalance(account));

                var transfer = SingleTransfer(account, TransferKind.NoteWithdraw);
                Assert.AreEqual(ticket, Count($"SELECT ticket_Id FROM market_transfer WHERE id = {transfer};"), "the transfer names the ticket it was saved with");
            }
        }

        [TestMethod]
        public void Bridge_MmdWithdraw_WhileTheMarketIsPaused_FailsAsPausedAndNothingMoves()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            using (var shard = MarketTestDatabase.CreateContext(Db))
                MarketPause.Pause(shard, "test", DateTime.UtcNow);

            try
            {
                using (VaultTestWorld.Online(player))
                {
                    var ticket = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 5));

                    Assert.AreEqual($"{TicketStatus.Failed}|paused", WaitForTicket(ticket));
                    Assert.AreEqual(0, NotesInPacks(player));
                    Assert.AreEqual(10, Ledger.GetBalance(account));
                }
            }
            finally
            {
                ResumeMarket();
            }
        }

        [TestMethod]
        public void Bridge_MmdWithdraw_SaveFails_TicketFailsAndNothingMoves()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            var balanceRow = BalanceRow(account);

            using (VaultTestWorld.Online(player))
            using (FailInsertsInto("market_ledger_entry"))
            {
                var ticket = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 4));

                Assert.AreEqual($"{TicketStatus.Failed}|save_failed", WaitForTicket(ticket));
            }

            Assert.AreEqual(0, NotesInPacks(player));
            Assert.AreEqual(0, DatabaseNoteStacks(player.Guid.Full).Count);
            Assert.AreEqual(balanceRow, BalanceRow(account), "the balance row is exactly as before");
        }

        // ---- items

        [TestMethod]
        public void Bridge_ItemWithdraw_RunsTheChannelInGame_ThenTheItemIsInThePack()
        {
            var (player, guid) = DepositedItem();

            using (ChannelSeconds(1))
            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));

                VaultTestWorld.WaitUntil(() => player.IsVaultChannelling, "the channel to start");
                Assert.IsTrue(player.IsFrozen ?? false, "the channel freezes the player");
                Assert.AreEqual(TicketStatus.Claimed, TicketStatusOf(ticket), "the ticket is in progress while the player channels");
                Assert.AreEqual(VaultItemState.Withdrawing, VaultStore.Get(guid).State);

                Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}", WaitForTicket(ticket));
            }

            VaultTestWorld.WaitUntil(() => player.GetInventoryItem(guid) != null, "the item to reach the pack");
            Assert.IsNull(VaultStore.Get(guid), "the Vault row is gone");
            Assert.IsFalse(player.IsFrozen ?? false);
            Assert.AreEqual(player.Guid.Full, (uint)Count($"SELECT value FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"));
        }

        [TestMethod]
        public void Bridge_ItemWithdraw_CharacterOffline_FailsWithAReasonAndNothingMoves()
        {
            var (player, guid) = DepositedItem();

            var ticket = NewTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));

            Assert.AreEqual($"{TicketStatus.Failed}|offline", WaitForTicket(ticket));
            Assert.IsFalse(string.IsNullOrWhiteSpace(TicketMessage(ticket)));
            AssertStillHeld(player, guid);
        }

        [TestMethod]
        public void Bridge_ItemWithdraw_InAPlayerFight_FailsWithAReasonAndNothingMoves()
        {
            var (player, guid) = DepositedItem();
            player.PlayerKillerStatus = PlayerKillerStatus.PK;
            Player.UpdatePKTimers(NewPk(), player);

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));

                Assert.AreEqual($"{TicketStatus.Failed}|recent_player_fight", WaitForTicket(ticket));
            }

            Assert.IsFalse(player.IsVaultChannelling);
            AssertStillHeld(player, guid);
        }

        [TestMethod]
        public void Bridge_ItemWithdraw_NoPackSpace_FailsWithAReasonAndNothingMoves()
        {
            var (player, guid) = DepositedItem();
            VaultTestWorld.OnWorldThread(() =>
            {
                while (player.TryAddToInventory(VaultTestWorld.NewItem(VaultTestWorld.SwordWcid), out _)) { }
            });

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));

                Assert.AreEqual($"{TicketStatus.Failed}|no_pack_space", WaitForTicket(ticket));
            }

            AssertStillHeld(player, guid);
        }

        [TestMethod]
        public void Bridge_TicketForAnotherAccountsCharacter_OrOfAnUnsupportedKind_Fails()
        {
            var (player, guid) = DepositedItem();
            var stranger = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            using (VaultTestWorld.Online(player))
            using (VaultTestWorld.Online(stranger))
            {
                var wrongAccount = NewTicket(stranger, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid), accountId: player.Character.AccountId);
                var deposit = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: guid));

                Assert.AreEqual($"{TicketStatus.Failed}|invalid_character", WaitForTicket(wrongAccount));
                Assert.AreEqual($"{TicketStatus.Failed}|unsupported_kind", WaitForTicket(deposit));
            }

            AssertStillHeld(player, guid);
        }

        // ---- restart

        [TestMethod]
        public void Bridge_Restart_ClaimedTicketsAreFailedWithAReason_AndNothingWasHalfDone()
        {
            var (player, guid) = DepositedItem();
            var account = player.Character.AccountId;
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            var balanceRow = BalanceRow(account);

            // the server stopped while it worked on these: an item withdrawal whose channel had marked the row, and a note withdrawal
            var itemTicket = NewClaimedTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));
            var notesTicket = NewClaimedTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 5));
            Assert.IsNotNull(VaultStore.TryMarkWithdrawing(guid, account, VaultStore.Get(guid).RowVersion));

            Vault.Initialize();

            Assert.AreEqual($"{TicketStatus.Failed}|{TicketStore.ServerRestart}", TicketResult(itemTicket));
            Assert.AreEqual($"{TicketStatus.Failed}|{TicketStore.ServerRestart}", TicketResult(notesTicket));
            Assert.IsFalse(string.IsNullOrWhiteSpace(TicketMessage(itemTicket)));

            AssertStillHeld(player, guid);
            Assert.AreEqual(balanceRow, BalanceRow(account), "no MMD left the balance");
            Assert.AreEqual(0, NotesInPacks(player));
        }

        [TestMethod]
        public void Bridge_FinishedTicketsOlderThanThirtyDays_AreRemoved()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var old = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 1));
            var recent = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 1));

            // finished by the poller (the character is offline), then aged
            Assert.AreEqual($"{TicketStatus.Failed}|offline", WaitForTicket(old));
            Assert.AreEqual($"{TicketStatus.Failed}|offline", WaitForTicket(recent));
            MarketTestDatabase.Execute(Db, $"UPDATE market_ticket SET finished_Time = UTC_TIMESTAMP(6) - INTERVAL 31 DAY WHERE id = {old};");
            MarketTestDatabase.Execute(Db, $"UPDATE market_ticket SET finished_Time = UTC_TIMESTAMP(6) - INTERVAL 29 DAY WHERE id = {recent};");

            Vault.Initialize();

            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_ticket WHERE id = {old};"), "the old ticket is deleted at startup");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_ticket WHERE id = {recent};"));
        }

        [TestMethod]
        public void Bridge_EveryVaultRefusal_HasItsOwnResultCode()
        {
            var refusals = Enum.GetValues<VaultOutcome>().Where(o => !new VaultResult(o, "", 0).Success).ToList();
            var codes = refusals.Select(GameBridge.ResultCode).ToList();

            CollectionAssert.DoesNotContain(codes, "failed", "every refusal is named");
            Assert.AreEqual(codes.Count, codes.Distinct().Count(), "no two refusals share a code");
            CollectionAssert.AllItemsAreNotNull(codes);
            Assert.IsTrue(codes.All(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^[a-z]+(_[a-z]+)*$")), "codes are snake case");
            Assert.AreEqual("no_pack_space", GameBridge.ResultCode(VaultOutcome.NoPackSpace));
        }

        // ---- helpers

        /// <summary>
        /// A ticket written as the Market API writes one, for the player's character (the account is the player's unless given)
        /// </summary>
        private static long NewTicket(Player player, string kind, TicketPayload payload, uint? accountId = null)
        {
            using var shard = MarketTestDatabase.CreateContext(Db);

            var result = TicketStore.Create(shard, accountId ?? player.Character.AccountId, player.Guid.Full, kind, payload, Guid.NewGuid().ToString("N"), DateTime.UtcNow);
            Assert.AreEqual(TicketCreateOutcome.Created, result.Outcome);

            return result.Ticket.Id;
        }

        /// <summary>
        /// A ticket as a game server leaves it while working on it. Written already claimed, so the world's poller never sees it waiting.
        /// </summary>
        private static long NewClaimedTicket(Player player, string kind, TicketPayload payload)
        {
            MarketTestDatabase.Execute(Db, "INSERT INTO market_ticket (kind, account_Id, character_Id, payload, status, idempotency_Key, created_Time, claimed_Time) " +
                $"VALUES ('{kind}', {player.Character.AccountId}, {player.Guid.Full}, '{payload.ToJson()}', '{TicketStatus.Claimed}', '{Guid.NewGuid():N}', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));");

            return Count($"SELECT MAX(id) FROM market_ticket WHERE account_Id = {player.Character.AccountId} AND kind = '{kind}';");
        }

        /// <summary>
        /// Waits for the game server to finish the ticket, and returns its status and result code
        /// </summary>
        private static string WaitForTicket(long ticketId)
        {
            VaultTestWorld.WaitUntil(() => TicketStatusOf(ticketId) is TicketStatus.Done or TicketStatus.Failed, $"ticket {ticketId} to finish");

            return TicketResult(ticketId);
        }

        private static string TicketStatusOf(long ticketId) => MarketTestDatabase.Rows(Db, $"SELECT status FROM market_ticket WHERE id = {ticketId};").Single();

        private static string TicketResult(long ticketId) => MarketTestDatabase.Rows(Db, $"SELECT status, result_Code FROM market_ticket WHERE id = {ticketId};").Single();

        private static string TicketMessage(long ticketId) => MarketTestDatabase.Rows(Db, $"SELECT result_Message FROM market_ticket WHERE id = {ticketId};").Single();

        private static void AssertStillHeld(Player player, uint guid)
        {
            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(guid)?.State, "the item is still held in the Vault");
            Assert.IsNull(player.GetInventoryItem(guid), "the item is not in the pack");
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"), "the database has no container for it");
        }
    }
}
