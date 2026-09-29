using System;
using System.IO;
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

namespace ACE.MarketApi.Tests.Support
{
    /// <summary>
    /// Seam 1: the Market API in-process on the ASP.NET Core test server, against the scratch auth and shard databases on the real MySQL.
    /// A request's client IP is set by the X-Test-Remote-Ip header, the clock by <see cref="Clock"/>.
    /// </summary>
    internal sealed class MarketApiHost : IAsyncDisposable
    {
        public const string DefaultIp = "10.1.1.1";

        public const string RemoteIpHeader = "X-Test-Remote-Ip";

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public ManualClock Clock { get; }

        public string KeysPath { get; }

        private MarketApiHost(WebApplication app, ManualClock clock, string keysPath)
        {
            App = app;
            Client = app.GetTestClient();
            Clock = clock;
            KeysPath = keysPath;
        }

        public static string NewKeysPath() => Path.Combine(Path.GetTempPath(), "ace-market-api-tests", Guid.NewGuid().ToString("N"));

        /// <summary>
        /// Builds the API as Program does, on the test server. Throws what MarketApi.Create throws.
        /// </summary>
        public static WebApplication Build(string shardDatabase = MarketApiTestData.ShardDatabase, string keysPath = null, ManualClock clock = null, params string[] extraArgs)
        {
            return MarketApi.Create(new[]
            {
                $"--Market:AuthDatabase={MarketApiTestData.AuthDatabase}",
                $"--Market:ShardDatabase={shardDatabase}",
                $"--Market:KeysPath={keysPath ?? NewKeysPath()}",
            }.Concat(extraArgs).ToArray(),
            builder =>
            {
                builder.WebHost.UseTestServer();

                if (clock != null)
                    builder.Services.AddSingleton<TimeProvider>(clock);

                builder.Services.AddSingleton<IStartupFilter, TestRemoteIpFilter>();
            });
        }

        public static async Task<MarketApiHost> StartAsync(string keysPath = null, ManualClock clock = null, params string[] extraArgs)
        {
            keysPath ??= NewKeysPath();
            clock ??= new ManualClock(DateTimeOffset.UtcNow);

            var app = Build(keysPath: keysPath, clock: clock, extraArgs: extraArgs);
            await app.StartAsync();

            return new MarketApiHost(app, clock, keysPath);
        }

        public async Task<HttpResponseMessage> SignInAsync(string account, string password, string ip = DefaultIp, string forwardedFor = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { account, password }),
            };
            request.Headers.Add(RemoteIpHeader, ip);

            if (forwardedFor != null)
                request.Headers.Add("X-Forwarded-For", forwardedFor);

            return await Client.SendAsync(request);
        }

        /// <summary>
        /// Signs in and returns the session cookie ("name=value"), failing the test if sign-in fails
        /// </summary>
        public async Task<string> SignInForCookieAsync(string account, string password)
        {
            var response = await SignInAsync(account, password);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            return SessionCookie(response);
        }

        public static string SessionCookie(HttpResponseMessage response)
        {
            Assert.IsTrue(response.Headers.TryGetValues("Set-Cookie", out var cookies), "no Set-Cookie header");

            return cookies.Select(c => c.Split(';')[0]).Single(c => c.StartsWith("market_session=", StringComparison.Ordinal));
        }

        public Task<HttpResponseMessage> GetAsync(string path, string cookie = null) => SendAsync(HttpMethod.Get, path, cookie);

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string cookie = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add(RemoteIpHeader, DefaultIp);

            if (cookie != null)
                request.Headers.Add("Cookie", cookie);

            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> PostJsonAsync(string path, object body, string cookie = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = body is string raw ? new StringContent(raw, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body),
            };
            request.Headers.Add(RemoteIpHeader, DefaultIp);

            if (cookie != null)
                request.Headers.Add("Cookie", cookie);

            return await Client.SendAsync(request);
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
