using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using ACE.Database.Models.Shard.Market;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// The BFF's web sessions: sign-in returns an opaque token stored only as a hash and sets no cookie; a session lives 14 days idle (renewed
    /// at most once an hour) and 30 days at most; sign-out, a password change and a ban end it, and a ban ends every session of the account for good.
    /// </summary>
    [TestClass]
    public class MarketApiWebSessionTests
    {
        private const string TimeFormat = "%Y-%m-%d %H:%i:%s.%f";

        private sealed record Player(string Name, uint AccountId, uint CharacterId);

        private static Player NewPlayer(string prefix)
        {
            var name = MarketApiTestData.UniqueName(prefix);
            var accountId = MarketApiTestData.CreateAccount(name, "pass");

            return new Player(name, accountId, MarketApiTestData.AddCharacter(accountId, name + "Main"));
        }

        // ---- sign-in

        [TestMethod]
        public async Task SignIn_ReturnsATokenAndItsExpiries_SetsNoCookie_AndStoresOnlyTheHash()
        {
            var player = NewPlayer("wsin");
            var accountBefore = MarketApiTestData.AccountRow(player.AccountId);

            await using var host = await MarketApiHost.StartAsync();
            var now = Microseconds(host.Clock.GetUtcNow().UtcDateTime);

            var response = await host.SessionSignInAsync(player.Name, "pass");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.IsFalse(response.Headers.Contains("Set-Cookie"), "the BFF keeps the token in its own cookie");

            var body = await MarketApiHost.JsonAsync(response);
            var token = body.GetProperty("token").GetString();
            Assert.AreEqual(player.AccountId, body.GetProperty("accountId").GetUInt32());
            Assert.AreEqual(player.Name, body.GetProperty("accountName").GetString());
            Assert.AreEqual(now.AddDays(14), body.GetProperty("idleExpiresTime").GetDateTime().ToUniversalTime());
            Assert.AreEqual(now.AddDays(30), body.GetProperty("absoluteExpiresTime").GetDateTime().ToUniversalTime());

            // opaque and random: 32 bytes after the prefix, and no two alike
            StringAssert.StartsWith(token, "ws.");
            Assert.AreEqual(32, Convert.FromBase64String(Base64(token.Substring(3))).Length);
            Assert.AreNotEqual(token, await host.SignInForSessionAsync(player.Name, "pass"));

            // the row holds the token's SHA-256 and nothing that is the token
            var row = MarketApiTestData.Rows(
                "SELECT CONCAT_WS('|', HEX(token_Hash), account_Id, " +
                $"DATE_FORMAT(created_Time, '{TimeFormat}'), DATE_FORMAT(last_Used_Time, '{TimeFormat}'), DATE_FORMAT(idle_Expires_Time, '{TimeFormat}'), " +
                $"DATE_FORMAT(absolute_Expires_Time, '{TimeFormat}'), IFNULL(revoked_Time, '-')) FROM market_web_session WHERE token_Hash = UNHEX('{Sha256(token)}');").Single();
            Assert.AreEqual(string.Join("|", Sha256(token), player.AccountId, Sql(now), Sql(now), Sql(now.AddDays(14)), Sql(now.AddDays(30)), "-"), row);
            Assert.AreEqual(0L, MarketApiTestData.Scalar(
                $"SELECT COUNT(*) FROM market_web_session WHERE INSTR(token_Hash, '{token}') > 0 OR INSTR(password_Fingerprint, '{token}') > 0;"));

            var me = await host.GetWithTokenAsync("/api/me", token);
            Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
            Assert.AreEqual(player.Name, (await MarketApiHost.JsonAsync(me)).GetProperty("accountName").GetString());

            Assert.AreEqual(accountBefore, MarketApiTestData.AccountRow(player.AccountId), "sign-in never writes the account");
        }

        [TestMethod]
        public async Task SignIn_Refusals_AreTodaysCodes_AndStartNoSession()
        {
            var player = NewPlayer("wsno");
            var banned = NewPlayer("wsban");

            await using var host = await MarketApiHost.StartAsync();
            MarketApiTestData.Ban(banned.AccountId, BanEnds(host));

            async Task Refused(HttpResponseMessage response, HttpStatusCode status, string code)
            {
                Assert.AreEqual(status, response.StatusCode, code);
                Assert.AreEqual(code, await MarketApiHost.ErrorAsync(response));
                Assert.IsFalse(response.Headers.Contains("Set-Cookie"), code);
            }

            await Refused(await host.PostJsonAsync("/api/auth/session", new { account = player.Name }), HttpStatusCode.BadRequest, "bad_request");
            await Refused(await host.SessionSignInAsync(MarketApiTestData.UniqueName("nobody"), "pass"), HttpStatusCode.Unauthorized, "invalid_credentials");
            await Refused(await host.SessionSignInAsync(banned.Name, "pass"), HttpStatusCode.Forbidden, "banned");

            for (var i = 0; i < 5; i++)
                await Refused(await host.SessionSignInAsync(player.Name, "wrong"), HttpStatusCode.Unauthorized, "invalid_credentials");

            await Refused(await host.SessionSignInAsync(player.Name, "pass"), HttpStatusCode.TooManyRequests, "account_locked");

            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_web_session WHERE account_Id IN ({player.AccountId}, {banned.AccountId});"));
        }

        [TestMethod]
        public async Task SessionBearer_ChangesWithoutTheRequestHeader_LikeAPluginToken()
        {
            var player = NewPlayer("wscsrf");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Bearer Helm", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var token = await host.SignInForSessionAsync(player.Name, "pass");

            // CSRF is the BFF's job: a bearer token is never sent by a browser on its own
            var list = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, token: token, requestHeader: false);
            Assert.AreEqual(HttpStatusCode.Created, list.StatusCode, await list.Content.ReadAsStringAsync());
        }

        // ---- lifetime

        [TestMethod]
        public async Task Idle_RefusedAfterFourteenUnusedDays_AndUseRenewsIt()
        {
            var player = NewPlayer("wsidle");

            await using var host = await MarketApiHost.StartAsync();
            var unused = await host.SignInForSessionAsync(player.Name, "pass");
            var used = await host.SignInForSessionAsync(player.Name, "pass");

            host.Clock.Advance(TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1));
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", used)).StatusCode);

            host.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", unused)).StatusCode, "14 days without a use");
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", used)).StatusCode, "the use a second ago renewed it");

            host.Clock.Advance(TimeSpan.FromDays(14));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", used)).StatusCode, "14 days since its last use");
        }

        [TestMethod]
        public async Task Absolute_RefusedThirtyDaysAfterSignIn_HoweverActive_AndTheIdleExpiryNeverPassesIt()
        {
            var player = NewPlayer("wsabs");

            await using var host = await MarketApiHost.StartAsync();
            var signedIn = Microseconds(host.Clock.GetUtcNow().UtcDateTime);
            var token = await host.SignInForSessionAsync(player.Name, "pass");

            for (var day = 1; day < 30; day++)
            {
                host.Clock.Advance(TimeSpan.FromDays(1));
                Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode, $"day {day}");
            }

            // used on day 29: the idle expiry would be day 43, but it stops at the absolute expiry
            Assert.AreEqual($"{Sql(signedIn.AddDays(30))}|{Sql(signedIn.AddDays(30))}", SessionTimes(token, "idle_Expires_Time", "absolute_Expires_Time"));

            host.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1));
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode, "a second before day 30");

            host.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", token)).StatusCode, "day 30, used a second ago");
        }

        [TestMethod]
        public async Task LastUsedAndIdleExpiry_AreWrittenAtMostOnceAnHour()
        {
            var player = NewPlayer("wshour");

            // a whole second, so "exactly an hour" after sign-in is exact to the microsecond the row keeps
            var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await using var host = await MarketApiHost.StartAsync(clock: new ManualClock(start));
            var signedIn = start.UtcDateTime;
            var token = await host.SignInForSessionAsync(player.Name, "pass");
            var atSignIn = $"{Sql(signedIn)}|{Sql(signedIn.AddDays(14))}";

            Assert.AreEqual(atSignIn, SessionTimes(token, "last_Used_Time", "idle_Expires_Time"));

            // ticket polling: every 2 seconds for most of an hour
            for (var i = 0; i < 20; i++)
            {
                host.Clock.Advance(TimeSpan.FromMinutes(2.95));
                Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/tickets", token)).StatusCode);
            }

            Assert.AreEqual(atSignIn, SessionTimes(token, "last_Used_Time", "idle_Expires_Time"), "59 minutes of use wrote nothing");

            host.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.AreEqual(signedIn.AddHours(1), host.Clock.GetUtcNow().UtcDateTime);
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode);
            Assert.AreEqual(atSignIn, SessionTimes(token, "last_Used_Time", "idle_Expires_Time"), "exactly an hour after the last write: not over an hour, so nothing");

            host.Clock.Advance(TimeSpan.FromTicks(10));
            var pastTheHour = host.Clock.GetUtcNow().UtcDateTime;
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode);
            Assert.AreEqual($"{Sql(pastTheHour)}|{Sql(pastTheHour.AddDays(14))}", SessionTimes(token, "last_Used_Time", "idle_Expires_Time"), "a microsecond over an hour after the last write");

            host.Clock.Advance(TimeSpan.FromMinutes(30));
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode);
            Assert.AreEqual($"{Sql(pastTheHour)}|{Sql(pastTheHour.AddDays(14))}", SessionTimes(token, "last_Used_Time", "idle_Expires_Time"), "half an hour after the last write");
        }

        // ---- revocation

        [TestMethod]
        public async Task SignOut_RevokesThatSessionOnly()
        {
            var player = NewPlayer("wsout");

            await using var host = await MarketApiHost.StartAsync();
            var token = await host.SignInForSessionAsync(player.Name, "pass");
            var other = await host.SignInForSessionAsync(player.Name, "pass");

            var signOut = await host.SendAsync(HttpMethod.Delete, "/api/auth/session", token: token, requestHeader: false);
            Assert.AreEqual(HttpStatusCode.OK, signOut.StatusCode, await signOut.Content.ReadAsStringAsync());

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", token)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Delete, "/api/auth/session", token: token)).StatusCode, "signing out twice");
            Assert.AreNotEqual("-", SessionTimes(token, "revoked_Time"));

            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", other)).StatusCode, "the account's other session goes on");
        }

        [TestMethod]
        public async Task SignOut_WithACookie_HasNoWebSessionToEnd()
        {
            var player = NewPlayer("wsoutc");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var signOut = await host.SendAsync(HttpMethod.Delete, "/api/auth/session", cookie);
            Assert.AreEqual(HttpStatusCode.Unauthorized, signOut.StatusCode);
            Assert.AreEqual("unauthorized", await MarketApiHost.ErrorAsync(signOut));
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/me", cookie)).StatusCode);
        }

        [TestMethod]
        public async Task SignOut_RefusedByTheWebSessionSchemeAlone_StillAnswersTheJson401()
        {
            var player = NewPlayer("wsoutj");

            await using var host = await MarketApiHost.StartAsync();
            var token = await host.SignInForSessionAsync(player.Name, "pass");

            // sign-out takes only a web session, so the cookie scheme never writes its 401 body: the web session scheme must
            foreach (var (label, bearer) in new[] { ("no sign-in", (string)null), ("a forged token", "ws." + new string('B', 43)), ("a plugin-shaped token", "not-a-session") })
            {
                var response = await host.SendAsync(HttpMethod.Delete, "/api/auth/session", token: bearer);
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, label);
                Assert.AreEqual("unauthorized", await MarketApiHost.ErrorAsync(response), label);
            }

            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode, "none of those ended the real session");
        }

        [TestMethod]
        public async Task PasswordChange_EndsTheSession()
        {
            var player = NewPlayer("wspass");

            await using var host = await MarketApiHost.StartAsync();
            var token = await host.SignInForSessionAsync(player.Name, "pass");
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode);

            MarketApiTestData.SetPassword(player.AccountId, "changed");

            var refused = await host.GetWithTokenAsync("/api/me", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.AreEqual("unauthorized", await MarketApiHost.ErrorAsync(refused));

            var fresh = await host.SignInForSessionAsync(player.Name, "changed");
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", fresh)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", token)).StatusCode);
        }

        [TestMethod]
        public async Task Ban_SeenByOneRequest_EndsEverySessionOfTheAccount_ForGood()
        {
            var player = NewPlayer("wsbanned");
            var bystander = NewPlayer("wsbystander");

            await using var host = await MarketApiHost.StartAsync();
            var first = await host.SignInForSessionAsync(player.Name, "pass");
            var second = await host.SignInForSessionAsync(player.Name, "pass");
            var bystanders = await host.SignInForSessionAsync(bystander.Name, "pass");

            MarketApiTestData.Ban(player.AccountId, BanEnds(host));

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", first)).StatusCode, "the request that sees the ban");

            MarketApiTestData.LiftBan(player.AccountId);

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", first)).StatusCode, "after the ban, the token that saw it");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", second)).StatusCode, "after the ban, the token that was never used during it");
            Assert.AreEqual(2L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_web_session WHERE account_Id = {player.AccountId} AND revoked_Time IS NOT NULL;"));

            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", bystanders)).StatusCode, "another account's session");
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", await host.SignInForSessionAsync(player.Name, "pass"))).StatusCode, "signing in again works");
        }

        [TestMethod]
        public async Task Ban_SeenByACookieRequestOrBrowsing_AlsoEndsTheWebSessions()
        {
            var cookieSeen = NewPlayer("wsbanc");
            var browseSeen = NewPlayer("wsbanb");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(cookieSeen.Name, "pass");
            var cookieAccountsSession = await host.SignInForSessionAsync(cookieSeen.Name, "pass");
            var browseAccountsSession = await host.SignInForSessionAsync(browseSeen.Name, "pass");

            MarketApiTestData.Ban(cookieSeen.AccountId, BanEnds(host));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/api/me", cookie)).StatusCode);
            MarketApiTestData.LiftBan(cookieSeen.AccountId);

            MarketApiTestData.Ban(browseSeen.AccountId, BanEnds(host));
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/listings")).StatusCode, "anyone's browse request sees every ban");
            MarketApiTestData.LiftBan(browseSeen.AccountId);

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", cookieAccountsSession)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", browseAccountsSession)).StatusCode);
        }

        [TestMethod]
        public async Task UnknownOrForgedToken_IsRefused()
        {
            var player = NewPlayer("wsforged");

            await using var host = await MarketApiHost.StartAsync();
            var token = await host.SignInForSessionAsync(player.Name, "pass");

            foreach (var forged in new[] { "ws." + new string('A', 43), token + "x", token.Substring(0, token.Length - 1), "ws.", token.ToUpperInvariant() })
            {
                var response = await host.GetWithTokenAsync("/api/me", forged);
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, forged);
                Assert.AreEqual("unauthorized", await MarketApiHost.ErrorAsync(response));
            }
        }

        // ---- helpers

        /// <summary>
        /// The session row's columns, as text with microseconds, "-" for null
        /// </summary>
        private static string SessionTimes(string token, params string[] columns) => MarketApiTestData.Rows(
            "SELECT CONCAT_WS('|', " + string.Join(", ", columns.Select(c => $"IFNULL(DATE_FORMAT({c}, '{TimeFormat}'), '-')")) + ") " +
            $"FROM market_web_session WHERE token_Hash = UNHEX('{Sha256(token)}');").Single();

        /// <summary>
        /// A ban's expiry a day ahead of the host's clock, the clock the API judges bans by
        /// </summary>
        private static DateTime BanEnds(MarketApiHost host) => host.Clock.GetUtcNow().UtcDateTime.AddDays(1);

        private static DateTime Microseconds(DateTime utc) => new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);

        private static string Sql(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        private static string Base64(string base64Url)
        {
            var text = base64Url.Replace('-', '+').Replace('_', '/');
            return text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        }
    }
}
