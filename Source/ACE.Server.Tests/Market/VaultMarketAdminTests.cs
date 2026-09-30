using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: /market audit, resume, adjust and reverse in game, and the pause as MMD withdrawals see it
    /// </summary>
    public partial class VaultTests
    {
        [TestMethod]
        public void MarketCommand_IsForAdminsOnly()
        {
            var handler = typeof(MarketCommands).GetMethod(nameof(MarketCommands.HandleMarket)).GetCustomAttribute<CommandHandlerAttribute>();

            Assert.AreEqual("market", handler.Command);
            Assert.AreEqual(AccessLevel.Admin, handler.Access);
        }

        [TestMethod]
        public void MarketAudit_Failed_PausesMmdWithdrawals_WhileItemAndNoteDepositsWork_AndResumeRestoresThem()
        {
            var admin = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 30);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            // a balance with no ledger entries behind it
            var planted = VaultTestWorld.NewAccountId();
            MarketTestDatabase.Execute(Db, $"INSERT INTO market_balance (account_Id, balance) VALUES ({planted}, 5);");

            try
            {
                VaultTestWorld.TakeSent(admin);
                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "audit"));

                var told = WaitForChat(admin, "Ledger audit FAILED");
                Assert.IsTrue(told.Any(c => c.Contains($"account {planted} ", StringComparison.Ordinal)), "the admin sees the failures: " + string.Join("\n", told));
                Assert.IsTrue(Paused(), "a failed audit pauses the market");

                // MMD withdrawals are refused, through the entry point and the command
                var refused = VaultTestWorld.WithdrawNotes(player, 5);
                Assert.AreEqual(VaultOutcome.Paused, refused.Outcome, refused.Message);
                Assert.AreEqual(30, refused.Balance);
                Assert.AreEqual(30, Ledger.GetBalance(account));
                Assert.AreEqual(0, NotesInPacks(player));

                VaultTestWorld.TakeSent(player);
                VaultTestWorld.OnWorldThread(() => VaultCommands.HandleVault(player.Session, "withdraw", "mmd", "5"));
                WaitForChat(player, "paused");
                Assert.AreEqual(30, Ledger.GetBalance(account));

                // item and note deposits go on
                var sword = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
                VaultTestWorld.Save(sword);
                var deposited = VaultTestWorld.Deposit(player, sword.Guid.Full);
                Assert.AreEqual(VaultOutcome.Deposited, deposited.Outcome, deposited.Message);

                GiveNotes(player, 3);
                var notes = VaultTestWorld.DepositNotes(player);
                Assert.AreEqual(VaultOutcome.NotesDeposited, notes.Outcome, notes.Message);
                Assert.AreEqual(33, notes.Balance);

                // /market resume lifts it
                VaultTestWorld.TakeSent(admin);
                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "resume"));
                WaitForChat(admin, "resumed");
                Assert.IsFalse(Paused());

                var withdrawn = VaultTestWorld.WithdrawNotes(player, 5);
                Assert.AreEqual(VaultOutcome.NotesWithdrawn, withdrawn.Outcome, withdrawn.Message);
                Assert.AreEqual(28, Ledger.GetBalance(account));
            }
            finally
            {
                MarketTestDatabase.Execute(Db, $"DELETE FROM market_balance WHERE account_Id = {planted};");
                ResumeMarket();
            }
        }

        [TestMethod]
        public void WithdrawNotes_PausedBetweenTheCheckAndTheSave_TheJobRefusesAndNothingChanges()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            var before = BalanceRow(account);

            try
            {
                // hold the save queue, so the world-thread check passes and the pause lands before the job runs
                var gate = new System.Threading.ManualResetEventSlim();
                DatabaseManager.Shard.RemoveBiota(0x7FFFFFF1, _ => gate.Wait());

                VaultResult result = null;
                var done = new System.Threading.ManualResetEventSlim();
                VaultTestWorld.OnWorldThread(() => Vault.WithdrawNotes(player, 4, r => { result = r; done.Set(); }));

                using (var shard = MarketTestDatabase.CreateContext(Db))
                    MarketPause.Pause(shard, "test", DateTime.UtcNow);

                gate.Set();

                Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "the Vault reported a result");
                Assert.AreEqual(VaultOutcome.Paused, result.Outcome, result.Message);
                Assert.AreEqual(before, BalanceRow(account));
                Assert.AreEqual(0, NotesInPacks(player));
            }
            finally
            {
                ResumeMarket();
            }
        }

        [TestMethod]
        public void MarketAdjust_WritesAZeroSumAdminTransfer_WithTheMemoAndTheAdmin()
        {
            var admin = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            VaultTestWorld.TakeSent(admin);
            VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "adjust", account.ToString(), "+25", "lost", "in", "a", "crash"));
            WaitForChat(admin, "balance is 35 MMD");

            var transfer = SingleTransfer(account, TransferKind.AdminAdjust);
            Assert.AreEqual($"{admin.Session.AccountId}|{admin.Guid.Full}|lost in a crash", MarketTestDatabase.Rows(Db, $"SELECT actor_Account_Id, actor_Character_Id, memo FROM market_transfer WHERE id = {transfer};").Single());
            CollectionAssert.AreEqual(new[] { $"{account}|NULL|25|2|35", "NULL|ADMIN|-25|NULL|NULL" }, Entries(transfer));
            Assert.AreEqual(35, Ledger.GetBalance(account));

            // a debit below zero, no reason, a zero amount and an unknown account are refused and write nothing
            var transfers = Count("SELECT COUNT(*) FROM market_transfer;");

            VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "adjust", account.ToString(), "-36", "too", "much"));
            WaitForChat(admin, "below 0");

            VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "adjust", account.ToString(), "5"));
            WaitForChat(admin, "Usage: /market adjust");

            VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "adjust", account.ToString(), "0", "nothing"));
            WaitForChat(admin, "Usage: /market adjust");

            VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "adjust", "no-such-account-x9", "5", "grant"));
            WaitForChat(admin, "No account");

            Assert.AreEqual(transfers, Count("SELECT COUNT(*) FROM market_transfer;"));
            Assert.AreEqual(35, Ledger.GetBalance(account));
        }

        [TestMethod]
        public void MarketReverse_UndoesATransferOnce_ASecondReversalIsRefused()
        {
            var admin = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            VaultTestWorld.TakeSent(admin);
            VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "adjust", account.ToString(), "5", "grant"));
            WaitForChat(admin, "balance is 15 MMD");
            var grant = SingleTransfer(account, TransferKind.AdminAdjust);

            VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "reverse", grant.ToString(), "granted", "in", "error"));
            WaitForChat(admin, $"Reversed transfer {grant}");

            var reversal = SingleTransfer(account, TransferKind.Reversal);
            Assert.AreEqual($"{admin.Session.AccountId}|{admin.Guid.Full}|granted in error|{grant}", MarketTestDatabase.Rows(Db, $"SELECT actor_Account_Id, actor_Character_Id, memo, reverses_Transfer_Id FROM market_transfer WHERE id = {reversal};").Single());
            CollectionAssert.AreEqual(new[] { "NULL|ADMIN|5|NULL|NULL", $"{account}|NULL|-5|3|10" }, Entries(reversal));
            Assert.AreEqual(10, Ledger.GetBalance(account));

            VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "reverse", grant.ToString(), "again"));
            WaitForChat(admin, "already been reversed");

            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_transfer WHERE reverses_Transfer_Id = {grant};"));
            Assert.AreEqual(10, Ledger.GetBalance(account));
        }

        // ---- helpers

        /// <summary>
        /// Waits for a chat line containing text, and returns every line the player was told meanwhile
        /// </summary>
        private static List<string> WaitForChat(Player player, string text)
        {
            var told = new List<string>();

            VaultTestWorld.WaitUntil(() =>
            {
                told.AddRange(VaultTestWorld.Chats(VaultTestWorld.TakeSent(player)));
                return told.Any(c => c.Contains(text, StringComparison.Ordinal));
            }, $"a chat saying '{text}'");

            return told;
        }

        private static bool Paused()
        {
            using var shard = MarketTestDatabase.CreateContext(Db);
            return MarketPause.IsPaused(shard);
        }

        private static void ResumeMarket()
        {
            using var shard = MarketTestDatabase.CreateContext(Db);
            MarketPause.Resume(shard, "test cleanup", DateTime.UtcNow);
        }
    }
}
