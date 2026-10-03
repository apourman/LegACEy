using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Seam 3: the web session rows, against real MySQL. Only the token's hash is stored; times are kept to the microsecond, never rounded up;
    /// the idle expiry never passes the absolute one; a ban's revocation touches only the banned accounts' live sessions.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class WebSessionsTests
    {
        private const string Db = "ace_shard_market_web_sessions";

        private const string TimeFormat = "%Y-%m-%d %H:%i:%s.%f";

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            Assert.AreEqual(0, failures.Count, string.Join(", ", failures.Select(f => $"{f.Key}: {f.Value.Message}")));
        }

        [ClassCleanup]
        public static void ClassCleanup() => MarketTestDatabase.Drop(Db);

        [TestCleanup]
        public void Cleanup()
        {
            MarketTestDatabase.Execute(Db, "DELETE FROM market_web_session;");
            MarketTestDatabase.Execute(Db, $"DELETE FROM config_properties_long WHERE `key` IN ('{MarketSettings.WebSessionIdleDays.Key}', '{MarketSettings.WebSessionAbsoluteDays.Key}');");
        }

        [TestMethod]
        public void Create_StoresOnlyTheTokensHash_WithTimesTruncatedToTheMicrosecond()
        {
            // 0.9999999 s past the minute: rounding would store the next second
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc).AddTicks(9_999_999);

            string token;
            using (var context = MarketTestDatabase.CreateContext(Db))
                WebSessions.Create(context, 7, "the password hash", now, out token);

            StringAssert.StartsWith(token, WebSessions.TokenPrefix);

            var row = MarketTestDatabase.Rows(Db,
                $"SELECT CONCAT_WS('|', HEX(token_Hash), HEX(password_Fingerprint), DATE_FORMAT(created_Time, '{TimeFormat}'), DATE_FORMAT(last_Used_Time, '{TimeFormat}'), " +
                $"DATE_FORMAT(idle_Expires_Time, '{TimeFormat}'), DATE_FORMAT(absolute_Expires_Time, '{TimeFormat}'), IFNULL(revoked_Time, '-')) FROM market_web_session WHERE account_Id = 7;").Single();

            Assert.AreEqual(string.Join("|",
                Sha256(token), Sha256("the password hash"),
                "2026-10-02 12:00:00.999999", "2026-10-02 12:00:00.999999", "2026-10-16 12:00:00.999999", "2026-11-01 12:00:00.999999", "-"), row);
        }

        [TestMethod]
        public void Create_AndTouch_NeverSetTheIdleExpiryPastTheAbsoluteOne()
        {
            MarketTestDatabase.Execute(Db, $"INSERT INTO config_properties_long (`key`, `value`) VALUES ('{MarketSettings.WebSessionIdleDays.Key}', 40), ('{MarketSettings.WebSessionAbsoluteDays.Key}', 30);");
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                var session = WebSessions.Create(context, 8, "hash", now, out _);
                Assert.IsTrue(WebSessions.Touch(context, session, now.AddDays(1)));
            }

            Assert.AreEqual("2026-11-01 12:00:00.000000|2026-11-01 12:00:00.000000", MarketTestDatabase.Rows(Db,
                $"SELECT CONCAT_WS('|', DATE_FORMAT(idle_Expires_Time, '{TimeFormat}'), DATE_FORMAT(absolute_Expires_Time, '{TimeFormat}')) FROM market_web_session WHERE account_Id = 8;").Single());
        }

        [TestMethod]
        public void RevokeAll_EndsOnlyTheAccountsLiveSessions_AndKeepsEarlierRevocations()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                WebSessions.Create(context, 21, "hash", now, out _);
                WebSessions.Create(context, 21, "hash", now, out _);
                var signedOut = WebSessions.Create(context, 22, "hash", now, out _);
                WebSessions.Revoke(context, signedOut.Id, now.AddHours(1));
                WebSessions.Create(context, 22, "hash", now, out _);
                WebSessions.Create(context, 23, "hash", now, out _);

                Assert.AreEqual(3, WebSessions.RevokeAll(context, new uint[] { 21, 22 }, now.AddHours(2)));
                Assert.AreEqual(0, WebSessions.RevokeAll(context, new uint[] { 21, 22 }, now.AddHours(3)), "nothing left to revoke");
            }

            CollectionAssert.AreEquivalent(new[]
            {
                "21|2026-10-02 14:00:00.000000", "21|2026-10-02 14:00:00.000000",
                "22|2026-10-02 13:00:00.000000", "22|2026-10-02 14:00:00.000000",
                "23|-",
            }, MarketTestDatabase.Rows(Db, $"SELECT CONCAT_WS('|', account_Id, IFNULL(DATE_FORMAT(revoked_Time, '{TimeFormat}'), '-')) FROM market_web_session;"));
        }

        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
