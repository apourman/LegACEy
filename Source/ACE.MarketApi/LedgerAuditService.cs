using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// When the Market API audits the ledger
    /// </summary>
    /// <param name="AtStartup">audit once as the API starts, before it serves requests</param>
    /// <param name="Every">then audit again every interval; null for never</param>
    public sealed record LedgerAuditSchedule(bool AtStartup, TimeSpan? Every)
    {
        /// <summary>
        /// At startup and every hour
        /// </summary>
        public static readonly LedgerAuditSchedule Default = new(true, TimeSpan.FromHours(1));

        public static readonly LedgerAuditSchedule Off = new(false, null);
    }

    /// <summary>
    /// Runs the ledger audit at startup and on the schedule. A failed audit is logged as critical, every failure on its own line, and pauses purchases
    /// and MMD withdrawals server-wide until an admin runs /market resume.
    /// </summary>
    public sealed class LedgerAuditService : BackgroundService
    {
        private readonly MarketDatabase database;
        private readonly LedgerAuditSchedule schedule;
        private readonly TimeProvider time;
        private readonly ILogger<LedgerAuditService> logger;

        public LedgerAuditService(MarketDatabase database, LedgerAuditSchedule schedule, TimeProvider time, ILogger<LedgerAuditService> logger)
        {
            this.database = database;
            this.schedule = schedule;
            this.time = time;
            this.logger = logger;
        }

        /// <summary>
        /// Hosted services start before the server does, so the startup audit has run (and paused, if it failed) before the first request
        /// </summary>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            if (schedule.AtStartup)
                Audit("the Market API startup audit");

            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (schedule.Every is not TimeSpan every)
                return;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(every, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Audit("the hourly audit");
            }
        }

        private void Audit(string source)
        {
            try
            {
                using var shard = database.CreateShard();

                var report = LedgerAudit.RunAndPause(shard, source, time.GetUtcNow().UtcDateTime);

                if (report.Passed)
                {
                    logger.LogInformation("{Summary} ({Source})", report.Summary, source);
                    return;
                }

                logger.LogCritical("{Summary}, found by {Source}. Purchases and MMD withdrawals are paused until an admin runs /market resume.", report.Summary, source);

                foreach (var failure in report.Failures)
                    logger.LogCritical("Ledger audit {Check}: {Detail}", failure.Check, failure.Detail);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The ledger audit ({Source}) could not run", source);
            }
        }
    }
}
