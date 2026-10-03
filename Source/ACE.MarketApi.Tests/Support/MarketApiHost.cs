using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ACE.MarketApi.Tests.Support
{
    /// <summary>
    /// Seam 1: the Market API in-process on the ASP.NET Core test server, against the scratch auth and shard databases on the real MySQL.
    /// A request's connection address is set by the X-Test-Remote-Ip header, the clock by <see cref="Clock"/>.
    /// <see cref="Client"/> sends the service key on every request, as the BFF does; <see cref="ClientWithoutKey"/> sends none.
    /// Requests are signed in as the BFF signs them: "Authorization: Bearer" with a web session token (or a plugin token). No request carries
    /// a cookie or X-Market-Request: the API needs neither.
    /// </summary>
    internal sealed class MarketApiHost : IAsyncDisposable
    {
        public const string DefaultIp = "10.1.1.1";

        public const string RemoteIpHeader = "X-Test-Remote-Ip";

        /// <summary>
        /// The service key every test host is configured with (Market:ServiceKey)
        /// </summary>
        public const string ServiceKey = "3f9c1d7e5a2b8c4d6e0f1a3b5c7d9e1f2a4b6c8d0e2f4a6b8c0d2e4f6a8b0c2d";

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public ManualClock Clock { get; }

        private MarketApiHost(WebApplication app, ManualClock clock)
        {
            App = app;
            Client = app.GetTestClient();
            Client.DefaultRequestHeaders.Add(ServiceGate.KeyHeader, ServiceKey);
            Clock = clock;
        }

        /// <summary>
        /// A client that sends no service key (dispose it)
        /// </summary>
        public HttpClient ClientWithoutKey() => App.GetTestClient();

        /// <summary>
        /// The configuration argument that gives a host built by hand the test service key
        /// </summary>
        public static string ServiceKeyArgument => $"--Market:ServiceKey={ServiceKey}";

        /// <summary>
        /// Builds the API as Program does, on the test server. Throws what MarketApi.Create throws.
        /// </summary>
        public static WebApplication Build(string shardDatabase = MarketApiTestData.ShardDatabase, ManualClock clock = null, params string[] extraArgs) =>
            Build(shardDatabase, clock, null, extraArgs);

        /// <param name="services">registers test replacements for the API's services (the fee policy, the pause), which the API only adds when missing</param>
        private static WebApplication Build(string shardDatabase, ManualClock clock, Action<IServiceCollection> services, string[] extraArgs)
        {
            return MarketApi.Create(new[]
            {
                $"--Market:AuthDatabase={MarketApiTestData.AuthDatabase}",
                $"--Market:ShardDatabase={shardDatabase}",
                ServiceKeyArgument,
            }.Concat(extraArgs).ToArray(),
            builder =>
            {
                builder.WebHost.UseTestServer();

                if (clock != null)
                    builder.Services.AddSingleton<TimeProvider>(clock);

                builder.Services.AddSingleton<IStartupFilter, TestRemoteIpFilter>();

                services?.Invoke(builder.Services);

                // the shared test shard is seeded with balances that have no ledger entries, which the audit would rightly fail and pause the market for:
                // only tests that ask for a schedule (on their own shard) audit
                builder.Services.TryAddSingleton(LedgerAuditSchedule.Off);
            });
        }

        public static Task<MarketApiHost> StartAsync(ManualClock clock = null, params string[] extraArgs) => StartAsync(null, clock, extraArgs);

        /// <summary>
        /// Starts the API with test services in place of its own
        /// </summary>
        public static Task<MarketApiHost> StartAsync(Action<IServiceCollection> services, ManualClock clock = null, params string[] extraArgs) =>
            StartOnAsync(MarketApiTestData.ShardDatabase, services, clock, extraArgs);

        /// <summary>
        /// Starts the API on another scratch shard, with test services in place of its own
        /// </summary>
        public static async Task<MarketApiHost> StartOnAsync(string shardDatabase, Action<IServiceCollection> services = null, ManualClock clock = null, params string[] extraArgs)
        {
            clock ??= new ManualClock(DateTimeOffset.UtcNow);

            var app = Build(shardDatabase, clock, services, extraArgs);
            await app.StartAsync();

            return new MarketApiHost(app, clock);
        }

        /// <summary>
        /// The BFF's session sign-in (POST /api/auth/session) from the connection address ip, carrying X-Market-Client-Ip when clientIp is given
        /// and X-Forwarded-For when forwardedFor is given
        /// </summary>
        public async Task<HttpResponseMessage> SignInAsync(string account, string password, string ip = DefaultIp, string clientIp = null, string forwardedFor = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/session")
            {
                Content = JsonContent.Create(new { account, password }),
            };
            request.Headers.Add(RemoteIpHeader, ip);

            if (clientIp != null)
                request.Headers.Add(ServiceGate.ClientIpHeader, clientIp);

            if (forwardedFor != null)
                request.Headers.Add("X-Forwarded-For", forwardedFor);

            return await Client.SendAsync(request);
        }

        /// <summary>
        /// Starts a web session and returns its bearer token, failing the test if sign-in fails
        /// </summary>
        public async Task<string> SignInForSessionAsync(string account, string password)
        {
            var response = await SignInAsync(account, password);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            return SessionToken(await JsonAsync(response));
        }

        /// <summary>
        /// The web session token of a successful sign-in's answer
        /// </summary>
        public static string SessionToken(JsonElement signIn) => signIn.GetProperty("token").GetString();

        /// <param name="token">a web session or plugin token, sent as "Authorization: Bearer"</param>
        public Task<HttpResponseMessage> GetAsync(string path, string token = null) => SendAsync(HttpMethod.Get, path, token);

        /// <summary>
        /// A GET signed in with a bearer token (a plugin token or a web session)
        /// </summary>
        public Task<HttpResponseMessage> GetWithTokenAsync(string path, string token) => SendAsync(HttpMethod.Get, path, token);

        /// <param name="token">a web session or plugin token, sent as "Authorization: Bearer"</param>
        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add(RemoteIpHeader, DefaultIp);
            AddToken(request, token);

            return await Client.SendAsync(request);
        }

        /// <param name="token">a web session or plugin token, sent as "Authorization: Bearer"</param>
        public async Task<HttpResponseMessage> PostJsonAsync(string path, object body, string token = null, string ip = DefaultIp)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = body is string raw ? new StringContent(raw, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body),
            };
            request.Headers.Add(RemoteIpHeader, ip);
            AddToken(request, token);

            return await Client.SendAsync(request);
        }

        private static void AddToken(HttpRequestMessage request, string token)
        {
            if (token != null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync();

            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public static async Task<string> ErrorAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("error").GetString();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }

        private sealed class TestRemoteIpFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            {
                return app =>
                {
                    app.Use(async (context, nextMiddleware) =>
                    {
                        if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var ip))
                            context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());

                        await nextMiddleware(context);
                    });

                    next(app);
                };
            }
        }
    }

    /// <summary>
    /// A clock the tests move by hand
    /// </summary>
    internal sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now;

        public ManualClock(DateTimeOffset start)
        {
            now = start;
        }

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
