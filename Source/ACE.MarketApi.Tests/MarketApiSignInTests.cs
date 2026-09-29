using System;
using System.Net;
using System.Threading.Tasks;

using ACE.Database.Market;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// Web sign-in with the game password: read-only, rate limited, bans refused
    /// </summary>
    [TestClass]
    public class MarketApiSignInTests
    {
        [TestMethod]
        public async Task SignIn_CorrectPassword_Bcrypt_SucceedsAndTheAccountRowIsUnchanged()
        {
            // work factor 4 differs from the configured one, so the game's check would rehash it
            var name = MarketApiTestData.UniqueName("bc");
            var id = MarketApiTestData.CreateAccount(name, "hunter2", bcryptWorkFactor: 4);
            var before = MarketApiTestData.AccountRow(id);

            await using var host = await MarketApiHost.StartAsync();
            var response = await host.SignInAsync(name, "hunter2");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(id, (await MarketApiHost.JsonAsync(response)).GetProperty("accountId").GetUInt32());
            MarketApiHost.SessionCookie(response);
            Assert.AreEqual(before, MarketApiTestData.AccountRow(id));
        }

        [TestMethod]
        public async Task SignIn_CorrectPassword_OldSha512Account_SucceedsAndTheAccountRowIsUnchanged()
        {
            var name = MarketApiTestData.UniqueName("sha");
            var id = MarketApiTestData.CreateAccount(name, "hunter2", sha512: true);
            var before = MarketApiTestData.AccountRow(id);

            await using var host = await MarketApiHost.StartAsync();
            var response = await host.SignInAsync(name, "hunter2");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(before, MarketApiTestData.AccountRow(id));
            StringAssert.DoesNotMatch(before, new System.Text.RegularExpressions.Regex("\\|" + ToHex("use bcrypt") + "\\|"), "the account should still be SHA512");
        }

        [TestMethod]
        public async Task SignIn_WrongPasswordOrUnknownAccount_IsRefusedAndTheAccountRowIsUnchanged()
        {
            var bcrypt = MarketApiTestData.UniqueName("bcw");
            var bcryptId = MarketApiTestData.CreateAccount(bcrypt, "hunter2");
            var sha = MarketApiTestData.UniqueName("shaw");
            var shaId = MarketApiTestData.CreateAccount(sha, "hunter2", sha512: true);
            var bcryptBefore = MarketApiTestData.AccountRow(bcryptId);
            var shaBefore = MarketApiTestData.AccountRow(shaId);

            await using var host = await MarketApiHost.StartAsync();

            foreach (var (account, password) in new[] { (bcrypt, "wrong"), (sha, "wrong"), (MarketApiTestData.UniqueName("nobody"), "hunter2"), (bcrypt, "") })
            {
                var response = await host.SignInAsync(account, password);

                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, account);
                Assert.AreEqual("invalid_credentials", await MarketApiHost.ErrorAsync(response));
                Assert.IsFalse(response.Headers.Contains("Set-Cookie"));
            }

            Assert.AreEqual(bcryptBefore, MarketApiTestData.AccountRow(bcryptId));
            Assert.AreEqual(shaBefore, MarketApiTestData.AccountRow(shaId));
        }

        [TestMethod]
        public async Task SignIn_SixthAttemptAfterFiveWrong_IsRefusedForFifteenMinutesEvenIfCorrect()
        {
            var name = MarketApiTestData.UniqueName("lock");
            MarketApiTestData.CreateAccount(name, "right");

            await using var host = await MarketApiHost.StartAsync();

            for (var i = 0; i < 5; i++)
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.SignInAsync(name, "wrong")).StatusCode);
                host.Clock.Advance(TimeSpan.FromMinutes(1));
            }

            var sixth = await host.SignInAsync(name, "right");
            Assert.AreEqual(HttpStatusCode.TooManyRequests, sixth.StatusCode);
            Assert.AreEqual("account_locked", await MarketApiHost.ErrorAsync(sixth));
            Assert.IsFalse(sixth.Headers.Contains("Set-Cookie"));

            // the lock runs 15 minutes from the 5th failure, which was 1 minute ago
            host.Clock.Advance(TimeSpan.FromMinutes(13.9));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, (await host.SignInAsync(name, "right")).StatusCode);

            // a different IP doesn't get around an account lock
            Assert.AreEqual(HttpStatusCode.TooManyRequests, (await host.SignInAsync(name, "right", "10.9.9.9")).StatusCode);

            host.Clock.Advance(TimeSpan.FromMinutes(0.2));
            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(name, "right")).StatusCode);
        }

        [TestMethod]
        public async Task SignIn_FiveFailuresSpreadOverMoreThanFifteenMinutes_DoNotLock()
        {
            var name = MarketApiTestData.UniqueName("spread");
            MarketApiTestData.CreateAccount(name, "right");

            await using var host = await MarketApiHost.StartAsync();

            for (var i = 0; i < 5; i++)
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.SignInAsync(name, "wrong")).StatusCode);
                host.Clock.Advance(TimeSpan.FromMinutes(4));
            }

            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(name, "right")).StatusCode);
        }

        [TestMethod]
        public async Task SignIn_TwentyFailuresFromOneIp_BlockThatIp()
        {
            var victim = MarketApiTestData.UniqueName("victim");
            MarketApiTestData.CreateAccount(victim, "right");

            await using var host = await MarketApiHost.StartAsync();

            // 20 failures over 5 accounts (4 each, so no account locks), from one IP
            for (var i = 0; i < 20; i++)
            {
                var target = MarketApiTestData.UniqueName("t");
                if (i % 4 == 0)
                    MarketApiTestData.CreateAccount(target, "x");

                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.SignInAsync(i % 2 == 0 ? target : MarketApiTestData.UniqueName("ghost"), "wrong", "10.2.2.2")).StatusCode);
                host.Clock.Advance(TimeSpan.FromSeconds(30));
            }

            var blocked = await host.SignInAsync(victim, "right", "10.2.2.2");
            Assert.AreEqual(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(blocked));

            // another IP is not blocked, and neither is the account
            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(victim, "right", "10.3.3.3")).StatusCode);

            host.Clock.Advance(TimeSpan.FromMinutes(15.1));
            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(victim, "right", "10.2.2.2")).StatusCode);
        }

        [TestMethod]
        public async Task SignIn_NineteenFailuresFromOneIp_DoNotBlockIt()
        {
            var name = MarketApiTestData.UniqueName("ok19");
            MarketApiTestData.CreateAccount(name, "right");

            await using var host = await MarketApiHost.StartAsync();

            for (var i = 0; i < 19; i++)
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.SignInAsync(MarketApiTestData.UniqueName("ghost"), "wrong", "10.4.4.4")).StatusCode);

            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(name, "right", "10.4.4.4")).StatusCode);
        }

        [TestMethod]
        public async Task SignIn_LockoutLimits_ComeFromTheServerSettings()
        {
            var name = MarketApiTestData.UniqueName("set");
            MarketApiTestData.CreateAccount(name, "right");
            MarketApiTestData.SetSetting(MarketSettings.SignInAccountFailures.Key, 2);
            MarketApiTestData.SetSetting(MarketSettings.SignInAccountLockMinutes.Key, 60);

            try
            {
                await using var host = await MarketApiHost.StartAsync();

                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.SignInAsync(name, "wrong")).StatusCode);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.SignInAsync(name, "wrong")).StatusCode);
                Assert.AreEqual(HttpStatusCode.TooManyRequests, (await host.SignInAsync(name, "right")).StatusCode);

                host.Clock.Advance(TimeSpan.FromMinutes(30));
                Assert.AreEqual(HttpStatusCode.TooManyRequests, (await host.SignInAsync(name, "right")).StatusCode);

                host.Clock.Advance(TimeSpan.FromMinutes(31));
                Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(name, "right")).StatusCode);
            }
            finally
            {
                MarketApiTestData.ClearSetting(MarketSettings.SignInAccountFailures.Key);
                MarketApiTestData.ClearSetting(MarketSettings.SignInAccountLockMinutes.Key);
            }
        }

        [TestMethod]
        public async Task SignIn_BannedAccount_IsRefusedEvenWithTheRightPassword()
        {
            var name = MarketApiTestData.UniqueName("ban");
            var id = MarketApiTestData.CreateAccount(name, "right");
            MarketApiTestData.Ban(id, DateTime.UtcNow.AddDays(1));

            await using var host = await MarketApiHost.StartAsync();
            var response = await host.SignInAsync(name, "right");

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.AreEqual("banned", await MarketApiHost.ErrorAsync(response));
            Assert.IsFalse(response.Headers.Contains("Set-Cookie"));
        }

        [TestMethod]
        public async Task SignIn_ExpiredBan_SignsIn()
        {
            var name = MarketApiTestData.UniqueName("exban");
            var id = MarketApiTestData.CreateAccount(name, "right");
            MarketApiTestData.Ban(id, DateTime.UtcNow.AddDays(-1));

            await using var host = await MarketApiHost.StartAsync();

            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(name, "right")).StatusCode);
        }

        [TestMethod]
        public async Task Session_BanNoticedAfterSignIn_StopsTheSessionWorking()
        {
            var name = MarketApiTestData.UniqueName("later");
            var id = MarketApiTestData.CreateAccount(name, "right");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(name, "right");

            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/me", cookie)).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/vault", cookie)).StatusCode);

            MarketApiTestData.Ban(id, DateTime.UtcNow.AddDays(1));

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/me", cookie)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/vault", cookie)).StatusCode);
        }

        private static string ToHex(string text) => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(text));
    }
}
