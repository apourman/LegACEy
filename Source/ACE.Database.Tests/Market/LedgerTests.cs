using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Seam 3: the ledger service against real MySQL. Every transfer is written with the caller's own context and one SaveChanges;
    /// the balance row's row version is what keeps two writers from taking the same sequence number.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class LedgerTests
    {
        private const string Db = "ace_shard_market_ledger";

        private static uint nextAccountId = 700000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;
        }

        [ClassCleanup]
        public static void TestCleanup() => MarketTestDatabase.Drop(Db);

        [TestMethod]
        public void TryAdd_PlayerAndSystemEntries_OnlyPlayerEntriesGetSequenceAndBalanceAfter()
        {
            var account = NewAccountId();

            Assert.IsTrue(Write(account, 50));
            Assert.IsTrue(Write(account, 10));

            var entries = MarketTestDatabase.Rows(Db, $"SELECT e.account_Id, e.system_Account, e.amount, e.sequence, e.balance_After FROM market_ledger_entry e JOIN market_ledger_entry p ON p.transfer_Id = e.transfer_Id AND p.account_Id = {account} ORDER BY e.transfer_Id, e.id;");

            CollectionAssert.AreEqual(new[]
            {
                $"{account}|NULL|50|1|50",
                "NULL|NOTES|-50|NULL|NULL",
                $"{account}|NULL|10|2|60",
                "NULL|NOTES|-10|NULL|NULL",
            }, entries);

            Assert.AreEqual("60|2|1", Balance(account), "balance, last sequence, and a row version that starts at 0 and is bumped by each later transfer");

            using var context = MarketTestDatabase.CreateContext(Db);
            Assert.AreEqual(60, Ledger.GetBalance(context, account));
            Assert.AreEqual(0, Ledger.GetBalance(context, NewAccountId()), "no balance row reads as 0");
        }

        [TestMethod]
        public void TryAdd_OneAccountTwiceInATransfer_TakesTwoSequences()
        {
            var buyer = NewAccountId();
            var seller = NewAccountId();
            Assert.IsTrue(Write(buyer, 100));

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                // the purchase shape: buyer -price, seller +price, seller -fee, FEES +fee
                var transfer = NewTransfer(TransferKind.Purchase,
                    Ledger.PlayerEntry(buyer, -40), Ledger.PlayerEntry(seller, 40), Ledger.PlayerEntry(seller, -3), Ledger.SystemEntry(SystemAccount.Fees, 3));

                Assert.IsTrue(Ledger.TryAdd(context, transfer));
                context.SaveChanges();
            }

            CollectionAssert.AreEqual(new[] { "40|1|40", "-3|2|37" }, MarketTestDatabase.Rows(Db, $"SELECT amount, sequence, balance_After FROM market_ledger_entry WHERE account_Id = {seller} ORDER BY sequence;"));
            Assert.AreEqual("37|2|0", Balance(seller));
            Assert.AreEqual("60|2|1", Balance(buyer));
        }

        [TestMethod]
        public void TryAdd_MalformedTransfer_Throws()
        {
            var account = NewAccountId();

            using var context = MarketTestDatabase.CreateContext(Db);

            Assert.ThrowsExactly<ArgumentException>(() => Ledger.TryAdd(context, NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, 5), Ledger.SystemEntry(SystemAccount.Notes, -4))), "entries must add up to zero");
            Assert.ThrowsExactly<ArgumentException>(() => Ledger.TryAdd(context, NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, 0))), "a transfer has two or more entries");
            Assert.ThrowsExactly<ArgumentException>(() => Ledger.TryAdd(context, NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, 5), Ledger.SystemEntry("BANK", -5))), "only the known system accounts");

            Assert.AreEqual(0, context.ChangeTracker.Entries().Count(), "nothing was added");
        }

        [TestMethod]
        public void TryAdd_BalanceWouldGoNegative_ReturnsFalseAndAddsNothing()
        {
            var account = NewAccountId();
            Assert.IsTrue(Write(account, 10));

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                Assert.IsFalse(Ledger.TryAdd(context, NewTransfer(TransferKind.NoteWithdraw, Ledger.PlayerEntry(account, -11), Ledger.SystemEntry(SystemAccount.Notes, 11))));
                Assert.IsFalse(context.ChangeTracker.Entries().Any(e => e.State != EntityState.Unchanged), "nothing to save");
                context.SaveChanges();
            }

            Assert.AreEqual("10|1|0", Balance(account));
            Assert.AreEqual(1, TransferCount(account));
        }

        [TestMethod]
        public void TryAdd_SavedWithTheCallersOtherChanges_InOneSaveOrNotAtAll()
        {
            var account = NewAccountId();
            Assert.IsTrue(Write(account, 10));

            // the caller's own change fails inside the same SaveChanges: the transfer and the balance change go with it
            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                Assert.IsTrue(Ledger.TryAdd(context, NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, 5), Ledger.SystemEntry(SystemAccount.Notes, -5))));
                context.MarketItemEvents.Add(new ItemEvent { ItemGuid = 1, AccountId = account, Kind = "not_a_kind", EventTime = DateTime.UtcNow });

                Assert.ThrowsExactly<DbUpdateException>(() => context.SaveChanges());
            }

            Assert.AreEqual("10|1|0", Balance(account));
            Assert.AreEqual(1, TransferCount(account));
        }

        [TestMethod]
        public void ConcurrentWriters_SecondSaveOnAStaleBalance_ConflictsAndWritesNothing()
        {
            var account = NewAccountId();
            Assert.IsTrue(Write(account, 1));

            using (var first = MarketTestDatabase.CreateContext(Db))
            using (var second = MarketTestDatabase.CreateContext(Db))
            {
                // both read the balance row at sequence 1 before either saves
                Assert.IsTrue(Ledger.TryAdd(first, NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, 1), Ledger.SystemEntry(SystemAccount.Notes, -1))));
                Assert.IsTrue(Ledger.TryAdd(second, NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, 1), Ledger.SystemEntry(SystemAccount.Notes, -1))));

                first.SaveChanges();

                Assert.ThrowsExactly<DbUpdateConcurrencyException>(() => second.SaveChanges(), "the stale writer would have reused sequence 2");
            }

            // a retry with a fresh read takes the next sequence
            Assert.IsTrue(Write(account, 1));

            CollectionAssert.AreEqual(new[] { "1|1", "2|2", "3|3" }, MarketTestDatabase.Rows(Db, $"SELECT sequence, balance_After FROM market_ledger_entry WHERE account_Id = {account} ORDER BY sequence;"));
            Assert.AreEqual(3, TransferCount(account), "the conflicting save wrote no transfer");
            Assert.AreEqual("3|3|2", Balance(account));
        }

        [TestMethod]
        public void ConcurrentWriters_FirstEverWriteForAnAccount_OneWinsTheOtherWritesNothing()
        {
            var account = NewAccountId();

            using (var first = MarketTestDatabase.CreateContext(Db))
            using (var second = MarketTestDatabase.CreateContext(Db))
            {
                Assert.IsTrue(Ledger.TryAdd(first, NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, 1), Ledger.SystemEntry(SystemAccount.Notes, -1))));
                Assert.IsTrue(Ledger.TryAdd(second, NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, 1), Ledger.SystemEntry(SystemAccount.Notes, -1))));

                first.SaveChanges();

                // both tried to insert the account's balance row
                Assert.ThrowsExactly<DbUpdateException>(() => second.SaveChanges());
            }

            Assert.AreEqual(1, TransferCount(account));
            Assert.AreEqual("1|1|0", Balance(account));
        }

        [TestMethod]
        public void ConcurrentWriters_ManyThreads_SequencesHaveNoGapsOrDuplicates()
        {
            const int writers = 4;
            const int writesEach = 25;

            var account = NewAccountId();
            var conflicts = 0;
            var start = new Barrier(writers);

            var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();

                for (var i = 0; i < writesEach; i++)
                {
                    // a writer that loses the race re-reads and tries again, as callers of the ledger do
                    while (!Write(account, 1, throwOnConflict: false))
                        Interlocked.Increment(ref conflicts);
                }
            })).ToArray();

            Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromMinutes(2)), "the writers finished");

            const int total = writers * writesEach;

            var sequences = MarketTestDatabase.Rows(Db, $"SELECT sequence FROM market_ledger_entry WHERE account_Id = {account} ORDER BY sequence;").Select(long.Parse).ToList();
            CollectionAssert.AreEqual(Enumerable.Range(1, total).Select(i => (long)i).ToList(), sequences, "every sequence from 1 to the total, once each");

            var balanceAfters = MarketTestDatabase.Rows(Db, $"SELECT balance_After FROM market_ledger_entry WHERE account_Id = {account} ORDER BY sequence;").Select(long.Parse).ToList();
            CollectionAssert.AreEqual(Enumerable.Range(1, total).Select(i => (long)i).ToList(), balanceAfters);

            Assert.AreEqual(total, TransferCount(account));
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, "SELECT COUNT(*) FROM (SELECT transfer_Id FROM market_ledger_entry GROUP BY transfer_Id HAVING SUM(amount) <> 0) t;"), "every transfer adds up to zero");
            Assert.AreEqual($"{total}|{total}", string.Join("|", Balance(account).Split('|').Take(2)));

            Console.WriteLine($"{conflicts} conflicting saves were retried");
        }

        // ---- helpers

        private static uint NewAccountId() => Interlocked.Increment(ref nextAccountId);

        private static Transfer NewTransfer(string kind, params LedgerEntry[] entries)
        {
            var transfer = new Transfer { Kind = kind };

            foreach (var entry in entries)
                transfer.Entries.Add(entry);

            return transfer;
        }

        /// <summary>
        /// A note_deposit of amount for the account, in its own context and save. False if the save lost a race (only when not throwing).
        /// </summary>
        private static bool Write(uint account, long amount, bool throwOnConflict = true)
        {
            using var context = MarketTestDatabase.CreateContext(Db);

            if (!Ledger.TryAdd(context, NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, amount), Ledger.SystemEntry(SystemAccount.Notes, -amount))))
                return false;

            try
            {
                context.SaveChanges();
                return true;
            }
            catch (DbUpdateException) when (!throwOnConflict)
            {
                // a stale row version (DbUpdateConcurrencyException) or two first writes racing to insert the balance row
                return false;
            }
        }

        private static string Balance(uint account)
        {
            return MarketTestDatabase.Rows(Db, $"SELECT balance, last_Sequence, row_Version FROM market_balance WHERE account_Id = {account};").SingleOrDefault();
        }

        private static long TransferCount(uint account)
        {
            return MarketTestDatabase.Scalar(Db, $"SELECT COUNT(DISTINCT transfer_Id) FROM market_ledger_entry WHERE account_Id = {account};");
        }
    }
}
