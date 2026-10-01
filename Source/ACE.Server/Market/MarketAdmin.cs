using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using log4net;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Models;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;

namespace ACE.Server.Market
{
    /// <summary>
    /// The game's side of market administration: the ledger audit on demand, lifting the pause, admin corrections to the ledger,
    /// the Vault's WCID blocklist and the search-column refresh. All of it runs off the world thread; each callback runs back on the world thread.
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
        /// Blocks a weenie class from new deposits, recorded with the admin's account, the reason and the time. Items of that class already in a Vault stay there.
        /// The callback gets the weenie's name and the block in force (an earlier block is kept), or Unknown if the world database has no such weenie.
        /// </summary>
        public static void Block(uint wcid, string reason, uint adminAccountId, Action<BlockResult> completed)
        {
            RunOffWorldThread(() =>
            {
                var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

                if (weenie == null)
                    return new BlockResult(BlockOutcome.UnknownWeenie, null, null, 0);

                using var context = new ShardDbContext();

                var block = VaultStore.Block(context, wcid, reason, adminAccountId, DateTime.UtcNow, out var added);
                var inVaults = context.MarketVaultItems.Count(r => r.Wcid == wcid);

                if (added)
                    log.Warn($"[MARKET] Admin account {adminAccountId} blocked WCID {wcid} ({weenie.GetName()}) from the Vault: {reason}");

                return new BlockResult(added ? BlockOutcome.Blocked : BlockOutcome.AlreadyBlocked, weenie.GetName(), block, inVaults);
            }, completed);
        }

        /// <summary>
        /// Lets a weenie class into the Vault again. The callback gets false if it wasn't blocked.
        /// </summary>
        public static void Unblock(uint wcid, string by, Action<bool?> completed)
        {
            RunOffWorldThread<bool?>(() =>
            {
                using var context = new ShardDbContext();

                var removed = VaultStore.Unblock(context, wcid);

                if (removed)
                    log.Warn($"[MARKET] WCID {wcid} unblocked from the Vault by {by}");

                return removed;
            }, completed);
        }

        /// <summary>
        /// Every blocked weenie class
        /// </summary>
        public static void Blocked(Action<List<BlockedWcid>> completed)
        {
            RunOffWorldThread(() =>
            {
                using var context = new ShardDbContext();
                return VaultStore.ListBlocked(context);
            }, completed);
        }

        /// <summary>
        /// Re-copies the Vault search columns of every escrowed item, or of one weenie class, from the item rows as the database holds them now.
        /// Run it after a shard SQL update changes items. Each item is loaded fresh, made into a WorldObject that is never added to the world or saved,
        /// and copied with the same Vault.NewVaultItem as a deposit; only the search columns of its Vault row are written.
        /// </summary>
        public static void Refresh(uint? wcid, string by, Action<RefreshReport> completed)
        {
            RunOffWorldThread(() =>
            {
                List<uint> guids;

                using (var context = new ShardDbContext())
                    guids = VaultStore.ItemGuids(context, wcid);

                int refreshed = 0, gone = 0, failed = 0;

                foreach (var guid in guids)
                {
                    switch (RefreshOne(guid))
                    {
                        case true: refreshed++; break;
                        case false: gone++; break;
                        default: failed++; break;
                    }
                }

                var what = wcid == null ? "every Vault item" : $"Vault items of WCID {wcid}";

                if (failed == 0)
                    log.Info($"[MARKET] {by} refreshed the search columns of {what}: {refreshed:N0} refreshed, {gone:N0} withdrawn meanwhile");
                else
                    log.Warn($"[MARKET] {by} refreshed the search columns of {what}: {refreshed:N0} refreshed, {gone:N0} withdrawn meanwhile, {failed:N0} could not be (see above)");

                return new RefreshReport(refreshed, gone, failed);
            }, completed);
        }

        /// <summary>
        /// True if the row was refreshed, false if its Vault row is gone (withdrawn meanwhile, a normal race), null if the item couldn't be loaded or made into an object (logged)
        /// </summary>
        private static bool? RefreshOne(uint guid)
        {
            try
            {
                var biota = DatabaseManager.Shard.BaseDatabase.GetBiotaUncached(guid);

                if (biota == null)
                {
                    log.Error($"[MARKET] Refresh: Vault item 0x{guid:X8} has no item row");
                    return null;
                }

                var item = WorldObjectFactory.CreateWorldObject(biota);

                if (item == null)
                {
                    log.Error($"[MARKET] Refresh: Vault item 0x{guid:X8} (WCID {biota.WeenieClassId}) could not be created from its item row");
                    return null;
                }

                // the owner and state columns NewVaultItem fills are not written by UpdateSearchColumns
                var columns = Vault.NewVaultItem(item, 0, 0);

                using var context = new ShardDbContext();

                return VaultStore.UpdateSearchColumns(context, columns);
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] Refresh: Vault item 0x{guid:X8} failed: {ex}");
                return null;
            }
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

    public enum BlockOutcome
    {
        Blocked,
        AlreadyBlocked,
        UnknownWeenie,
    }

    /// <param name="Block">the block in force: the new one, or the earlier one when AlreadyBlocked</param>
    /// <param name="InVaults">how many items of the class are in Vaults now (a block doesn't move them)</param>
    public sealed record BlockResult(BlockOutcome Outcome, string WeenieName, BlockedWcid Block, int InVaults);

    /// <param name="Gone">items withdrawn while the refresh ran: nothing to refresh</param>
    /// <param name="Failed">items whose columns couldn't be refreshed: logged one by one</param>
    public sealed record RefreshReport(int Refreshed, int Gone, int Failed);
}
