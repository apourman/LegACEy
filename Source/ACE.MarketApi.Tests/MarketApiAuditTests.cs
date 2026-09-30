using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// Seam 1: the ledger audit at API startup and every hour, and the pause it sets. These run on their own scratch shard, whose ledger is only ever written
    /// through the ledger service, because the shared one is seeded with bare balance rows the audit rightly fails.
    /// </summary>
    [TestClass]
    public class MarketApiAuditTests
    {
        private const string Db = "ace_shard_market_api_audit";

        private static uint balanceHolder;

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;

            // the account whose balance the tests edit to break the books
            balanceHolder = NewPlayer("holder", balance: 10).AccountId;
        }

        [ClassCleanup]
        public static void ClassCleanup() => MarketTestDatabase.Drop(Db);

        [TestCleanup]
        public void TestCleanup()
        {
            // whatever a test did, leave the books balanced and the market running
            MarketTestDatabase.Execute(Db, $"UPDATE market_balance SET balance = 10 WHERE account_Id = {balanceHolder};");
            Resume();
        }

        [TestMethod]
        public async Task Schedule_ByDefault_AuditsAtStartupAndEveryHour_AndThePauseIsTheDatabaseRow()
        {
            Assert.AreEqual(new LedgerAuditSchedule(true, TimeSpan.FromHours(1)), LedgerAuditSchedule.Default);

            await using var app = MarketApi.Create(new[]
            {
                $"--Market:AuthDatabase={MarketApiTestData.AuthDatabase}",
                $"--Market:ShardDatabase={Db}",
                $"--Market:KeysPath={MarketApiHost.NewKeysPath()}",
            }, builder => builder.WebHost.UseTestServer());

            Assert.AreEqual(LedgerAuditSchedule.Default, app.Services.GetRequiredService<LedgerAuditSchedule>());

            var pause = app.Services.GetRequiredService<IMarketPause>();
            Assert.IsFalse(pause.IsPaused);

            using (var shard = MarketTestDatabase.CreateContext(Db))
                MarketPause.Pause(shard, "test", DateTime.UtcNow);

            Assert.IsTrue(pause.IsPaused, "the API reads the pause the game and the audit write");
        }

        [TestMethod]
        public async Task Startup_CleanLedger_PassesAndDoesNotPause()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);

            // a real sale first, so the books have more than seeded adjustments in them
            await using (var host = await StartAsync(LedgerAuditSchedule.Default))
            {
                var listed = NewListing(host, seller, 120);
                var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
                var response = await PurchaseAsync(host, cookie, listed);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            var logs = new CapturedLogs();

            await using (var host = await StartAsync(LedgerAuditSchedule.Default, logs))
            {
                Assert.IsFalse(Paused());
                Assert.IsFalse(logs.Any(LogLevel.Critical), string.Join("\n", logs.Lines));

                var listed = NewListing(host, seller, 5);
                var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
                Assert.AreEqual(HttpStatusCode.OK, (await PurchaseAsync(host, cookie, listed)).StatusCode);
            }
        }

        [TestMethod]
        public async Task Startup_FailedAudit_PausesPurchasesAndLogsLoudly_WhileBrowsingAndListingWork_UntilResumed()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);
            var logs = new CapturedLogs();

            BreakTheBooks();

            await using var host = await StartAsync(LedgerAuditSchedule.Default, logs);

            Assert.IsTrue(Paused(), "the startup audit failed and paused the market");
            Assert.IsTrue(logs.Any(LogLevel.Critical, "Ledger audit FAILED"), string.Join("\n", logs.Lines));
            Assert.IsTrue(logs.Any(LogLevel.Critical, $"account {balanceHolder} "), "each failure is logged: " + string.Join("\n", logs.Lines));

            var listed = NewListing(host, seller, 50);
            var held = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Held Helm", VaultItemState.Held, database: Db);
            var buyerCookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var sellerCookie = await host.SignInForCookieAsync(seller.Name, "pass");

            var refused = await PurchaseAsync(host, buyerCookie, listed);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.AreEqual("paused", await MarketApiHost.ErrorAsync(refused));
            Assert.AreEqual(500, Balance(buyer.AccountId), "nothing was charged");

            // browsing and listing go on
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/listings")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/listings/{listed.ListingId}")).StatusCode);
            var listing = await host.PostJsonAsync("/listings", new { itemGuid = held, price = 7 }, sellerCookie);
            Assert.AreEqual(HttpStatusCode.Created, listing.StatusCode, await listing.Content.ReadAsStringAsync());

            // the admin fixes the books and resumes, as /market resume does
            MarketTestDatabase.Execute(Db, $"UPDATE market_balance SET balance = 10 WHERE account_Id = {balanceHolder};");
            Resume();

            var bought = await PurchaseAsync(host, buyerCookie, listed);
            Assert.AreEqual(HttpStatusCode.OK, bought.StatusCode, await bought.Content.ReadAsStringAsync());
            Assert.AreEqual(450, Balance(buyer.AccountId));
        }

        [TestMethod]
        public async Task Hourly_FailedAudit_PausesARunningApi()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);

            // the hourly audit, on a test-sized interval
            await using var host = await StartAsync(new LedgerAuditSchedule(true, TimeSpan.FromMilliseconds(200)));

            Assert.IsFalse(Paused(), "the startup audit passed");

            BreakTheBooks();

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!Paused())
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, "the periodic audit paused the market");
                await Task.Delay(50);
            }

            var listed = NewListing(host, seller, 50);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var refused = await PurchaseAsync(host, cookie, listed);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.AreEqual("paused", await MarketApiHost.ErrorAsync(refused));
        }

        // ---- helpers

        private sealed record Player(string Name, uint AccountId, uint CharacterId);

        private sealed record Listed(uint ItemGuid, long ListingId, long Price);

        private static Task<MarketApiHost> StartAsync(LedgerAuditSchedule schedule, CapturedLogs logs = null) =>
            MarketApiHost.StartOnAsync(Db, services =>
            {
                services.AddSingleton(schedule);

                if (logs != null)
                    services.AddSingleton<ILoggerProvider>(logs);
            });

        private static Player NewPlayer(string prefix, long? balance = null)
        {
            var name = MarketApiTestData.UniqueName(prefix);
            var accountId = MarketApiTestData.CreateAccount(name, "pass");
            var characterId = MarketApiTestData.AddCharacter(accountId, name + "Main", database: Db);

            // through the ledger, so the books stay balanced
            if (balance.HasValue)
                Assert.AreEqual(CorrectionOutcome.Done, LedgerCorrections.Adjust(() => MarketTestDatabase.CreateContext(Db), accountId, balance.Value, "test seed", 1, null, DateTime.UtcNow).Outcome);

            return new Player(name, accountId, characterId);
        }

        private static Listed NewListing(MarketApiHost host, Player seller, long price)
        {
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Bone Slicer", VaultItemState.Listed, database: Db);
            var id = MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, guid, price, ListingStatus.Active, host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1), database: Db);

            return new Listed(guid, id, price);
        }

        private static Task<HttpResponseMessage> PurchaseAsync(MarketApiHost host, string cookie, Listed listed) =>
            host.PostJsonAsync($"/listings/{listed.ListingId}/purchase", new { count = 1, expectedPrice = listed.Price, idempotencyKey = Guid.NewGuid().ToString("N") }, cookie);

        private static long Balance(uint accountId) => MarketTestDatabase.Scalar(Db, $"SELECT IFNULL((SELECT balance FROM market_balance WHERE account_Id = {accountId}), 0);");

        /// <summary>
        /// A balance edited behind the ledger's back
        /// </summary>
        private static void BreakTheBooks() => MarketTestDatabase.Execute(Db, $"UPDATE market_balance SET balance = 11 WHERE account_Id = {balanceHolder};");

        private static bool Paused()
        {
            using var shard = MarketTestDatabase.CreateContext(Db);
            return MarketPause.IsPaused(shard);
        }

        private static void Resume()
        {
            using var shard = MarketTestDatabase.CreateContext(Db);
            MarketPause.Resume(shard, "test admin", DateTime.UtcNow);
        }

        /// <summary>
        /// Every log line the API writes
        /// </summary>
        private sealed class CapturedLogs : ILoggerProvider
        {
            private readonly ConcurrentQueue<(LogLevel Level, string Text)> lines = new();

            public string[] Lines => lines.Select(l => $"{l.Level}: {l.Text}").ToArray();

            public bool Any(LogLevel level, string text = "") => lines.Any(l => l.Level == level && l.Text.Contains(text, StringComparison.Ordinal));

            public ILogger CreateLogger(string categoryName) => new Logger(lines);

            public void Dispose() { }

            private sealed class Logger : ILogger
            {
                private readonly ConcurrentQueue<(LogLevel, string)> lines;

                public Logger(ConcurrentQueue<(LogLevel, string)> lines) => this.lines = lines;

                public IDisposable BeginScope<TState>(TState state) => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) => lines.Enqueue((logLevel, formatter(state, exception)));
            }
        }
    }
}
