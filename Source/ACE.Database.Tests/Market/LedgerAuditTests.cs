using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Seam 3: the ledger audit, the market pause and the admin corrections against real MySQL.
    /// Each test gets a fresh shard, so a check that fails can only have failed on what that test planted.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class LedgerAuditTests
    {
        private const string Db = "ace_shard_market_audit";

        private const uint Admin = 1;
        private const uint AdminCharacter = 0x50000001;

        private static uint nextAccountId = 800000;

        private static readonly DateTime Now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        [ClassInitialize]
        public static void ClassSetup(TestContext context) => MarketTestDatabase.InitializeConfig();

        [TestInitialize]
        public void TestSetup()
        {
            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;
        }

        [TestCleanup]
        public void TestCleanup() => MarketTestDatabase.Drop(Db);

        // ---- the audit

        [TestMethod]
        public void Audit_CleanDatabase_PassesEveryCheck()
        {
            Assert.IsTrue(Audit().Passed, "an empty ledger passes");

            var alice = NewAccountId();
            var bob = NewAccountId();

            // every kind of transfer the market writes, the way it writes them
            DepositNotes(alice, 2000, 1000, 999, 1);
            DepositNotes(bob, 30, 30);
            Write(TransferKind.NoteWithdraw, Ledger.PlayerEntry(alice, -20), Ledger.SystemEntry(SystemAccount.Notes, 20));
            var purchase = Write(TransferKind.Purchase, Ledger.PlayerEntry(bob, -25), Ledger.PlayerEntry(alice, 25), Ledger.PlayerEntry(alice, -2, "fee"), Ledger.SystemEntry(SystemAccount.Fees, 2));
            Write(TransferKind.Purchase, Ledger.PlayerEntry(alice, -5), Ledger.PlayerEntry(bob, 5), Ledger.PlayerEntry(bob, 0), Ledger.SystemEntry(SystemAccount.Fees, 0));

            Assert.AreEqual(CorrectionOutcome.Done, Adjust(bob, 40, "compensation").Outcome);
            Assert.AreEqual(CorrectionOutcome.Done, Adjust(alice, -3, "clawback").Outcome);
            Assert.AreEqual(CorrectionOutcome.Done, Reverse(purchase, "refund").Outcome);

            var report = Audit();

            Assert.IsTrue(report.Passed, Describe(report));
            Assert.AreEqual("Ledger audit passed", report.Summary);
        }

        [TestMethod]
        public void Audit_TransferThatDoesNotAddUpToZero_FailsTheTransferSumCheck()
        {
            DepositNotes(NewAccountId(), 10, 10);

            var unbalanced = PlantTransfer(TransferKind.Purchase, "(NULL, 'NOTES', 5, NULL, NULL)", "(NULL, 'FEES', -4, NULL, NULL)");
            var lonely = PlantTransfer(TransferKind.Purchase, "(NULL, 'FEES', 0, NULL, NULL)");

            var report = Audit();

            AssertFails(report, LedgerAuditCheck.TransferSum, $"transfer {unbalanced} ");
            AssertFails(report, LedgerAuditCheck.TransferSum, $"transfer {lonely} ");
            Assert.AreEqual(2, report.Failures.Count(f => f.Check == LedgerAuditCheck.TransferSum), Describe(report));
        }

        [TestMethod]
        public void Audit_BalanceEditedDirectly_FailsTheBalanceCheck()
        {
            var account = NewAccountId();
            var other = NewAccountId();
            DepositNotes(account, 10, 10);
            DepositNotes(other, 10, 10);

            MarketTestDatabase.Execute(Db, $"UPDATE market_balance SET balance = 17 WHERE account_Id = {account};");

            var report = Audit();

            AssertFails(report, LedgerAuditCheck.Balance, $"account {account} ");
            Assert.AreEqual(1, report.Failures.Count(f => f.Check == LedgerAuditCheck.Balance), "only the edited account: " + Describe(report));
            Assert.IsFalse(report.Failures.Any(f => f.Check == LedgerAuditCheck.Sequence || f.Check == LedgerAuditCheck.TransferSum), Describe(report));
        }

        [TestMethod]
        public void Audit_LatestBalanceAfterDisagreesWithTheBalance_FailsTheBalanceCheck()
        {
            var account = NewAccountId();
            DepositNotes(account, 10, 10);

            // a second transfer whose entries add up and whose sequence follows on, but whose balance-after is wrong
            PlantTransfer(TransferKind.NoteDeposit, $"({account}, NULL, 5, 2, 99)", "(NULL, 'NOTES', -5, NULL, NULL)");
            MarketTestDatabase.Execute(Db, $"UPDATE market_balance SET balance = 15, last_Sequence = 2 WHERE account_Id = {account};");

            var report = Audit();

            AssertFails(report, LedgerAuditCheck.Balance, $"account {account} ");
            Assert.IsFalse(report.Failures.Any(f => f.Check != LedgerAuditCheck.Balance && f.Check != LedgerAuditCheck.NoteEvents), Describe(report));
        }

        [TestMethod]
        public void Audit_GapInAnAccountsSequence_FailsTheSequenceCheck()
        {
            var account = NewAccountId();
            DepositNotes(account, 10, 10);

            // sequence 2 is skipped; the balance and its balance-after agree
            PlantTransfer(TransferKind.Purchase, $"({account}, NULL, 5, 3, 15)", "(NULL, 'FEES', -5, NULL, NULL)");
            MarketTestDatabase.Execute(Db, $"UPDATE market_balance SET balance = 15, last_Sequence = 3 WHERE account_Id = {account};");

            var report = Audit();

            AssertFails(report, LedgerAuditCheck.Sequence, $"account {account} ");
            Assert.IsFalse(report.Failures.Any(f => f.Check != LedgerAuditCheck.Sequence), Describe(report));
        }

        [TestMethod]
        public void Audit_LastSequenceAheadOfTheEntries_FailsTheSequenceCheck()
        {
            var account = NewAccountId();
            DepositNotes(account, 10, 10);

            MarketTestDatabase.Execute(Db, $"UPDATE market_balance SET last_Sequence = 2 WHERE account_Id = {account};");

            AssertFails(Audit(), LedgerAuditCheck.Sequence, $"account {account} ");
        }

        [TestMethod]
        public void Audit_PlayerBalancesDoNotMatchTheSystemAccounts_FailsTheSystemTotalCheck()
        {
            DepositNotes(NewAccountId(), 10, 10);

            // a balance with no entries behind it: money from nowhere
            var account = NewAccountId();
            MarketTestDatabase.Execute(Db, $"INSERT INTO market_balance (account_Id, balance) VALUES ({account}, 9);");

            var report = Audit();

            AssertFails(report, LedgerAuditCheck.SystemTotal, "players 19");
            AssertFails(report, LedgerAuditCheck.SystemTotal, "system -10");
        }

        [TestMethod]
        public void Audit_NoteDepositWithoutMatchingDestroyedNoteEvents_FailsTheNoteEventsCheck()
        {
            var account = NewAccountId();
            DepositNotes(account, 10, 10);

            var short3 = DepositNotes(account, 10, 3, 4);
            var none = DepositNotes(account, 5);

            // an event that lost its quantity counts for nothing
            var unknown = DepositNotes(account, 1);
            MarketTestDatabase.Execute(Db, $"INSERT INTO market_item_event (item_Guid, account_Id, kind, transfer_Id, quantity, event_Time) VALUES (1, {account}, 'deposit', {unknown}, NULL, UTC_TIMESTAMP(6));");

            var report = Audit();

            AssertFails(report, LedgerAuditCheck.NoteEvents, $"transfer {short3} ");
            AssertFails(report, LedgerAuditCheck.NoteEvents, $"transfer {none} ");
            AssertFails(report, LedgerAuditCheck.NoteEvents, $"transfer {unknown} ");
            Assert.AreEqual(3, report.Failures.Count, Describe(report));
        }

        // ---- the pause

        [TestMethod]
        public void RunAndPause_FailedAudit_PausesTheMarketWithTheReason_AndResumeLiftsIt()
        {
            var account = NewAccountId();
            DepositNotes(account, 10, 10);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                Assert.IsFalse(MarketPause.IsPaused(context), "not paused on a fresh shard");
                Assert.IsTrue(LedgerAudit.RunAndPause(context, "test", Now).Passed);
                Assert.IsFalse(MarketPause.IsPaused(context), "a passing audit doesn't pause");
            }

            MarketTestDatabase.Execute(Db, $"UPDATE market_balance SET balance = 11 WHERE account_Id = {account};");

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                var report = LedgerAudit.RunAndPause(context, "test", Now);

                Assert.IsFalse(report.Passed);

                var state = MarketPause.Get(context);
                Assert.IsTrue(state.Paused);
                StringAssert.Contains(state.Reason, "test");
                StringAssert.Contains(state.Reason, report.Summary);
            }

            // fixing the books doesn't lift the pause: only resume does
            MarketTestDatabase.Execute(Db, $"UPDATE market_balance SET balance = 10 WHERE account_Id = {account};");

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                Assert.IsTrue(LedgerAudit.RunAndPause(context, "test", Now).Passed);
                Assert.IsTrue(MarketPause.IsPaused(context), "still paused after a passing audit");

                MarketPause.Resume(context, "admin", Now);
            }

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                Assert.IsFalse(MarketPause.IsPaused(context));
                StringAssert.Contains(MarketPause.Get(context).Reason, "admin");
            }
        }

        [TestMethod]
        public void RunAndPause_AuditCannotRun_FailsClosedAndPausesTheMarket()
        {
            // a shard whose market tables are missing: the audit's query fails, but the pause row can still be written
            const string bare = "ace_shard_market_audit_bare";
            MarketTestDatabase.CreateFromBase(bare);

            try
            {
                using var context = MarketTestDatabase.CreateContext(bare);

                Assert.ThrowsExactly<MySqlConnector.MySqlException>(() => LedgerAudit.RunAndPause(context, "test", Now));

                var state = MarketPause.Get(context);
                Assert.IsTrue(state.Paused, "an audit that can't run doesn't leave money moving");
                StringAssert.Contains(state.Reason, "could not run");
            }
            finally
            {
                MarketTestDatabase.Drop(bare);
            }
        }

        // ---- admin corrections

        [TestMethod]
        public void Adjust_WritesAZeroSumAdminTransferWithTheMemoAndTheAdmin()
        {
            var account = NewAccountId();
            DepositNotes(account, 10, 10);

            var credit = Adjust(account, 25, "lost to a server crash");

            Assert.AreEqual(CorrectionOutcome.Done, credit.Outcome);
            Assert.AreEqual(35, credit.Balance);
            Assert.AreEqual($"admin_adjust|{Admin}|{AdminCharacter}|lost to a server crash|NULL", TransferRow(credit.TransferId));
            CollectionAssert.AreEqual(new[] { $"{account}|NULL|25|2|35", "NULL|ADMIN|-25|NULL|NULL" }, Entries(credit.TransferId));

            var debit = Adjust(account, -35, "dupe");

            Assert.AreEqual(CorrectionOutcome.Done, debit.Outcome);
            Assert.AreEqual(0, debit.Balance);
            CollectionAssert.AreEqual(new[] { $"{account}|NULL|-35|3|0", "NULL|ADMIN|35|NULL|NULL" }, Entries(debit.TransferId));

            var fresh = NewAccountId();
            Assert.AreEqual(CorrectionOutcome.Done, Adjust(fresh, 5, "grant").Outcome, "an account with no balance row yet");
            Assert.AreEqual("5|1", Balance(fresh));

            Assert.IsTrue(Audit().Passed, Describe(Audit()));
        }

        [TestMethod]
        public void Adjust_Refusals_WriteNothing()
        {
            var account = NewAccountId();
            DepositNotes(account, 10, 10);
            var transfers = TransferCount();

            Assert.AreEqual(CorrectionOutcome.InsufficientFunds, Adjust(account, -11, "too much").Outcome);
            Assert.AreEqual(CorrectionOutcome.InvalidAmount, Adjust(account, 0, "nothing").Outcome);
            Assert.AreEqual(CorrectionOutcome.InvalidMemo, Adjust(account, 5, "").Outcome);
            Assert.AreEqual(CorrectionOutcome.InvalidMemo, Adjust(account, 5, "   ").Outcome);
            Assert.AreEqual(CorrectionOutcome.InvalidMemo, Adjust(account, 5, new string('x', 513)).Outcome);

            Assert.AreEqual(transfers, TransferCount());
            Assert.AreEqual("10|1", Balance(account));
        }

        [TestMethod]
        public void Reverse_NegatesEveryEntryOnce_AndASecondReversalIsRefused()
        {
            var buyer = NewAccountId();
            var seller = NewAccountId();
            DepositNotes(buyer, 100, 100);
            var purchase = Write(TransferKind.Purchase, Ledger.PlayerEntry(buyer, -40), Ledger.PlayerEntry(seller, 40), Ledger.PlayerEntry(seller, -3, "fee"), Ledger.SystemEntry(SystemAccount.Fees, 3));

            var reversal = Reverse(purchase, "sold by mistake");

            Assert.AreEqual(CorrectionOutcome.Done, reversal.Outcome);
            Assert.AreEqual($"reversal|{Admin}|{AdminCharacter}|sold by mistake|{purchase}", TransferRow(reversal.TransferId));
            // undone in reverse order, so the seller's fee comes back before the price goes: no balance dips below zero on the way
            CollectionAssert.AreEqual(new[]
            {
                "NULL|FEES|-3|NULL|NULL",
                $"{seller}|NULL|3|3|40",
                $"{seller}|NULL|-40|4|0",
                $"{buyer}|NULL|40|3|100",
            }, Entries(reversal.TransferId));
            Assert.AreEqual("100|3", Balance(buyer));
            Assert.AreEqual("0|4", Balance(seller));

            var again = Reverse(purchase, "again");
            Assert.AreEqual(CorrectionOutcome.AlreadyReversed, again.Outcome);
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_transfer WHERE reverses_Transfer_Id = {purchase};"));
            Assert.AreEqual("100|3", Balance(buyer));

            Assert.IsTrue(Audit().Passed, Describe(Audit()));
        }

        [TestMethod]
        public void Reverse_Refusals_WriteNothing()
        {
            var account = NewAccountId();
            var deposit = DepositNotes(account, 10, 10);
            Write(TransferKind.NoteWithdraw, Ledger.PlayerEntry(account, -8), Ledger.SystemEntry(SystemAccount.Notes, 8));
            var transfers = TransferCount();

            Assert.AreEqual(CorrectionOutcome.UnknownTransfer, Reverse(999999, "no such transfer").Outcome);
            Assert.AreEqual(CorrectionOutcome.InsufficientFunds, Reverse(deposit, "the 10 are mostly spent").Outcome);
            Assert.AreEqual(CorrectionOutcome.InvalidMemo, Reverse(deposit, " ").Outcome);

            Assert.AreEqual(transfers, TransferCount());
            Assert.AreEqual("2|2", Balance(account));
        }

        [TestMethod]
        public void Reverse_ManyAdminsAtOnce_ExactlyOneReverses()
        {
            var account = NewAccountId();
            var deposit = DepositNotes(account, 50, 50);

            var outcomes = Enumerable.Range(0, 6).AsParallel().WithDegreeOfParallelism(6).Select(_ => Reverse(deposit, "race").Outcome).ToList();

            Assert.AreEqual(1, outcomes.Count(o => o == CorrectionOutcome.Done), string.Join(",", outcomes));
            Assert.AreEqual(5, outcomes.Count(o => o == CorrectionOutcome.AlreadyReversed), string.Join(",", outcomes));
            Assert.AreEqual("0|2", Balance(account));
        }

        [TestMethod]
        public void Reverse_AReversal_CanItselfBeReversedOnce()
        {
            var account = NewAccountId();
            DepositNotes(account, 10, 10);
            var adjust = Adjust(account, 5, "grant").TransferId;

            var undo = Reverse(adjust, "granted in error");
            Assert.AreEqual(CorrectionOutcome.Done, undo.Outcome);
            Assert.AreEqual("10|3", Balance(account));

            Assert.AreEqual(CorrectionOutcome.Done, Reverse(undo.TransferId, "the grant was right after all").Outcome);
            Assert.AreEqual("15|4", Balance(account));
            Assert.AreEqual(CorrectionOutcome.AlreadyReversed, Reverse(undo.TransferId, "again").Outcome);

            Assert.IsTrue(Audit().Passed, Describe(Audit()));
        }

        // ---- helpers

        private static uint NewAccountId() => Interlocked.Increment(ref nextAccountId);

        private static LedgerAuditReport Audit()
        {
            using var context = MarketTestDatabase.CreateContext(Db);
            return LedgerAudit.Run(context);
        }

        private static string Describe(LedgerAuditReport report) => string.Join("; ", report.Failures.Select(f => $"{f.Check}: {f.Detail}"));

        private static void AssertFails(LedgerAuditReport report, string check, string detail)
        {
            Assert.IsTrue(report.Failures.Any(f => f.Check == check && f.Detail.Contains(detail, StringComparison.Ordinal)), $"expected {check} naming '{detail}', got: {Describe(report)}");
        }

        private static CorrectionResult Adjust(uint account, long amount, string memo) =>
            LedgerCorrections.Adjust(() => MarketTestDatabase.CreateContext(Db), account, amount, memo, Admin, AdminCharacter, Now);

        private static CorrectionResult Reverse(long transferId, string memo) =>
            LedgerCorrections.Reverse(() => MarketTestDatabase.CreateContext(Db), transferId, memo, Admin, AdminCharacter, Now);

        /// <summary>
        /// A note_deposit of amount, with a destroyed-note deposit event of each given quantity, as ShardDatabase.DepositNotes writes them. Returns the transfer id.
        /// </summary>
        private static long DepositNotes(uint account, long amount, params int[] eventQuantities)
        {
            using var context = MarketTestDatabase.CreateContext(Db);

            var transfer = NewTransfer(TransferKind.NoteDeposit, Ledger.PlayerEntry(account, amount), Ledger.SystemEntry(SystemAccount.Notes, -amount));
            Assert.IsTrue(Ledger.TryAdd(context, transfer));

            foreach (var quantity in eventQuantities)
                context.MarketItemEvents.Add(new ItemEvent { ItemGuid = 0x80000000u + account, AccountId = account, Kind = ItemEventKind.Deposit, Transfer = transfer, Quantity = quantity, EventTime = Now });

            context.SaveChanges();

            return transfer.Id;
        }

        private static long Write(string kind, params LedgerEntry[] entries)
        {
            using var context = MarketTestDatabase.CreateContext(Db);

            var transfer = NewTransfer(kind, entries);
            Assert.IsTrue(Ledger.TryAdd(context, transfer), "the seeded transfer would overdraw an account");
            context.SaveChanges();

            return transfer.Id;
        }

        private static Transfer NewTransfer(string kind, params LedgerEntry[] entries)
        {
            var transfer = new Transfer { Kind = kind, CreatedTime = Now };

            foreach (var entry in entries)
                transfer.Entries.Add(entry);

            return transfer;
        }

        /// <summary>
        /// A transfer written with SQL, past the ledger service's checks. Each entry is "(account_Id, system_Account, amount, sequence, balance_After)".
        /// </summary>
        private static long PlantTransfer(string kind, params string[] entries)
        {
            MarketTestDatabase.Execute(Db, $"INSERT INTO market_transfer (kind, created_Time) VALUES ('{kind}', UTC_TIMESTAMP(6));");
            var id = MarketTestDatabase.Scalar(Db, "SELECT MAX(id) FROM market_transfer;");

            foreach (var entry in entries)
                MarketTestDatabase.Execute(Db, $"INSERT INTO market_ledger_entry (transfer_Id, account_Id, system_Account, amount, sequence, balance_After) VALUES ({id}, {entry.Trim('(', ')')});");

            return id;
        }

        private static string TransferRow(long id) =>
            MarketTestDatabase.Rows(Db, $"SELECT kind, actor_Account_Id, actor_Character_Id, memo, reverses_Transfer_Id FROM market_transfer WHERE id = {id};").Single();

        private static List<string> Entries(long transferId) =>
            MarketTestDatabase.Rows(Db, $"SELECT account_Id, system_Account, amount, sequence, balance_After FROM market_ledger_entry WHERE transfer_Id = {transferId} ORDER BY id;");

        private static string Balance(uint account) =>
            MarketTestDatabase.Rows(Db, $"SELECT balance, last_Sequence FROM market_balance WHERE account_Id = {account};").Single();

        private static long TransferCount() => MarketTestDatabase.Scalar(Db, "SELECT COUNT(*) FROM market_transfer;");
    }
}
