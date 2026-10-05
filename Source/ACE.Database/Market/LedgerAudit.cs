using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    /// <summary>
    /// The names of the ledger audit's checks
    /// </summary>
    public static class LedgerAuditCheck
    {
        /// <summary>
        /// Every transfer has two or more entries that add up to zero
        /// </summary>
        public const string TransferSum = "transfer_sum";

        /// <summary>
        /// Each balance equals the sum of its account's entries and its latest balance-after
        /// </summary>
        public const string Balance = "balance";

        /// <summary>
        /// Each account's sequence numbers run 1, 2, 3 ... up to its last sequence, with no gaps
        /// </summary>
        public const string Sequence = "sequence";

        /// <summary>
        /// All player balances add up to minus the system accounts' totals
        /// </summary>
        public const string SystemTotal = "system_total";

        /// <summary>
        /// Every note deposit's amount equals the quantity of the destroyed-note (deposit) item events that name it
        /// </summary>
        public const string NoteEvents = "note_events";
    }

    public sealed record LedgerAuditFailure(string Check, string Detail);

    public sealed class LedgerAuditReport
    {
        public IReadOnlyList<LedgerAuditFailure> Failures { get; }

        public bool Passed => Failures.Count == 0;

        public LedgerAuditReport(IReadOnlyList<LedgerAuditFailure> failures)
        {
            Failures = failures;
        }

        /// <summary>
        /// One line for logs and admins: passed, or how many failures of which checks
        /// </summary>
        public string Summary => Passed
            ? "Ledger audit passed"
            : $"Ledger audit FAILED: {Failures.Count} problem{(Failures.Count == 1 ? "" : "s")} ({string.Join(", ", Failures.GroupBy(f => f.Check).Select(g => $"{g.Key} x{g.Count()}"))})";
    }

    /// <summary>
    /// The ledger audit, the one used at Market API startup, every hour, and by /market audit.
    /// Every check is one SELECT of a single SQL statement, so they all read the same InnoDB snapshot: a transfer that commits while the audit runs
    /// is either seen whole by every check or by none, and can't look like a mismatch.
    /// Aggregates are grouped in derived tables and formatted outside them: MySQL 8.0.40 returns wrong rows for CONCAT over aggregates
    /// in a grouped UNION branch with ORDER BY and LIMIT.
    /// </summary>
    public static class LedgerAudit
    {
        /// <summary>
        /// The most failures reported per query of a check, so a badly broken ledger still gives a readable report.
        /// The balance and sequence checks are two queries each, so they can report up to twice this.
        /// </summary>
        public const int FailuresPerCheck = 50;

        private static readonly string AuditSql = $@"
(SELECT '{LedgerAuditCheck.TransferSum}' AS `Check`,
        CONCAT('transfer ', x.id, ' (', x.kind, ') has ', x.n, ' entries adding up to ', x.total) AS `Detail`
   FROM (SELECT t.id, t.kind, COUNT(e.id) AS n, IFNULL(SUM(e.amount), 0) AS total
           FROM market_transfer t
           LEFT JOIN market_ledger_entry e ON e.transfer_Id = t.id
          GROUP BY t.id, t.kind) x
  WHERE x.n < 2 OR x.total <> 0
  ORDER BY x.id LIMIT {FailuresPerCheck})
UNION ALL
(SELECT '{LedgerAuditCheck.Balance}',
        CONCAT('account ', b.account_Id, ' has balance ', b.balance, ', its entries add up to ', IFNULL(s.total, 0), ', latest balance-after ', IFNULL(l.balance_After, 'none'))
   FROM market_balance b
   LEFT JOIN (SELECT account_Id, SUM(amount) AS total, MAX(sequence) AS last FROM market_ledger_entry WHERE account_Id IS NOT NULL GROUP BY account_Id) s ON s.account_Id = b.account_Id
   LEFT JOIN market_ledger_entry l ON l.account_Id = b.account_Id AND l.sequence = s.last
  WHERE b.balance <> IFNULL(s.total, 0) OR b.balance <> IFNULL(l.balance_After, 0)
  ORDER BY b.account_Id LIMIT {FailuresPerCheck})
UNION ALL
(SELECT '{LedgerAuditCheck.Balance}',
        CONCAT('account ', s.account_Id, ' has entries adding up to ', s.total, ' but no balance row')
   FROM (SELECT account_Id, SUM(amount) AS total FROM market_ledger_entry WHERE account_Id IS NOT NULL GROUP BY account_Id) s
   LEFT JOIN market_balance b ON b.account_Id = s.account_Id
  WHERE b.account_Id IS NULL
  ORDER BY s.account_Id LIMIT {FailuresPerCheck})
UNION ALL
(SELECT '{LedgerAuditCheck.Sequence}',
        CONCAT('account ', x.account_Id, ' has ', x.n, ' entries numbered ', x.first, ' to ', x.last, ', last sequence ', IFNULL(b.last_Sequence, 'none'))
   FROM (SELECT account_Id, COUNT(*) AS n, MIN(sequence) AS first, MAX(sequence) AS last FROM market_ledger_entry WHERE account_Id IS NOT NULL GROUP BY account_Id) x
   LEFT JOIN market_balance b ON b.account_Id = x.account_Id
  WHERE x.first <> 1 OR x.last <> x.n OR IFNULL(b.last_Sequence, x.last) <> x.last
  ORDER BY x.account_Id LIMIT {FailuresPerCheck})
UNION ALL
(SELECT '{LedgerAuditCheck.Sequence}',
        CONCAT('account ', b.account_Id, ' has last sequence ', b.last_Sequence, ' but no entries')
   FROM market_balance b
  WHERE b.last_Sequence <> 0 AND NOT EXISTS (SELECT 1 FROM market_ledger_entry e WHERE e.account_Id = b.account_Id)
  ORDER BY b.account_Id LIMIT {FailuresPerCheck})
UNION ALL
(SELECT '{LedgerAuditCheck.SystemTotal}',
        CONCAT('players ', p.total, ', system ', s.total, ': player balances should equal minus the system accounts')
   FROM (SELECT IFNULL(SUM(balance), 0) AS total FROM market_balance) p
  CROSS JOIN (SELECT IFNULL(SUM(amount), 0) AS total FROM market_ledger_entry WHERE system_Account IS NOT NULL) s
  WHERE p.total + s.total <> 0)
UNION ALL
(SELECT '{LedgerAuditCheck.NoteEvents}',
        CONCAT('transfer ', t.id, ' deposited ', IFNULL(d.amount, 0), ' MMD but its destroyed-note events hold ', IFNULL(v.quantity, 0))
   FROM market_transfer t
   LEFT JOIN (SELECT transfer_Id, SUM(amount) AS amount FROM market_ledger_entry WHERE account_Id IS NOT NULL GROUP BY transfer_Id) d ON d.transfer_Id = t.id
   LEFT JOIN (SELECT transfer_Id, SUM(quantity) AS quantity FROM market_item_event WHERE kind = '{ItemEventKind.Deposit}' AND transfer_Id IS NOT NULL GROUP BY transfer_Id) v ON v.transfer_Id = t.id
  WHERE t.kind = '{TransferKind.NoteDeposit}' AND IFNULL(d.amount, 0) <> IFNULL(v.quantity, 0)
  ORDER BY t.id LIMIT {FailuresPerCheck})";

        /// <summary>
        /// Runs every check. Only reads.
        /// </summary>
        public static LedgerAuditReport Run(ShardDbContext context)
        {
            var failures = context.Database.SqlQueryRaw<AuditRow>(AuditSql).AsEnumerable().Select(r => new LedgerAuditFailure(r.Check, r.Detail)).ToList();

            return new LedgerAuditReport(failures);
        }

        /// <summary>
        /// Runs every check and, if any fails, pauses the market (purchases and MMD withdrawals) until an admin resumes it. A passing audit never lifts a pause.
        /// An audit that can't run fails closed: it pauses the market too (if the pause can still be written), then throws.
        /// </summary>
        /// <param name="source">what ran the audit, for the pause's reason: "Market API startup", "hourly", "/market audit by ..."</param>
        public static LedgerAuditReport RunAndPause(ShardDbContext context, string source, DateTime now)
        {
            LedgerAuditReport report;

            try
            {
                report = Run(context);
            }
            catch (Exception ex)
            {
                try
                {
                    MarketPause.Pause(context, $"the ledger audit could not run ({source}): {ex.Message}", now);
                }
                catch (Exception)
                {
                    // the database is unreachable: nothing can move money now anyway, and the caller logs the audit's own error
                }

                throw;
            }

            if (!report.Passed)
                MarketPause.Pause(context, $"{report.Summary}, found by {source}. First: {report.Failures[0].Check}: {report.Failures[0].Detail}", now);

            return report;
        }

        private sealed class AuditRow
        {
            public string Check { get; set; }

            public string Detail { get; set; }
        }
    }
}
