using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Routing;

using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// The private API's front door: the service key on every request but health, checked before routing and authentication;
    /// the bare health check; and X-Market-Client-Ip, trusted only alongside the key
    /// </summary>
    [TestClass]
    public class MarketApiServiceKeyTests
    {
        private static readonly string WrongKeySameLength = new string('0', MarketApiHost.ServiceKey.Length);

        // ---- the key

        [TestMethod]
        public async Task EveryRoute_MissingOrWrongKey_GetsABare401_EvenWithAValidSignIn()
        {
            var name = MarketApiTestData.UniqueName("keyless");
            MarketApiTestData.CreateAccount(name, "pass");

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(name, "pass");
            using var keyless = host.ClientWithoutKey();

            var routes = ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                .SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>().HttpMethods.Select(m => (Method: m, Path: Regex.Replace(e.RoutePattern.RawText, "{[^}]+}", "1"))))
                .Concat(new[] { ("GET", "/api/no-such-route"), ("GET", "/"), ("POST", "/health"), ("OPTIONS", "/api/listings"), ("GET", "/api/health") })
                .ToList();

            Assert.IsTrue(routes.Count > 25, "every mapped route, and some that aren't");

            var keys = new (string Label, string[] Values)[]
            {
                ("no key", Array.Empty<string>()),
                ("wrong key, same length", new[] { WrongKeySameLength }),
                ("wrong key, shorter", new[] { MarketApiHost.ServiceKey.Substring(0, 10) }),
                ("wrong key, longer", new[] { MarketApiHost.ServiceKey + "0" }),
                ("empty key", new[] { "" }),
                ("the key twice", new[] { MarketApiHost.ServiceKey, MarketApiHost.ServiceKey }),
                ("the key and a wrong one", new[] { MarketApiHost.ServiceKey, WrongKeySameLength }),
            };

            foreach (var (method, path) in routes)
            {
                foreach (var (label, values) in keys)
                {
                    var request = new HttpRequestMessage(new HttpMethod(method), path);
                    request.Headers.Add(MarketApiHost.RemoteIpHeader, MarketApiHost.DefaultIp);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
                    if (method == "POST")
                        request.Content = JsonContent.Create(new { account = name, password = "pass" });

                    foreach (var value in values)
                        request.Headers.TryAddWithoutValidation(ServiceGate.KeyHeader, value);

                    var response = await keyless.SendAsync(request);

                    var what = $"{method} {path} with {label}";
                    Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, what);
                    Assert.AreEqual("", await response.Content.ReadAsStringAsync(), what);
                    Assert.IsFalse(response.Headers.Contains("WWW-Authenticate"), what);
                    Assert.IsFalse(response.Headers.Contains("Set-Cookie"), what);
                }
            }

            // the same sign-in works with the key: the refusals above were the key's alone
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetWithTokenAsync("/api/me", session)).StatusCode);
        }

        [TestMethod]
        public async Task WithoutTheKey_NothingRuns_SoFailedSignInsCountForNothing()
        {
            var name = MarketApiTestData.UniqueName("keyfirst");
            MarketApiTestData.CreateAccount(name, "right");

            await using var host = await MarketApiHost.StartAsync();
            using var keyless = host.ClientWithoutKey();

            // far past the account (5) and IP (20) limits, without the key
            for (var i = 0; i < 25; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/session") { Content = JsonContent.Create(new { account = name, password = "wrong" }) };
                request.Headers.Add(MarketApiHost.RemoteIpHeader, "10.6.6.6");
                request.Headers.Add(ServiceGate.ClientIpHeader, "198.51.100.66");
                request.Headers.Add(ServiceGate.KeyHeader, WrongKeySameLength);

                Assert.AreEqual(HttpStatusCode.Unauthorized, (await keyless.SendAsync(request)).StatusCode);
            }

            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(name, "right", "10.6.6.6", clientIp: "198.51.100.66")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(name, "right", "10.6.6.6")).StatusCode);
        }

        [TestMethod]
        public async Task ClientIpHeader_WithAMissingOrWrongKey_CountsNothingTowardThatIpsLimit()
        {
            const string headerIp = "198.51.100.77";
            var victim = MarketApiTestData.UniqueName("cipkey");
            MarketApiTestData.CreateAccount(victim, "right");

            await using var host = await MarketApiHost.StartAsync();
            using var keyless = host.ClientWithoutKey();

            // 30 failures naming headerIp, each over a different unknown account (so no account lock): more than the 20 that block an IP
            for (var i = 0; i < 30; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/session")
                {
                    Content = JsonContent.Create(new { account = MarketApiTestData.UniqueName("ghost"), password = "wrong" }),
                };
                request.Headers.Add(MarketApiHost.RemoteIpHeader, "10.23.0.1");
                request.Headers.Add(ServiceGate.ClientIpHeader, headerIp);
                if (i % 2 == 1)
                    request.Headers.Add(ServiceGate.KeyHeader, WrongKeySameLength);

                Assert.AreEqual(HttpStatusCode.Unauthorized, (await keyless.SendAsync(request)).StatusCode, i % 2 == 1 ? "wrong key" : "no key");
            }

            // a valid-key sign-in from headerIp gets the normal answer, not ip_blocked
            var signIn = await host.SignInAsync(victim, "right", "10.23.0.1", clientIp: headerIp);
            Assert.AreEqual(HttpStatusCode.OK, signIn.StatusCode, await signIn.Content.ReadAsStringAsync());

            // the same failures with the key do block it, so the limit above was really in reach
            var blockedAfterKeyed = MarketApiTestData.UniqueName("cipkeyed");
            MarketApiTestData.CreateAccount(blockedAfterKeyed, "right");
            await FailTwentyTimes(host, "10.23.0.1", headerIp);
            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(await host.SignInAsync(blockedAfterKeyed, "right", "10.23.0.1", clientIp: headerIp)));
        }

        // ---- health

        [TestMethod]
        public async Task Health_AnswersABareOk_WithoutTheKey()
        {
            await using var host = await MarketApiHost.StartAsync();
            using var keyless = host.ClientWithoutKey();

            foreach (var key in new[] { null, WrongKeySameLength, MarketApiHost.ServiceKey })
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/health");
                if (key != null)
                    request.Headers.Add(ServiceGate.KeyHeader, key);

                var response = await keyless.SendAsync(request);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, key ?? "no key");
                Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());
                Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);
                Assert.IsFalse(response.Headers.Contains("Set-Cookie"));
                CollectionAssert.AreEquivalent(new[] { "Content-Type" }, response.Content.Headers.Select(h => h.Key).Where(h => h != "Content-Length").ToArray(), "no other content headers");
                Assert.AreEqual(0, response.Headers.Count(), "no response headers: " + string.Join(", ", response.Headers.Select(h => h.Key)));
            }

            var head = await keyless.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"));
            Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
            Assert.AreEqual("", await head.Content.ReadAsStringAsync());
        }

        // ---- X-Market-Client-Ip

        [TestMethod]
        public async Task ClientIpHeader_WithTheKey_IsTheIpTheSignInLimitsCount()
        {
            const string bff = "10.20.0.2";
            var victim = MarketApiTestData.UniqueName("cipv");
            MarketApiTestData.CreateAccount(victim, "right");

            await using var host = await MarketApiHost.StartAsync();

            await FailTwentyTimes(host, bff, "203.0.113.7");

            var blocked = await host.SignInAsync(victim, "right", bff, clientIp: "203.0.113.7");
            Assert.AreEqual(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(blocked));

            // the same BFF connection for another player, or with no client IP (the connection address), isn't blocked
            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(victim, "right", bff, clientIp: "203.0.113.8")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(victim, "right", bff)).StatusCode);

            // the address is the player's, whichever connection carries it: another BFF connection with the same client IP is blocked too
            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(await host.SignInAsync(victim, "right", "10.99.0.1", clientIp: "203.0.113.7")));
        }

        [TestMethod]
        public async Task ClientIpHeader_WithoutIt_TheConnectionAddressIsCounted()
        {
            var victim = MarketApiTestData.UniqueName("cipc");
            MarketApiTestData.CreateAccount(victim, "right");

            await using var host = await MarketApiHost.StartAsync();

            await FailTwentyTimes(host, "10.21.0.5", clientIp: null);

            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(await host.SignInAsync(victim, "right", "10.21.0.5")));
            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(victim, "right", "10.21.0.5", clientIp: "203.0.113.21")).StatusCode);
        }

        [TestMethod]
        public async Task ClientIpHeader_Ipv6_IsLimitedPerAddress_AndMappedIpv4CountsAsIpv4()
        {
            var victim = MarketApiTestData.UniqueName("cip6");
            MarketApiTestData.CreateAccount(victim, "right");

            await using var host = await MarketApiHost.StartAsync();

            await FailTwentyTimes(host, MarketApiHost.DefaultIp, "2001:db8:1:2::10");

            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(await host.SignInAsync(victim, "right", clientIp: "2001:db8:1:2::10")));
            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(await host.SignInAsync(victim, "right", clientIp: "2001:0db8:0001:0002:0000:0000:0000:0010")), "the same address written out");
            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(victim, "right", clientIp: "2001:db8:1:2::11")).StatusCode, "another address in the same /64");

            await FailTwentyTimes(host, MarketApiHost.DefaultIp, "::ffff:203.0.113.30");

            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(await host.SignInAsync(victim, "right", clientIp: "203.0.113.30")));
        }

        [TestMethod]
        public async Task ClientIpHeader_NotOnePlainAddress_GetsABare400_AndCountsForNothing()
        {
            var name = MarketApiTestData.UniqueName("cipbad");
            MarketApiTestData.CreateAccount(name, "right");

            await using var host = await MarketApiHost.StartAsync();

            var bad = new[]
            {
                "not-an-ip", "10.1", "167772161", "010.0.0.1", "10.0.0.256", "10.0.0.1, 10.0.0.2", "fe80::1%eth0", "[2001:db8::1]", "[2001:db8::1]:443",
                "2001:db8::1:", "10.0.0.1:8080", new string('1', 100),
            };

            foreach (var value in bad.Concat(bad).Concat(bad))
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/session") { Content = JsonContent.Create(new { account = name, password = "wrong" }) };
                request.Headers.Add(MarketApiHost.RemoteIpHeader, "10.22.0.1");
                request.Headers.TryAddWithoutValidation(ServiceGate.ClientIpHeader, value);

                var response = await host.Client.SendAsync(request);

                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"'{value}'");
                Assert.AreEqual("", await response.Content.ReadAsStringAsync(), $"'{value}'");
            }

            // two headers, each a good address
            var twice = new HttpRequestMessage(HttpMethod.Post, "/api/auth/session") { Content = JsonContent.Create(new { account = name, password = "wrong" }) };
            twice.Headers.Add(ServiceGate.ClientIpHeader, new[] { "203.0.113.40", "203.0.113.41" });
            Assert.AreEqual(HttpStatusCode.BadRequest, (await host.Client.SendAsync(twice)).StatusCode);

            // the refused attempts, far more than the limits, reached neither the account lock nor the connection's IP block
            Assert.AreEqual(HttpStatusCode.OK, (await host.SignInAsync(name, "right", "10.22.0.1")).StatusCode);
        }

        [TestMethod]
        public async Task ClientIpHeader_WinsOverXForwardedFor_WhichIsNeverCounted()
        {
            const string proxy = "10.8.8.9";
            var name = MarketApiTestData.UniqueName("cipxff");
            MarketApiTestData.CreateAccount(name, "right");

            // the old Market:TrustedProxies setting is gone; set as before, it changes nothing
            await using var host = await MarketApiHost.StartAsync(extraArgs: $"--Market:TrustedProxies:0={proxy}");

            for (var i = 0; i < 20; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/session") { Content = JsonContent.Create(new { account = MarketApiTestData.UniqueName("ghost"), password = "wrong" }) };
                request.Headers.Add(MarketApiHost.RemoteIpHeader, proxy);
                request.Headers.Add("X-Forwarded-For", "198.51.100.50");
                request.Headers.Add(ServiceGate.ClientIpHeader, "203.0.113.50");
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(request)).StatusCode);
            }

            Assert.AreEqual("ip_blocked", await MarketApiHost.ErrorAsync(await host.SignInAsync(name, "right", proxy, clientIp: "203.0.113.50")));

            var forwarded = new HttpRequestMessage(HttpMethod.Post, "/api/auth/session") { Content = JsonContent.Create(new { account = name, password = "right" }) };
            forwarded.Headers.Add(MarketApiHost.RemoteIpHeader, proxy);
            forwarded.Headers.Add("X-Forwarded-For", "198.51.100.50");
            Assert.AreEqual(HttpStatusCode.OK, (await host.Client.SendAsync(forwarded)).StatusCode, "the forwarded address was never counted");
        }

        /// <summary>
        /// 20 wrong passwords over unknown accounts (so no account locks) from the connection address, with the client IP when given
        /// </summary>
        private static async Task FailTwentyTimes(MarketApiHost host, string connection, string clientIp)
        {
            for (var i = 0; i < 20; i++)
            {
                var response = await host.SignInAsync(MarketApiTestData.UniqueName("ghost"), "wrong", connection, clientIp);
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
        }
    }
}
