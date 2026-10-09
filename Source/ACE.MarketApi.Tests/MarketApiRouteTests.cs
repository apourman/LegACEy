using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// The API's URL shape and request rules: every route under /api, the URLs the API hands out include /api, no CORS, and the website's concerns
    /// gone (ticket 03): the API takes no cookie (an old session cookie signs in nowhere) and needs no CSRF header; the BFF owns both.
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
            ("GET", "/icons/0x06003237.png"),
            ("POST", "/vault/withdraw"),
            ("POST", "/vault/deposit"),
            ("POST", "/inventory/snapshot"),
            ("POST", "/mmd/withdraw"),
            ("GET", "/tickets/1"),
            ("POST", "/auth/session"),
            ("DELETE", "/auth/session"),
        };

        private static Task<HttpResponseMessage> SendAsync(MarketApiHost host, string method, string path, string session)
        {
            return method switch
            {
                "GET" => host.GetAsync(path, session),
                "POST" => host.PostJsonAsync(path, new { }, session),
                _ => host.SendAsync(new HttpMethod(method), path, session),
            };
        }

        // ---- /api

        [TestMethod]
        public async Task EveryRoute_AnswersUnderApi_AndNotAtTheRoot()
        {
            var player = NewPlayer("route");

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(player.Name, "pass");

            // the list below is every route the API maps, and every one of them is under /api
            var mapped = ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
            Assert.AreEqual(Routes.Length, mapped.Count, string.Join(", ", mapped.Select(e => e.RoutePattern.RawText)));
            foreach (var endpoint in mapped)
                StringAssert.StartsWith(endpoint.RoutePattern.RawText, MarketApi.PathBase + "/");

            foreach (var (method, path) in Routes)
            {
                // a route the API doesn't map answers 404 with no body; an API "not found" is a JSON error
                var atRoot = await SendAsync(host, method, path, session);
                Assert.AreEqual(HttpStatusCode.NotFound, atRoot.StatusCode, $"{method} {path} at the root");
                Assert.AreEqual("", await atRoot.Content.ReadAsStringAsync(), $"{method} {path} at the root");

                var underApi = await SendAsync(host, method, MarketApi.PathBase + path, session);

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
            var session = await host.SignInForSessionAsync(player.Name, "pass");

            var listed = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, session);
            Assert.AreEqual(HttpStatusCode.Created, listed.StatusCode, await listed.Content.ReadAsStringAsync());
            var listingId = (await MarketApiHost.JsonAsync(listed)).GetProperty("id").GetInt64();
            Assert.AreEqual($"/api/listings/{listingId}", listed.Headers.Location?.OriginalString);
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync(listed.Headers.Location.OriginalString)).StatusCode);

            var ticket = await host.PostJsonAsync("/api/mmd/withdraw", new { characterId = player.CharacterId, amount = 1, idempotencyKey = NewKey() }, session);
            Assert.AreEqual(HttpStatusCode.Accepted, ticket.StatusCode, await ticket.Content.ReadAsStringAsync());
            var ticketId = (await MarketApiHost.JsonAsync(ticket)).GetProperty("id").GetInt64();
            Assert.AreEqual($"/api/tickets/{ticketId}", ticket.Headers.Location?.OriginalString);
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync(ticket.Headers.Location.OriginalString, session)).StatusCode);
        }

        [TestMethod]
        public async Task IconUrls_IncludeApi_AndServeThePng()
        {
            var player = NewPlayer("iconurl");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Urled Breastplate", VaultItemState.Held);
            MarketApiTestData.SetVaultColumns(guid, $"item_Type = {(int)ItemType.Armor}, icon = {0x06003237u}, ui_Effects = 1");

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(player.Name, "pass");

            var listed = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, session);
            var listingId = (await MarketApiHost.JsonAsync(listed)).GetProperty("id").GetInt64();

            var icon = (await MarketApiHost.JsonAsync(await host.GetAsync($"/api/listings/{listingId}"))).GetProperty("icon");
            var urls = icon.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("url").GetString()).ToList();

            Assert.AreEqual(1, urls.Count, "the composed base icon alone: no plate");

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


        // ---- no cookie, no CSRF header: the BFF owns both

        /// <summary>
        /// A session cookie as the removed cookie scheme wrote it: an authentication ticket for the account, protected by ASP.NET data protection
        /// under the API's old application name and the cookie scheme's purposes. A browser holding one from before the BFF sends exactly this.
        /// </summary>
        private static string OldSessionCookie(Player player)
        {
            var keys = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ace-market-api-tests", Guid.NewGuid().ToString("N")));
            var protector = DataProtectionProvider.Create(keys, builder => builder.SetApplicationName("ACE.MarketApi"))
                .CreateProtector("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", CookieAuthenticationDefaults.AuthenticationScheme, "v2");

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, player.AccountId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, player.Name),
            }, CookieAuthenticationDefaults.AuthenticationScheme);

            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);

            return "market_session=" + new TicketDataFormat(protector).Protect(ticket);
        }

        [TestMethod]
        public async Task OldSessionCookie_SignsInNowhere_AndNoRouteSetsACookie()
        {
            var player = NewPlayer("oldcookie");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Cookied Helm", VaultItemState.Held);
            var cookie = OldSessionCookie(player);

            await using var host = await MarketApiHost.StartAsync();

            var mapped = ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();

            foreach (var endpoint in mapped)
            {
                var signedIn = endpoint.Metadata.GetMetadata<IAuthorizeData>() != null;
                var path = Regex.Replace(endpoint.RoutePattern.RawText, "{[^}]+}", "1");

                foreach (var method in endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods)
                {
                    var request = new HttpRequestMessage(new HttpMethod(method), path);
                    request.Headers.Add(MarketApiHost.RemoteIpHeader, MarketApiHost.DefaultIp);
                    request.Headers.Add("Cookie", cookie);
                    request.Headers.Add("X-Market-Request", "1");
                    if (method == "POST")
                        request.Content = JsonContent.Create(new { });

                    var response = await host.Client.SendAsync(request);
                    var what = $"{method} {path}";

                    Assert.IsFalse(response.Headers.Contains("Set-Cookie"), what);
                    if (signedIn)
                    {
                        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, what);
                        Assert.AreEqual("unauthorized", await MarketApiHost.ErrorAsync(response), what);
                    }
                }
            }

            // a write with the old cookie and a bearer token that isn't usable: still nobody, and nothing written
            var listing = new HttpRequestMessage(HttpMethod.Post, "/api/listings") { Content = JsonContent.Create(new { itemGuid = guid, price = 10 }) };
            listing.Headers.Add(MarketApiHost.RemoteIpHeader, MarketApiHost.DefaultIp);
            listing.Headers.Add("Cookie", cookie);
            listing.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(listing)).StatusCode);

            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE item_Guid = {guid};"));
            Assert.AreEqual(VaultItemState.Held, MarketApiTestData.Rows($"SELECT state FROM market_vault_item WHERE item_Guid = {guid};").Single());
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_ticket WHERE account_Id = {player.AccountId};"));

            // the cookie is ignored, not the account: a web session for it works
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/me", await host.SignInForSessionAsync(player.Name, "pass"))).StatusCode);
        }

        [TestMethod]
        public async Task SessionWrites_NeedNoRequestHeader()
        {
            var player = NewPlayer("nocsrf");
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Honest Helm", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(player.Name, "pass");

            // MarketApiHost never sends X-Market-Request: the API has no CSRF check
            var list = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, session);
            Assert.AreEqual(HttpStatusCode.Created, list.StatusCode, await list.Content.ReadAsStringAsync());
            var listingId = (await MarketApiHost.JsonAsync(list)).GetProperty("id").GetInt64();

            var delist = await host.PostJsonAsync($"/api/listings/{listingId}/delist", new { }, session);
            Assert.AreEqual(HttpStatusCode.OK, delist.StatusCode, await delist.Content.ReadAsStringAsync());

            var withdraw = await host.PostJsonAsync("/api/mmd/withdraw", new { characterId = player.CharacterId, amount = 1, idempotencyKey = NewKey() }, session);
            Assert.AreEqual(HttpStatusCode.Accepted, withdraw.StatusCode, await withdraw.Content.ReadAsStringAsync());

            // a header a website once sent changes nothing either way
            var withHeader = new HttpRequestMessage(HttpMethod.Post, "/api/mmd/withdraw") { Content = JsonContent.Create(new { characterId = player.CharacterId, amount = 1, idempotencyKey = NewKey() }) };
            withHeader.Headers.Add(MarketApiHost.RemoteIpHeader, MarketApiHost.DefaultIp);
            withHeader.Headers.Add("X-Market-Request", "0");
            withHeader.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
            Assert.AreEqual(HttpStatusCode.Accepted, (await host.Client.SendAsync(withHeader)).StatusCode);

            var signOut = await host.SendAsync(HttpMethod.Delete, "/api/auth/session", session);
            Assert.AreEqual(HttpStatusCode.OK, signOut.StatusCode, await signOut.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task SignIn_NeedsNoRequestHeader()
        {
            var player = NewPlayer("csrfsignin");

            await using var host = await MarketApiHost.StartAsync();

            // SignInAsync sends no X-Market-Request
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

            var exchange = await host.PostJsonAsync("/api/auth/plugin-token", new { code, label = "plugin" });
            Assert.AreEqual(HttpStatusCode.OK, exchange.StatusCode, await exchange.Content.ReadAsStringAsync());
            var token = (await MarketApiHost.JsonAsync(exchange)).GetProperty("token").GetString();

            var list = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 10 }, token: token);
            Assert.AreEqual(HttpStatusCode.Created, list.StatusCode, await list.Content.ReadAsStringAsync());

            var withdraw = await host.PostJsonAsync("/api/mmd/withdraw", new { characterId = player.CharacterId, amount = 1, idempotencyKey = NewKey() }, token: token);
            Assert.AreEqual(HttpStatusCode.Accepted, withdraw.StatusCode, await withdraw.Content.ReadAsStringAsync());
        }
    }
}
