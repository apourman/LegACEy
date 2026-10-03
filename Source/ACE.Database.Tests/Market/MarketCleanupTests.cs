using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Seam 3: the market's cleanup of rows nothing reads any more (stored request results, link codes, plugin tokens, web sessions), against real MySQL.
    /// Finished tickets are the game bridge's own cleanup and must be left alone.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class MarketCleanupTests
    {
        private const string Db = "ace_shard_market_cleanup";

        [TestInitialize]
        public void Setup()
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;
        }

        [TestCleanup]
        public void Cleanup() => MarketTestDatabase.Drop(Db);

        [TestMethod]
        public void Run_DeletesRequestsOlderThanThirtyDays_KeepsNewerOnes()
        {
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketRequests.Add(NewRequest("old", now.AddDays(-31)));
                context.MarketRequests.Add(NewRequest("just-over", now.AddDays(-30).AddSeconds(-1)));
                context.MarketRequests.Add(NewRequest("just-under", now.AddDays(-30).AddSeconds(1)));
                context.MarketRequests.Add(NewRequest("new", now.AddMinutes(-1)));
                context.SaveChanges();
            }

            MarketCleanupReport report;

            using (var context = MarketTestDatabase.CreateContext(Db))
                report = MarketCleanup.Run(context, now);

            Assert.AreEqual(2, report.Requests);
            CollectionAssert.AreEquivalent(new[] { "just-under", "new" }, MarketTestDatabase.Rows(Db, "SELECT idempotency_Key FROM market_request;"));
        }

        [TestMethod]
        public void Run_DeletesUsedAndExpiredLinkCodes_KeepsLiveOnes()
        {
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketLinkCodes.Add(NewLinkCode(1, expires: now.AddMinutes(4), used: null));
                context.MarketLinkCodes.Add(NewLinkCode(2, expires: now.AddMinutes(4), used: now.AddMinutes(-1)));
                context.MarketLinkCodes.Add(NewLinkCode(3, expires: now.AddSeconds(-1), used: null));
                context.MarketLinkCodes.Add(NewLinkCode(4, expires: now, used: null));
                context.SaveChanges();
            }

            MarketCleanupReport report;

            using (var context = MarketTestDatabase.CreateContext(Db))
                report = MarketCleanup.Run(context, now);

            Assert.AreEqual(3, report.LinkCodes, "used, expired, and expiring this instant (no longer redeemable) are gone");
            CollectionAssert.AreEquivalent(new[] { "1" }, MarketTestDatabase.Rows(Db, "SELECT account_Id FROM market_link_code;"));
        }

        [TestMethod]
        public void Run_DeletesTokensRevokedOrExpiredMoreThanThirtyDaysAgo_KeepsTheRest()
        {
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketPluginTokens.Add(NewToken("live", expires: now.AddDays(80), revoked: null));
                context.MarketPluginTokens.Add(NewToken("revoked-long-ago", expires: now.AddDays(80), revoked: now.AddDays(-31)));
                context.MarketPluginTokens.Add(NewToken("revoked-recently", expires: now.AddDays(80), revoked: now.AddDays(-29)));
                context.MarketPluginTokens.Add(NewToken("expired-long-ago", expires: now.AddDays(-31), revoked: null));
                context.MarketPluginTokens.Add(NewToken("expired-recently", expires: now.AddDays(-29), revoked: null));
                context.SaveChanges();
            }

            MarketCleanupReport report;

            using (var context = MarketTestDatabase.CreateContext(Db))
                report = MarketCleanup.Run(context, now);

            Assert.AreEqual(2, report.PluginTokens);
            CollectionAssert.AreEquivalent(new[] { "live", "revoked-recently", "expired-recently" }, MarketTestDatabase.Rows(Db, "SELECT label FROM market_plugin_token;"));
        }

        [TestMethod]
        public void Run_DeletesWebSessionsRevokedOrExpiredMoreThanAWeekAgo_KeepsTheRest()
        {
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketWebSessions.Add(NewSession(1, idle: now.AddDays(13), absolute: now.AddDays(29), revoked: null));
                context.MarketWebSessions.Add(NewSession(2, idle: now.AddDays(13), absolute: now.AddDays(29), revoked: now.AddDays(-8)));
                context.MarketWebSessions.Add(NewSession(3, idle: now.AddDays(13), absolute: now.AddDays(29), revoked: now.AddDays(-6)));
                context.MarketWebSessions.Add(NewSession(4, idle: now.AddDays(-8), absolute: now.AddDays(10), revoked: null));
                context.MarketWebSessions.Add(NewSession(5, idle: now.AddDays(-6), absolute: now.AddDays(10), revoked: null));
                context.MarketWebSessions.Add(NewSession(6, idle: now.AddDays(-8), absolute: now.AddDays(-8), revoked: null));
                context.MarketWebSessions.Add(NewSession(7, idle: now.AddDays(-7).AddSeconds(1), absolute: now.AddDays(-7).AddSeconds(1), revoked: null));
                context.SaveChanges();
            }

            MarketCleanupReport report;

            using (var context = MarketTestDatabase.CreateContext(Db))
                report = MarketCleanup.Run(context, now);

            Assert.AreEqual(3, report.WebSessions, "revoked 8 days ago, idle 8 days ago, past both 8 days ago");
            Assert.AreEqual(3, report.Total);
            CollectionAssert.AreEquivalent(new[] { "1", "3", "5", "7" }, MarketTestDatabase.Rows(Db, "SELECT account_Id FROM market_web_session;"),
                "the live session, and those that ended within the week, are kept");
        }

        [TestMethod]
        public void Run_LeavesTicketsToTheGameBridge()
        {
            var now = DateTime.UtcNow;

            MarketTestDatabase.Execute(Db,
                "INSERT INTO market_ticket (kind, account_Id, payload, status, idempotency_Key, created_Time, finished_Time) " +
                $"VALUES ('{TicketKind.MmdWithdraw}', 1, '{{}}', '{TicketStatus.Done}', 'k', UTC_TIMESTAMP(6) - INTERVAL 60 DAY, UTC_TIMESTAMP(6) - INTERVAL 60 DAY);");

            using (var context = MarketTestDatabase.CreateContext(Db))
                MarketCleanup.Run(context, now);

            Assert.AreEqual(1L, MarketTestDatabase.Scalar(Db, "SELECT COUNT(*) FROM market_ticket;"), "finished tickets are GameBridge's cleanup, not this one");
        }

        private static Request NewRequest(string key, DateTime created) => new Request { AccountId = 1, IdempotencyKey = key, Kind = "purchase", Result = "{}", CreatedTime = created };

        private static LinkCode NewLinkCode(uint accountId, DateTime expires, DateTime? used) => new LinkCode
        {
            CodeHash = Guid.NewGuid().ToByteArray(),
            AccountId = accountId,
            CharacterId = 1,
            ExpiresTime = expires,
            UsedTime = used,
        };

        private static WebSession NewSession(uint accountId, DateTime idle, DateTime absolute, DateTime? revoked) => new WebSession
        {
            TokenHash = Guid.NewGuid().ToByteArray(),
            AccountId = accountId,
            CreatedTime = absolute.AddDays(-30),
            LastUsedTime = idle.AddDays(-14),
            IdleExpiresTime = idle,
            AbsoluteExpiresTime = absolute,
            PasswordFingerprint = new byte[32],
            RevokedTime = revoked,
        };

        private static PluginToken NewToken(string label, DateTime expires, DateTime? revoked) => new PluginToken
        {
            TokenHash = Guid.NewGuid().ToByteArray(),
            AccountId = 1,
            Label = label,
            CreatedTime = expires.AddDays(-90),
            ExpiresTime = expires,
            RevokedTime = revoked,
            PasswordFingerprint = new byte[32],
        };
    }
}
