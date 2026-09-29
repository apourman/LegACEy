using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum.Properties;
using ACE.Server.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: trade notes (MMD) in and out of the account balance. Instant, through the Vault entry point, each a single save with its ledger transfer.
    /// </summary>
    public partial class VaultTests
    {
        private const uint TradeNoteWcid = 20630;   // tradenote250000, 1 MMD
        private const uint OtherTradeNoteWcid = 2627; // tradenote100000, not MMD

        // ---- deposit

        [TestMethod]
        public void DepositNotes_FiftyNotes_LeaveThePacksAndCreditTheBalanceInOneZeroSumTransfer()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            var pack = (Container)VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.PackWcid));

            // 50 single notes, one of them in a side pack; a sword and a note of another denomination stay
            var guids = Enumerable.Range(0, 49).Select(_ => GiveNotes(player, 1).Guid.Full).ToList();
            guids.Add(GiveNotes(player, 1, pack).Guid.Full);
            var sword = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var otherNote = VaultTestWorld.Give(player, VaultTestWorld.NewItem(OtherTradeNoteWcid));

            var result = VaultTestWorld.DepositNotes(player);

            Assert.AreEqual(VaultOutcome.NotesDeposited, result.Outcome, result.Message);
            Assert.AreEqual(50, result.Amount);
            Assert.AreEqual(50, result.Balance);

            Assert.AreEqual(0, NotesInPacks(player), "the notes left the packs");
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota WHERE id IN ({string.Join(",", guids)});"), "the note rows are deleted");
            Assert.IsNotNull(player.GetInventoryItem(sword.Guid), "other items stay");
            Assert.IsNotNull(player.GetInventoryItem(otherNote.Guid), "other trade notes aren't MMD");

            Assert.AreEqual(50, Ledger.GetBalance(account));

            var transfer = SingleTransfer(account, TransferKind.NoteDeposit);
            CollectionAssert.AreEqual(new[] { $"{account}|NULL|50|1|50", "NULL|NOTES|-50|NULL|NULL" }, Entries(transfer));

            var events = MarketTestDatabase.Rows(Db, $"SELECT item_Guid, account_Id, character_Id, kind, quantity FROM market_item_event WHERE transfer_Id = {transfer} ORDER BY item_Guid;");
            CollectionAssert.AreEqual(guids.OrderBy(g => g).Select(g => $"{g}|{account}|{player.Guid.Full}|{ItemEventKind.Deposit}|1").ToList(), events, "one destroyed-note event per note GUID");
        }

        [TestMethod]
        public void DepositNotes_Stacks_EventQuantitiesAddUpToTheTransfer()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 30);
            GiveNotes(player, 20);

            var result = VaultTestWorld.DepositNotes(player);

            Assert.AreEqual(VaultOutcome.NotesDeposited, result.Outcome, result.Message);
            Assert.AreEqual(50, result.Amount);

            var transfer = SingleTransfer(account, TransferKind.NoteDeposit);
            Assert.AreEqual(2, Count($"SELECT COUNT(*) FROM market_item_event WHERE transfer_Id = {transfer};"));
            Assert.AreEqual(50, Count($"SELECT SUM(quantity) FROM market_item_event WHERE transfer_Id = {transfer};"));
            Assert.AreEqual(0, Count($"SELECT SUM(amount) FROM market_ledger_entry WHERE transfer_Id = {transfer};"));
        }

        [TestMethod]
        public void DepositNotes_NoNotes_IsRefusedAndWritesNothing()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);

            var result = VaultTestWorld.DepositNotes(player);

            Assert.AreEqual(VaultOutcome.NoNotes, result.Outcome, result.Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Message));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_ledger_entry WHERE account_Id = {account};"));
        }

        // ---- withdraw

        [TestMethod]
        public void WithdrawNotes_Twenty_CreatesTheNotesInThePackAndDebitsInOneZeroSumTransfer()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 50);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            var result = VaultTestWorld.WithdrawNotes(player, 20);

            Assert.AreEqual(VaultOutcome.NotesWithdrawn, result.Outcome, result.Message);
            Assert.AreEqual(20, result.Amount);
            Assert.AreEqual(30, result.Balance);

            var notes = Notes(player);
            CollectionAssert.AreEqual(new[] { 20 }, notes.Select(n => n.StackSize ?? 1).ToList(), "one stack of 20 in the pack");
            Assert.AreEqual(player.Guid.Full, notes[0].ContainerId);
            CollectionAssert.AreEqual(new[] { 20L }, DatabaseNoteStacks(player.Guid.Full), "the database has the notes in the pack");

            Assert.AreEqual(30, Ledger.GetBalance(account));

            var transfer = SingleTransfer(account, TransferKind.NoteWithdraw);
            CollectionAssert.AreEqual(new[] { $"{account}|NULL|-20|2|30", "NULL|NOTES|20|NULL|NULL" }, Entries(transfer));
        }

        [TestMethod]
        public void WithdrawNotes_MoreThanAStack_ComesInStacksOfAtMostAThousand()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 1000);
            GiveNotes(player, 1000);
            GiveNotes(player, 600);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            var result = VaultTestWorld.WithdrawNotes(player, 2500);

            Assert.AreEqual(VaultOutcome.NotesWithdrawn, result.Outcome, result.Message);
            CollectionAssert.AreEqual(new[] { 500, 1000, 1000 }, Notes(player).Select(n => n.StackSize ?? 1).OrderBy(s => s).ToList());
            CollectionAssert.AreEqual(new[] { 500L, 1000L, 1000L }, DatabaseNoteStacks(player.Guid.Full));
            Assert.AreEqual(100, Ledger.GetBalance(account));
            Assert.AreEqual(0, Count($"SELECT SUM(amount) FROM market_ledger_entry WHERE transfer_Id = {SingleTransfer(account, TransferKind.NoteWithdraw)};"));
        }

        [TestMethod]
        public void WithdrawNotes_MoreThanTheBalance_IsRefusedAndChangesNothing()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            var balanceRow = BalanceRow(account);

            var result = VaultTestWorld.WithdrawNotes(player, 11);

            Assert.AreEqual(VaultOutcome.InsufficientFunds, result.Outcome, result.Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Message));
            Assert.AreEqual(0, NotesInPacks(player));
            Assert.AreEqual(0, DatabaseNoteStacks(player.Guid.Full).Count, "no notes were created");
            Assert.AreEqual(balanceRow, BalanceRow(account), "the balance row is untouched");
            Assert.AreEqual(1, Count($"SELECT COUNT(DISTINCT transfer_Id) FROM market_ledger_entry WHERE account_Id = {account};"), "no transfer was written");

            // and nonsense amounts are refused too
            Assert.AreEqual(VaultOutcome.InvalidAmount, VaultTestWorld.WithdrawNotes(player, 0).Outcome);
            Assert.AreEqual(VaultOutcome.InvalidAmount, VaultTestWorld.WithdrawNotes(player, -5).Outcome);
            Assert.AreEqual(balanceRow, BalanceRow(account));
        }

        [TestMethod]
        public void WithdrawNotes_BalanceDropsBeforeTheSave_TheJobRefusesAndNothingChanges()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            // hold the save queue, so the withdrawal passes its world-thread check and waits behind this
            var gate = new System.Threading.ManualResetEventSlim();
            DatabaseManager.Shard.RemoveBiota(0x7FFFFFF0, _ => gate.Wait());

            VaultResult result = null;
            var done = new System.Threading.ManualResetEventSlim();
            VaultTestWorld.OnWorldThread(() => Vault.WithdrawNotes(player, 8, r => { result = r; done.Set(); }));

            // meanwhile something else (the web, an admin) spends most of the balance
            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                var spend = new Transfer { Kind = TransferKind.AdminAdjust, Memo = "test spend" };
                spend.Entries.Add(Ledger.PlayerEntry(account, -5));
                spend.Entries.Add(Ledger.SystemEntry(SystemAccount.Admin, 5));
                Assert.IsTrue(Ledger.TryAdd(context, spend));
                context.SaveChanges();
            }
            var balanceRow = BalanceRow(account);

            gate.Set();
            Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "the withdrawal reported a result");

            Assert.AreEqual(VaultOutcome.InsufficientFunds, result.Outcome, result.Message);
            Assert.AreEqual(5, result.Balance, "the player is told the balance the job saw");
            Assert.AreEqual(0, NotesInPacks(player));
            Assert.AreEqual(0, DatabaseNoteStacks(player.Guid.Full).Count, "no notes were created");
            Assert.AreEqual(balanceRow, BalanceRow(account));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_transfer t JOIN market_ledger_entry e ON e.transfer_Id = t.id WHERE e.account_Id = {account} AND t.kind = '{TransferKind.NoteWithdraw}';"));
        }

        [TestMethod]
        public void DepositNotes_ConcurrentJobsForOneAccount_NoDuplicateOrSkippedSequence()
        {
            // the jobs themselves, run side by side as the game's queue and (later) the Market API's writers would be: each retries a lost race from a fresh read
            const int writers = 4;
            const int jobsEach = 10;

            var account = VaultTestWorld.NewAccountId();
            var character = VaultTestWorld.NewPlayer(account).Guid.Full;
            var start = new System.Threading.Barrier(writers);
            var saved = 0L;
            var results = new System.Collections.Concurrent.ConcurrentBag<MarketJobResult>();

            var tasks = Enumerable.Range(0, writers).Select(w => System.Threading.Tasks.Task.Run(() =>
            {
                start.SignalAndWait();

                for (var i = 0; i < jobsEach; i++)
                {
                    // notes that were never saved: nothing to delete, only the ledger is written
                    var note = new NoteStack(0x7FF00000u + (uint)(account % 1000) * 100 + (uint)(w * jobsEach + i), 1);
                    var result = DatabaseManager.Shard.BaseDatabase.DepositNotes(account, character, new[] { note }, out _);
                    results.Add(result);

                    if (result == MarketJobResult.Saved)
                        System.Threading.Interlocked.Increment(ref saved);
                }
            })).ToArray();

            Assert.IsTrue(System.Threading.Tasks.Task.WaitAll(tasks, TimeSpan.FromMinutes(2)));

            Assert.IsTrue(results.All(r => r == MarketJobResult.Saved || r == MarketJobResult.Failed), "a job saves or fails, nothing else");
            Assert.IsTrue(saved > writers, "the retries let most jobs through");

            var sequences = MarketTestDatabase.Rows(Db, $"SELECT sequence FROM market_ledger_entry WHERE account_Id = {account} ORDER BY sequence;").Select(long.Parse).ToList();
            CollectionAssert.AreEqual(Enumerable.Range(1, (int)saved).Select(i => (long)i).ToList(), sequences, "one sequence per saved job, no gaps or duplicates");
            Assert.AreEqual($"{saved}|{saved}", string.Join("|", BalanceRow(account).Split('|').Take(2)));
            Assert.AreEqual(saved, Count($"SELECT COUNT(*) FROM market_item_event WHERE account_Id = {account};"), "a failed job wrote no item event");
        }

        // ---- failures inside the save

        [TestMethod]
        public void DepositNotes_SaveFails_NotesAndBalanceExactlyAsBefore()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            var note = GiveNotes(player, 30);
            var balanceRow = BalanceRow(account);
            var events = Count("SELECT COUNT(*) FROM market_item_event;");

            foreach (var table in new[] { "market_ledger_entry", "market_item_event" })
            {
                VaultResult result;
                using (FailInsertsInto(table))
                    result = VaultTestWorld.DepositNotes(player);

                Assert.AreEqual(VaultOutcome.SaveFailed, result.Outcome, $"{table}: {result.Message}");
                Assert.AreEqual(30, NotesInPacks(player), $"{table}: the notes are back in the pack");
                Assert.IsNotNull(player.GetInventoryItem(note.Guid), $"{table}: the same stack");
                CollectionAssert.AreEqual(new[] { 30L }, DatabaseNoteStacks(player.Guid.Full), $"{table}: the note row is still in the pack");
                Assert.AreEqual(balanceRow, BalanceRow(account), $"{table}: the balance row is untouched");
                Assert.AreEqual(1, Count($"SELECT COUNT(DISTINCT transfer_Id) FROM market_ledger_entry WHERE account_Id = {account};"), $"{table}: no transfer was written");
                Assert.AreEqual(events, Count("SELECT COUNT(*) FROM market_item_event;"), $"{table}: no item event was written");
            }

            // and the deposit works once the failure is gone
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            Assert.AreEqual(40, Ledger.GetBalance(account));
        }

        [TestMethod]
        public void WithdrawNotes_SaveFails_NotesAndBalanceExactlyAsBefore()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 50);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            var balanceRow = BalanceRow(account);

            foreach (var table in new[] { "market_ledger_entry", "biota" })
            {
                VaultResult result;
                using (FailInsertsInto(table))
                    result = VaultTestWorld.WithdrawNotes(player, 20);

                Assert.AreEqual(VaultOutcome.SaveFailed, result.Outcome, $"{table}: {result.Message}");
                Assert.AreEqual(0, NotesInPacks(player), $"{table}: no notes in the pack");
                Assert.AreEqual(0, DatabaseNoteStacks(player.Guid.Full).Count, $"{table}: no note rows");
                Assert.AreEqual(balanceRow, BalanceRow(account), $"{table}: the balance row is untouched");
                Assert.AreEqual(1, Count($"SELECT COUNT(DISTINCT transfer_Id) FROM market_ledger_entry WHERE account_Id = {account};"), $"{table}: no transfer was written");
            }

            Assert.AreEqual(VaultOutcome.NotesWithdrawn, VaultTestWorld.WithdrawNotes(player, 20).Outcome);
            Assert.AreEqual(20, NotesInPacks(player));
        }

        // ---- the commands

        [TestMethod]
        public void VaultCommands_Balance_MatchesTheSumOfTheAccountsLedgerEntries()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 45);

            VaultTestWorld.OnWorldThread(() => Command.Handlers.VaultCommands.HandleVault(player.Session, "deposit", "mmd"));
            VaultTestWorld.WaitUntil(() => NotesInPacks(player) == 0 && Ledger.GetBalance(account) == 45, "the note deposit");

            VaultTestWorld.OnWorldThread(() => Command.Handlers.VaultCommands.HandleVault(player.Session, "withdraw", "mmd", "12"));
            VaultTestWorld.WaitUntil(() => NotesInPacks(player) == 12, "the note withdrawal");

            VaultTestWorld.TakeSent(player);
            VaultTestWorld.OnWorldThread(() => Command.Handlers.VaultCommands.HandleVault(player.Session, "balance"));

            var shown = VaultTestWorld.Chats(VaultTestWorld.TakeSent(player)).Select(c => Regex.Match(c, @"balance is ([\d,]+) MMD")).Single(m => m.Success);
            var balance = long.Parse(shown.Groups[1].Value.Replace(",", ""));

            Assert.AreEqual(33, balance);
            Assert.AreEqual(Count($"SELECT SUM(amount) FROM market_ledger_entry WHERE account_Id = {account};"), balance, "the balance is the sum of the account's entries");
            Assert.AreEqual(Count($"SELECT balance_After FROM market_ledger_entry WHERE account_Id = {account} ORDER BY sequence DESC LIMIT 1;"), balance, "and its latest balance-after");
        }

        [TestMethod]
        public void VaultCommands_Mmd_IsInstantWithNoChannel()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 5);

            // a channel would take ten minutes: the waits below time out after 30 seconds
            using var channel = ChannelSeconds(600);

            VaultTestWorld.OnWorldThread(() => Command.Handlers.VaultCommands.HandleVault(player.Session, "deposit", "mmd"));
            Assert.IsFalse(player.IsVaultChannelling, "no channel for a note deposit");
            Assert.IsFalse(player.IsFrozen ?? false);
            VaultTestWorld.WaitUntil(() => Ledger.GetBalance(account) == 5, "the note deposit");

            VaultTestWorld.OnWorldThread(() => Command.Handlers.VaultCommands.HandleVault(player.Session, "withdraw", "mmd", "3"));
            Assert.IsFalse(player.IsVaultChannelling, "no channel for a note withdrawal");
            Assert.IsFalse(player.IsFrozen ?? false);
            VaultTestWorld.WaitUntil(() => NotesInPacks(player) == 3, "the note withdrawal");
            Assert.AreEqual(2, Ledger.GetBalance(account));
        }

        // ---- helpers

        private static WorldObject GiveNotes(Player player, int stackSize, Container into = null)
        {
            var notes = VaultTestWorld.NewItem(TradeNoteWcid);
            notes.SetStackSize(stackSize);
            return VaultTestWorld.Give(player, notes, into);
        }

        private static List<WorldObject> Notes(Player player)
        {
            return player.GetTradeNotes().Where(n => n.WeenieClassId == TradeNoteWcid).ToList();
        }

        private static int NotesInPacks(Player player)
        {
            var notes = 0;
            VaultTestWorld.OnWorldThread(() => notes = Notes(player).Sum(n => n.StackSize ?? 1));
            return notes;
        }

        /// <summary>
        /// The stack sizes of the note rows the database has directly in the container, smallest first
        /// </summary>
        private static List<long> DatabaseNoteStacks(uint containerGuid)
        {
            return MarketTestDatabase.Rows(Db, $@"SELECT s.value FROM biota b
                JOIN biota_properties_i_i_d c ON c.object_Id = b.id AND c.type = {(int)PropertyInstanceId.Container} AND c.value = {containerGuid}
                JOIN biota_properties_int s ON s.object_Id = b.id AND s.type = {(int)PropertyInt.StackSize}
                WHERE b.weenie_Class_Id = {TradeNoteWcid} ORDER BY s.value;").Select(long.Parse).ToList();
        }

        private static long SingleTransfer(uint account, string kind)
        {
            var transfers = MarketTestDatabase.Rows(Db, $"SELECT DISTINCT t.id FROM market_transfer t JOIN market_ledger_entry e ON e.transfer_Id = t.id WHERE e.account_Id = {account} AND t.kind = '{kind}';");
            Assert.AreEqual(1, transfers.Count, $"one {kind} transfer");
            return long.Parse(transfers[0]);
        }

        private static List<string> Entries(long transfer)
        {
            return MarketTestDatabase.Rows(Db, $"SELECT account_Id, system_Account, amount, sequence, balance_After FROM market_ledger_entry WHERE transfer_Id = {transfer} ORDER BY id;");
        }

        private static string BalanceRow(uint account)
        {
            return MarketTestDatabase.Rows(Db, $"SELECT balance, last_Sequence, row_Version FROM market_balance WHERE account_Id = {account};").SingleOrDefault();
        }
    }
}
