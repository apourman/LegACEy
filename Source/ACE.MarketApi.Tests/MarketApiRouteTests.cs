using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Routing;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// The API's URL shape and request rules for the website: every route under /api (the website owns /), the URLs the API hands out include /api,
    /// a cookie-signed request that changes something must carry X-Market-Request (CSRF), and no CORS.
    /// </summary>
    [TestClass]
    public class MarketApiRouteTests
    {
        private sealed record Player(string Name, uint AccountId, uint CharacterId);

        private static Player NewPlayer(string prefix)
        {
            var name = MarketApiTestData.UniqueName(prefix);
            var accountId = MarketApiTestData.CreateAccount(name, "pass");

            return new Player(name, accountId, MarketApiTestData.AddCharacter(accountId, name + "Main"));
        }

        private static string NewKey() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// Every route the API maps, as it was at the root before it moved under /api
        /// </summary>
        private static readonly (string Method, string Path)[] Routes =
        {
            ("POST", "/auth/login"),
            ("POST", "/auth/logout"),
            ("POST", "/auth/plugin-token"),
            ("GET", "/me"),
            ("GET", "/vault"),
            ("GET", "/vault/1"),
            ("GET", "/listings"),
            ("GET", "/listings/suggest?q=a"),
            ("GET", "/listings/1"),
            ("GET", "/facets"),
            ("POST", "/listings"),
            ("POST", "/listings/1/delist"),
            ("POST", "/listings/1/purchase"),
            ("GET", "/history"),
            ("GET", "/tokens"),
            ("POST", "/tokens/1/revoke"),
            ("GET", "/tickets"),
            ("GET", "/icons/glow.css"),
            ("GET", "/icons/0x06003237.png"),
            ("POST", "/vault/withdraw"),
            ("POST", "/vault/deposit"),
            ("POST", "/inventory/snapshot"),
            ("POST", "/mmd/withdraw"),
            ("GET", "/tickets/1"),
            ("POST", "/auth/session"),
            ("DELETE", "/auth/session"),
        };

        private static Task<HttpResponseMessage> SendAsync(MarketApiHost host, string method, string path, string cookie)
        {
            return method switch
            {
                "GET" => host.GetAsync(path, cookie),
                "POST" => host.PostJsonAsync(path, new { }, cookie),
                _ => host.SendAsync(new HttpMethod(method), path, cookie),
            };
        }

        // ---- /api

        [TestMethod]
        public async Task EveryRoute_AnswersUnderApi_AndNotAtTheRoot()
        {
            var player = NewPlayer("route");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            // the list below is every route the API maps, and every one of them is under /api
            var mapped = ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
            Assert.AreEqual(Routes.Length, mapped.Count, string.Join(", ", mapped.Select(e => e.RoutePattern.RawText)));
            foreach (var endpoint in mapped)
                StringAssert.StartsWith(endpoint.RoutePattern.RawText, MarketApi.PathBase + "/");

            foreach (var (method, path) in Routes)
            {
                // a route the API doesn't map answers 404 with no body; an API "not found" is a JSON error
                var atRoot = await SendAsync(host, method, path, cookie);
                Assert.AreEqual(HttpStatusCode.NotFound, atRoot.StatusCode, $"{method} {path} at the root");
                Assert.AreEqual("", await atRoot.Content.ReadAsStringAsync(), $"{method} {path} at the root");

                var underApi = await SendAsync(host, method, MarketApi.PathBase + path, cookie);

                if (underApi.StatusCode == HttpStatusCode.NotFound)
                    Assert.AreEqual("not_found", await MarketApiHost.ErrorAsync(underApi), $"{method} /api{path}");
            }
        }

        [TestMethod]
        public async Task CreatedListingAndTicket_LocationIncludesApi_AndAnswers()
        {
            var player = NewPlayer("location");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Located Helm", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var listed = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, cookie);
            Assert.AreEqual(HttpStatusCode.Created, listed.StatusCode, await listed.Content.ReadAsStringAsync());
            var listingId = (await MarketApiHost.JsonAsync(listed)).GetProperty("id").GetInt64();
            Assert.AreEqual($"/api/listings/{listingId}", listed.Headers.Location?.OriginalString);
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync(listed.Headers.Location.OriginalString)).StatusCode);

            var ticket = await host.PostJsonAsync("/api/mmd/withdraw", new { characterId = player.CharacterId, amount = 1, idempotencyKey = NewKey() }, cookie);
            Assert.AreEqual(HttpStatusCode.Accepted, ticket.StatusCode, await ticket.Content.ReadAsStringAsync());
            var ticketId = (await MarketApiHost.JsonAsync(ticket)).GetProperty("id").GetInt64();
            Assert.AreEqual($"/api/tickets/{ticketId}", ticket.Headers.Location?.OriginalString);
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync(ticket.Headers.Location.OriginalString, cookie)).StatusCode);
        }

        [TestMethod]
        public async Task IconUrls_IncludeApi_AndServeThePng()
        {
            var player = NewPlayer("iconurl");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Urled Breastplate", VaultItemState.Held);
            MarketApiTestData.SetVaultColumns(guid, $"item_Type = {(int)ItemType.Armor}, icon = {0x06003237u}, ui_Effects = 1");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var listed = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, cookie);
            var listingId = (await MarketApiHost.JsonAsync(listed)).GetProperty("id").GetInt64();

            var icon = (await MarketApiHost.JsonAsync(await host.GetAsync($"/api/listings/{listingId}"))).GetProperty("icon");
            var urls = icon.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("url").GetString()).ToList();

            Assert.IsTrue(urls.Count >= 2, "a plate and the icon");

            foreach (var url in urls)
            {
                StringAssert.StartsWith(url, "/api/icons/");

                var png = await host.GetAsync(url);
                Assert.AreEqual(HttpStatusCode.OK, png.StatusCode, url);
                Assert.AreEqual("image/png", png.Content.Headers.ContentType?.MediaType, url);
            }
        }

        [TestMethod]
        public async Task CrossOriginRequest_GetsNoCorsHeaders()
        {
            await using var host = await MarketApiHost.StartAsync();

            var get = new HttpRequestMessage(HttpMethod.Get, "/api/facets");
            get.Headers.Add("Origin", "https://elsewhere.example");
            var response = await host.Client.SendAsync(get);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse(response.Headers.Contains("Access-Control-Allow-Origin"));

            var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/listings");
            preflight.Headers.Add("Origin", "https://elsewhere.example");
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "x-market-request");
            var preflightResponse = await host.Client.SendAsync(preflight);
            Assert.IsFalse(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.IsFalse(preflightResponse.Headers.Contains("Access-Control-Allow-Headers"));
        }

        // ---- CSRF

        [TestMethod]
        public async Task CookiePost_WithoutRequestHeader_IsRefusedCsrf_AndWritesNothing()
        {
            var player = NewPlayer("csrf");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Forged Helm", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var list = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, cookie, requestHeader: false);
            Assert.AreEqual(HttpStatusCode.Forbidden, list.StatusCode);
            Assert.AreEqual("csrf", await MarketApiHost.ErrorAsync(list));

            var withdraw = await host.PostJsonAsync("/api/mmd/withdraw", new { characterId = player.CharacterId, amount = 1, idempotencyKey = NewKey() }, cookie, requestHeader: false);
            Assert.AreEqual(HttpStatusCode.Forbidden, withdraw.StatusCode);
            Assert.AreEqual("csrf", await MarketApiHost.ErrorAsync(withdraw));

            var logout = await host.SendAsync(HttpMethod.Post, "/api/auth/logout", cookie, requestHeader: false);
            Assert.AreEqual(HttpStatusCode.Forbidden, logout.StatusCode);
            Assert.AreEqual("csrf", await MarketApiHost.ErrorAsync(logout));

            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE item_Guid = {guid};"));
            Assert.AreEqual(VaultItemState.Held, MarketApiTestData.Rows($"SELECT state FROM market_vault_item WHERE item_Guid = {guid};").Single());
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_ticket WHERE account_Id = {player.AccountId};"));

            // reads need no header
            Assert.AreEqual(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/me", cookie, requestHeader: false)).StatusCode);
        }

        [TestMethod]
        public async Task CookiePost_WithRequestHeader_Works()
        {
            var player = NewPlayer("csrfok");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Honest Helm", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var list = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, cookie, requestHeader: true);
            Assert.AreEqual(HttpStatusCode.Created, list.StatusCode, await list.Content.ReadAsStringAsync());

            var logout = await host.SendAsync(HttpMethod.Post, "/api/auth/logout", cookie, requestHeader: true);
            Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode, await logout.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task SignIn_WithoutACookie_NeedsNoRequestHeader()
        {
            var player = NewPlayer("csrfsignin");

            await using var host = await MarketApiHost.StartAsync();

            // SignInAsync sends no X-Market-Request: a request with no session cookie has no ambient credential to forge
            var response = await host.SignInAsync(player.Name, "pass");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task PluginTokenPost_WithoutRequestHeader_Works()
        {
            var player = NewPlayer("csrftoken");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Plugged Helm", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();

            string code;
            using (var shard = MarketApiTestData.Shard())
                code = PluginAuth.NewLinkCode(shard, player.AccountId, player.CharacterId, host.Clock.GetUtcNow().UtcDateTime);

            var exchange = await host.PostJsonAsync("/api/auth/plugin-token", new { code, label = "plugin" }, requestHeader: false);
            Assert.AreEqual(HttpStatusCode.OK, exchange.StatusCode, await exchange.Content.ReadAsStringAsync());
            var token = (await MarketApiHost.JsonAsync(exchange)).GetProperty("token").GetString();

            var list = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, token: token, requestHeader: false);
            Assert.AreEqual(HttpStatusCode.Created, list.StatusCode, await list.Content.ReadAsStringAsync());

            var withdraw = await host.PostJsonAsync("/api/mmd/withdraw", new { characterId = player.CharacterId, amount = 1, idempotencyKey = NewKey() }, token: token, requestHeader: false);
            Assert.AreEqual(HttpStatusCode.Accepted, withdraw.StatusCode, await withdraw.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task CookieAndABadToken_WithoutRequestHeader_IsRefusedCsrf()
        {
            var player = NewPlayer("csrfmixed");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Mixed Helm", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            // an unusable token doesn't make the request a plugin request: the cookie still signs it in
            var list = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, cookie, token: "not-a-token", requestHeader: false);
            Assert.AreEqual(HttpStatusCode.Forbidden, list.StatusCode);
            Assert.AreEqual("csrf", await MarketApiHost.ErrorAsync(list));
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE item_Guid = {guid};"));
        }
    }
}
