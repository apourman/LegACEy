using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Market;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// Plugin sign-in: /vault link codes exchanged for tokens at POST /api/auth/plugin-token, bearer tokens on API calls,
    /// the website's token list and revoke, and tokens dying on a password change or during a ban
    /// </summary>
    [TestClass]
    public class MarketApiPluginTokenTests
    {
        // ---- link codes

        [TestMethod]
        public async Task LinkCode_Fresh_GivesAWorkingToken_StoredOnlyAsHashes()
        {
            var player = NewPlayer("lfresh");

            await using var host = await MarketApiHost.StartAsync();

            var code = NewLinkCode(player, host);
            var response = await ExchangeAsync(host, code, "Laptop");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = await MarketApiHost.JsonAsync(response);
            var token = body.GetProperty("token").GetString();
            Assert.AreEqual("Laptop", body.GetProperty("label").GetString());
            Assert.IsTrue(token.Length >= 32, "a long random token");

            var me = await host.GetWithTokenAsync("/api/me", token);
            Assert.AreEqual(HttpStatusCode.OK, me.StatusCode, await me.Content.ReadAsStringAsync());
            Assert.AreEqual(player.AccountId, (await MarketApiHost.JsonAsync(me)).GetProperty("accountId").GetUInt32());

            // only hashes are stored: the code and the token can't be read back out of the database
            using var shard = MarketApiTestData.Shard();
            var codeRow = shard.MarketLinkCodes.AsNoTracking().Single(c => c.AccountId == player.AccountId);
            CollectionAssert.AreEqual(MarketCredentials.Hash(code), codeRow.CodeHash);
            Assert.IsFalse(codeRow.CodeHash.SequenceEqual(Encoding.UTF8.GetBytes(code)));
            Assert.IsNotNull(codeRow.UsedTime);

            var tokenRow = shard.MarketPluginTokens.AsNoTracking().Single(t => t.AccountId == player.AccountId);
            CollectionAssert.AreEqual(MarketCredentials.Hash(token), tokenRow.TokenHash);
            Assert.IsFalse(tokenRow.TokenHash.SequenceEqual(Encoding.UTF8.GetBytes(token)));
            Assert.AreEqual(body.GetProperty("tokenId").GetInt64(), tokenRow.Id);
            Assert.AreEqual("Laptop", tokenRow.Label);
            CollectionAssert.AreEqual(MarketCredentials.Fingerprint(MarketApiTestData.PasswordHash(player.AccountId)), tokenRow.PasswordFingerprint);
            AssertTime(host.Clock.GetUtcNow().UtcDateTime.AddDays(90), tokenRow.ExpiresTime, "90 days by default");
        }

        [TestMethod]
        public async Task LinkCode_Reused_IsRefused()
        {
            var player = NewPlayer("lreuse");

            await using var host = await MarketApiHost.StartAsync();

            var code = NewLinkCode(player, host);

            Assert.AreEqual(HttpStatusCode.OK, (await ExchangeAsync(host, code, "first")).StatusCode);

            var again = await ExchangeAsync(host, code, "second");
            Assert.AreEqual(HttpStatusCode.Unauthorized, again.StatusCode);
            Assert.AreEqual("invalid_code", await MarketApiHost.ErrorAsync(again));

            Assert.AreEqual(1L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_plugin_token WHERE account_Id = {player.AccountId};"));
        }

        [TestMethod]
        public async Task LinkCode_UsedConcurrently_GivesExactlyOneToken()
        {
            var player = NewPlayer("lrace");

            await using var host = await MarketApiHost.StartAsync();

            var code = NewLinkCode(player, host);

            var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => ExchangeAsync(host, code, "racer" + i)));

            Assert.AreEqual(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK), string.Join(", ", responses.Select(r => r.StatusCode)));
            Assert.AreEqual(5, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
            Assert.AreEqual(1L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_plugin_token WHERE account_Id = {player.AccountId};"));
        }

        [TestMethod]
        public async Task LinkCode_Expired_IsRefused_AfterTheSettingsLifetime()
        {
            var player = NewPlayer("lexp");

            await using var host = await MarketApiHost.StartAsync();

            // default 5 minutes: good just before, refused at the expiry
            var early = NewLinkCode(player, host);
            var late = NewLinkCode(player, host);
            host.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            Assert.AreEqual(HttpStatusCode.OK, (await ExchangeAsync(host, early, "early")).StatusCode);
            host.Clock.Advance(TimeSpan.FromSeconds(1));
            var expired = await ExchangeAsync(host, late, "late");
            Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
            Assert.AreEqual("invalid_code", await MarketApiHost.ErrorAsync(expired));

            // the lifetime is the server setting
            MarketApiTestData.SetSetting(MarketSettings.LinkCodeMinutes.Key, 1);
            try
            {
                var oneMinute = NewLinkCode(player, host);
                host.Clock.Advance(TimeSpan.FromSeconds(61));
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await ExchangeAsync(host, oneMinute, "short")).StatusCode);
            }
            finally
            {
                MarketApiTestData.ClearSetting(MarketSettings.LinkCodeMinutes.Key);
            }
        }

        [TestMethod]
        public async Task LinkCode_UnknownOrMalformed_IsRefused_AndGuessingBlocksTheIp()
        {
            var player = NewPlayer("lguess");

            await using var host = await MarketApiHost.StartAsync();

            var missing = await host.PostJsonAsync("/api/auth/plugin-token", new { label = "x" });
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);

            var longLabel = await ExchangeAsync(host, NewLinkCode(player, host), new string('x', PluginAuth.LabelMaxLength + 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, longLabel.StatusCode, "a label longer than the column is refused, not cut");

            const string guesser = "10.9.9.9";
            for (var i = 0; i < 20; i++)
            {
                var wrong = await ExchangeAsync(host, $"WRONG{i:00000}", "guess", guesser);
                Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode, "guess " + i);
            }

            // 20 failures from one IP block it, even for a good code; other addresses are unaffected
            var good = NewLinkCode(player, host);
            var blocked = await ExchangeAsync(host, good, "blocked", guesser);
            Assert.AreEqual((HttpStatusCode)429, blocked.StatusCode);
            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(blocked));
            Assert.AreEqual(HttpStatusCode.OK, (await ExchangeAsync(host, good, "elsewhere")).StatusCode, "the blocked attempt didn't use up the code");
        }

        [TestMethod]
        public async Task LinkCode_BannedAccount_GetsNoToken()
        {
            var player = NewPlayer("lban");

            await using var host = await MarketApiHost.StartAsync();

            var code = NewLinkCode(player, host);
            MarketApiTestData.Ban(player.AccountId, host.Clock.GetUtcNow().UtcDateTime.AddDays(1));

            var refused = await ExchangeAsync(host, code, "banned");
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.AreEqual("banned", await MarketApiHost.ErrorAsync(refused));
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_plugin_token WHERE account_Id = {player.AccountId};"));
        }

        // ---- tokens on API calls

        [TestMethod]
        public async Task Token_AuthenticatesApiCalls_AndItsExpiryMovesForwardOnUse()
        {
            var player = NewPlayer("tslide");
            MarketApiTestData.SetBalance(player.AccountId, 42);

            await using var host = await MarketApiHost.StartAsync();

            var token = await NewTokenAsync(host, player);

            foreach (var path in new[] { "/api/me", "/api/vault", "/api/history", "/api/tokens" })
            {
                var response = await host.GetWithTokenAsync(path, token);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path + ": " + await response.Content.ReadAsStringAsync());
            }

            var history = await MarketApiHost.JsonAsync(await host.GetWithTokenAsync("/api/history", token));
            Assert.AreEqual(42, history.GetProperty("balance").GetInt64(), "the token signs in as its account");

            // used every 89 days, it outlives its 90 day lifetime
            for (var i = 0; i < 3; i++)
            {
                host.Clock.Advance(TimeSpan.FromDays(89));
                Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode, "use " + i);

                using var shard = MarketApiTestData.Shard();
                var row = shard.MarketPluginTokens.AsNoTracking().Single(t => t.AccountId == player.AccountId);
                var now = host.Clock.GetUtcNow().UtcDateTime;
                AssertTime(now, row.LastUsedTime.Value, "last used");
                AssertTime(now.AddDays(90), row.ExpiresTime, "expiry renewed");
            }
        }

        [TestMethod]
        public async Task Token_Unused_StopsWorkingAfterItsLifetime()
        {
            var player = NewPlayer("tidle");

            await using var host = await MarketApiHost.StartAsync();

            var token = await NewTokenAsync(host, player);

            host.Clock.Advance(TimeSpan.FromDays(90) - TimeSpan.FromSeconds(1));
            var stillGood = await NewTokenAsync(host, player);

            host.Clock.Advance(TimeSpan.FromSeconds(1));
            var expired = await host.GetWithTokenAsync("/api/me", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
            Assert.AreEqual("unauthorized", await MarketApiHost.ErrorAsync(expired));

            // the lifetime is the server setting, read when the token is issued and on each use
            MarketApiTestData.SetSetting(MarketSettings.PluginTokenDays.Key, 2);
            try
            {
                Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", stillGood)).StatusCode, "renewed now for 2 days");
                host.Clock.Advance(TimeSpan.FromDays(2));
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", stillGood)).StatusCode);
            }
            finally
            {
                MarketApiTestData.ClearSetting(MarketSettings.PluginTokenDays.Key);
            }
        }

        [TestMethod]
        public async Task Token_UnknownOrMissing_IsRefused()
        {
            await using var host = await MarketApiHost.StartAsync();

            var unknown = await host.GetWithTokenAsync("/api/me", "not-a-token");
            Assert.AreEqual(HttpStatusCode.Unauthorized, unknown.StatusCode);
            Assert.AreEqual("unauthorized", await MarketApiHost.ErrorAsync(unknown));

            var none = await host.GetAsync("/api/me");
            Assert.AreEqual(HttpStatusCode.Unauthorized, none.StatusCode);
            Assert.AreEqual("unauthorized", await MarketApiHost.ErrorAsync(none));

            // public routes ignore a bad token
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/listings", "not-a-token")).StatusCode);
        }

        // ---- listing and revoking

        [TestMethod]
        public async Task Tokens_AreListedOnTheWebsite_AndRevokingOneStopsItImmediately()
        {
            var player = NewPlayer("trev");
            var other = NewPlayer("trevother");

            await using var host = await MarketApiHost.StartAsync();

            var laptop = await NewTokenAsync(host, player, "Laptop");
            host.Clock.Advance(TimeSpan.FromMinutes(1));
            var desktop = await NewTokenAsync(host, player, "Desktop");
            var othersToken = await NewTokenAsync(host, other, "Other");

            var cookie = await host.SignInForCookieAsync(player.Name, player.Password);

            var listed = await MarketApiHost.JsonAsync(await host.GetAsync("/api/tokens", cookie));
            var tokens = listed.GetProperty("tokens").EnumerateArray().ToList();
            CollectionAssert.AreEqual(new[] { "Desktop", "Laptop" }, tokens.Select(t => t.GetProperty("label").GetString()).ToList(), "newest first, own tokens only");
            Assert.IsFalse(tokens[0].TryGetProperty("tokenHash", out _), "never the hash");
            Assert.IsTrue(tokens[0].TryGetProperty("expiresTime", out _));
            Assert.IsTrue(tokens[0].TryGetProperty("createdTime", out _));
            Assert.IsTrue(tokens[0].TryGetProperty("lastUsedTime", out _));

            var laptopId = tokens.Single(t => t.GetProperty("label").GetString() == "Laptop").GetProperty("id").GetInt64();
            var othersId = MarketApiTestData.Scalar($"SELECT id FROM market_plugin_token WHERE account_Id = {other.AccountId};");

            // another account's token can't be revoked
            var notMine = await host.PostJsonAsync($"/api/tokens/{othersId}/revoke", new { }, cookie);
            Assert.AreEqual(HttpStatusCode.NotFound, notMine.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", othersToken)).StatusCode);

            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", laptop)).StatusCode);

            var revoked = await host.PostJsonAsync($"/api/tokens/{laptopId}/revoke", new { }, cookie);
            Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode, await revoked.Content.ReadAsStringAsync());

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", laptop)).StatusCode, "revoked at once");
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", desktop)).StatusCode, "the other token still works");

            var after = await MarketApiHost.JsonAsync(await host.GetWithTokenAsync("/api/tokens", desktop));
            CollectionAssert.AreEqual(new[] { "Desktop" }, after.GetProperty("tokens").EnumerateArray().Select(t => t.GetProperty("label").GetString()).ToList());

            // a token can revoke itself (the plugin's sign-out)
            var desktopId = after.GetProperty("tokens")[0].GetProperty("id").GetInt64();
            Assert.AreEqual(HttpStatusCode.OK, (await host.PostJsonAsync($"/api/tokens/{desktopId}/revoke", new { }, token: desktop)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", desktop)).StatusCode);

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/api/tokens")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await host.PostJsonAsync("/api/tokens/abc/revoke", new { }, cookie)).StatusCode);
        }

        // ---- invalidation

        [TestMethod]
        public async Task PasswordChange_InvalidatesEveryExistingToken()
        {
            var player = NewPlayer("tpass");

            await using var host = await MarketApiHost.StartAsync();

            var first = await NewTokenAsync(host, player, "one");
            var second = await NewTokenAsync(host, player, "two");
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", first)).StatusCode);

            MarketApiTestData.SetPassword(player.AccountId, "new-pass");

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", first)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", second)).StatusCode);

            // they're gone from the list too; a new link after the change works
            var cookie = await host.SignInForCookieAsync(player.Name, "new-pass");
            Assert.AreEqual(0, (await MarketApiHost.JsonAsync(await host.GetAsync("/api/tokens", cookie))).GetProperty("tokens").GetArrayLength());

            var fresh = await NewTokenAsync(host, player, "three");
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", fresh)).StatusCode);
        }

        [TestMethod]
        public async Task Ban_RefusesTokens_AndTheyWorkAgainWhenItEnds()
        {
            var player = NewPlayer("tban");

            await using var host = await MarketApiHost.StartAsync();

            var token = await NewTokenAsync(host, player);

            // a ban an admin lifts
            MarketApiTestData.Ban(player.AccountId, host.Clock.GetUtcNow().UtcDateTime.AddDays(30));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", token)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/history", token)).StatusCode);
            MarketApiTestData.LiftBan(player.AccountId);
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode);

            // a ban that runs out
            MarketApiTestData.Ban(player.AccountId, host.Clock.GetUtcNow().UtcDateTime.AddDays(3));
            host.Clock.Advance(TimeSpan.FromDays(2));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetWithTokenAsync("/api/me", token)).StatusCode);
            host.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", token)).StatusCode, "the token wasn't revoked by the ban");
        }

        // ---- helpers

        /// <summary>
        /// datetime(6) keeps microseconds, the clock has ticks
        /// </summary>
        private static void AssertTime(DateTime expected, DateTime actual, string what)
        {
            Assert.IsTrue(Math.Abs((expected - actual).TotalMilliseconds) < 1, $"{what}: expected {expected:O}, was {actual:O}");
        }

        private sealed record Player(string Name, string Password, uint AccountId, uint CharacterId);

        private static Player NewPlayer(string prefix)
        {
            var name = MarketApiTestData.UniqueName(prefix);
            var accountId = MarketApiTestData.CreateAccount(name, "p-pass");
            var characterId = MarketApiTestData.AddCharacter(accountId, name + "Main");

            return new Player(name, "p-pass", accountId, characterId);
        }

        /// <summary>
        /// A code as /vault link makes it, at the API's clock
        /// </summary>
        private static string NewLinkCode(Player player, MarketApiHost host)
        {
            using var shard = MarketApiTestData.Shard();

            return PluginAuth.NewLinkCode(shard, player.AccountId, player.CharacterId, host.Clock.GetUtcNow().UtcDateTime);
        }

        private static Task<HttpResponseMessage> ExchangeAsync(MarketApiHost host, string code, string label, string ip = MarketApiHost.DefaultIp) =>
            host.PostJsonAsync("/api/auth/plugin-token", new { code, label }, ip: ip);

        private static async Task<string> NewTokenAsync(MarketApiHost host, Player player, string label = "plugin")
        {
            var response = await ExchangeAsync(host, NewLinkCode(player, host), label);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            return (await MarketApiHost.JsonAsync(response)).GetProperty("token").GetString();
        }
    }
}
