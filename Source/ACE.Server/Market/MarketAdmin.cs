using System;
using System.Linq;
using System.Threading.Tasks;

using log4net;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

namespace ACE.Server.Market
{
    /// <summary>
    /// The game's side of market administration: the ledger audit on demand, lifting the pause, and admin corrections to the ledger.
    /// The audit and the corrections run off the world thread; each callback runs back on the world thread.
    /// </summary>
    public static class MarketAdmin
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Runs the ledger audit, the same one the Market API runs at startup and hourly. A failure is logged as an error, every problem on its own line,
        /// and pauses purchases and MMD withdrawals until Resume.
        /// </summary>
        /// <param name="by">who asked, for the pause's reason</param>
        public static void Audit(string by, Action<LedgerAuditReport> completed)
        {
            RunOffWorldThread(() =>
            {
                using var context = new ShardDbContext();

                var report = LedgerAudit.RunAndPause(context, $"/market audit by {by}", DateTime.UtcNow);

                if (report.Passed)
                    log.Info($"[MARKET] {report.Summary} (/market audit by {by})");
                else
                {
                    log.Error($"[MARKET] {report.Summary}, found by /market audit by {by}. Purchases and MMD withdrawals are paused until /market resume.");

                    foreach (var failure in report.Failures)
                        log.Error($"[MARKET] Ledger audit {failure.Check}: {failure.Detail}");
                }

                return report;
            }, completed);
        }

        /// <summary>
        /// Lifts the pause. The callback gets false if the market wasn't paused (two admins resuming at once may both get true; the pause is lifted either way).
        /// </summary>
        public static void Resume(string by, Action<bool?> completed)
        {
            RunOffWorldThread<bool?>(() =>
            {
                using var context = new ShardDbContext();

                if (!MarketPause.IsPaused(context))
                    return false;

                MarketPause.Resume(context, by, DateTime.UtcNow);

                log.Warn($"[MARKET] Market resumed by {by}: purchases and MMD withdrawals are allowed again");

                return true;
            }, completed);
        }

        /// <summary>
        /// The account an admin named: an account id the market or the auth database knows, else an account name. Null if there is none.
        /// </summary>
        private static uint? FindAccount(string text)
        {
            if (uint.TryParse(text, out var id))
            {
                if (DatabaseManager.Authentication.GetAccountById(id) != null)
                    return id;

                using var context = new ShardDbContext();

                return context.MarketBalances.Any(b => b.AccountId == id) ? id : null;
            }

            return DatabaseManager.Authentication.GetAccountByName(text)?.AccountId;
        }

        /// <summary>
        /// Writes an admin_adjust transfer of amount MMD (negative to take MMD away) for the account, recorded with the admin's account and character and the memo.
        /// The account is an account name or id, looked up off the world thread (UnknownAccount if there is none).
        /// </summary>
        public static void Adjust(string account, long amount, string memo, uint adminAccountId, uint adminCharacterId, Action<CorrectionResult> completed)
        {
            RunOffWorldThread(() =>
            {
                if (FindAccount(account) is not uint accountId)
                    return new CorrectionResult(CorrectionOutcome.UnknownAccount);

                var result = LedgerCorrections.Adjust(() => new ShardDbContext(), accountId, amount, memo, adminAccountId, adminCharacterId, DateTime.UtcNow);

                if (result.Outcome == CorrectionOutcome.Done)
                    log.Warn($"[MARKET] Admin account {adminAccountId} adjusted account {accountId} by {amount:+#;-#} MMD (transfer {result.TransferId}): {memo}");

                return result;
            }, completed);
        }

        /// <summary>
        /// Writes a reversal of the transfer, recorded with the admin's account and character and the memo. A transfer can be reversed once.
        /// </summary>
        public static void Reverse(long transferId, string memo, uint adminAccountId, uint adminCharacterId, Action<CorrectionResult> completed)
        {
            RunOffWorldThread(() =>
            {
                var result = LedgerCorrections.Reverse(() => new ShardDbContext(), transferId, memo, adminAccountId, adminCharacterId, DateTime.UtcNow);

                if (result.Outcome == CorrectionOutcome.Done)
                    log.Warn($"[MARKET] Admin account {adminAccountId} reversed transfer {transferId} (transfer {result.TransferId}): {memo}");

                return result;
            }, completed);
        }

        /// <summary>
        /// Does the database work on the thread pool, so a long audit doesn't stall the world, then hands the result back to the world thread.
        /// If the work throws, the error is logged and the callback gets default (null): callers must treat null as "failed, see the log".
        /// </summary>
        private static void RunOffWorldThread<T>(Func<T> work, Action<T> completed)
        {
            Task.Run(() =>
            {
                T result;

                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] Market admin work failed: {ex}");
                    result = default;
                }

                WorldManager.EnqueueAction(new ActionEventDelegate(() => completed(result)));
            });
        }
    }
}
