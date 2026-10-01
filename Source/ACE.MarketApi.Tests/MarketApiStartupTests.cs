using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// The API as its own process, and its refusal to start without the market's tables
    /// </summary>
    [TestClass]
    public class MarketApiStartupTests
    {
        [TestMethod]
        public void Startup_MarketTablesMissing_RefusesToStart()
        {
            var ex = Assert.ThrowsExactly<MarketUnavailableException>(() => MarketApiHost.Build(shardDatabase: MarketApiTestData.BareShardDatabase));

            StringAssert.Contains(ex.Message, "missing: market_vault_item");
        }

        [TestMethod]
        public async Task Startup_MarketTablesPresent_StartsAndAnswers()
        {
            await using var host = await MarketApiHost.StartAsync();

            var response = await host.GetAsync("/api/me");

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [TestMethod]
        public void Startup_DatabaseLoginSetting_ReplacesTheConfigJsLogin()
        {
            // a container gets its login from docker.env, not from the Config.js it mounts: a wrong one must be the one used
            var ex = Assert.Throws<Exception>(() => MarketApiHost.Build(extraArgs: new[] { "--Market:DatabaseUsername=nosuchmarketuser", "--Market:DatabasePassword=wrong" }));

            StringAssert.Contains(ex.ToString(), "nosuchmarketuser");
        }

        [TestMethod]
        public async Task Startup_DatabaseLoginSetting_WithTheRightLogin_StartsAndAnswers()
        {
            var mysql = ACE.Common.ConfigManager.Config.MySql.Shard;

            await using var host = await MarketApiHost.StartAsync(extraArgs: new[] { $"--Market:DatabaseUsername={mysql.Username}", $"--Market:DatabasePassword={mysql.Password}" });

            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/facets")).StatusCode);
        }

        [TestMethod]
        public async Task Process_StartsAgainstTheDockerDatabase_AndSignsIn()
        {
            var name = MarketApiTestData.UniqueName("proc");
            MarketApiTestData.CreateAccount(name, "secret");

            var port = FreePort();
            using var process = StartApiProcess(MarketApiTestData.ShardDatabase, port, out var output);

            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

                var up = false;
                for (var i = 0; i < 120 && !up && !process.HasExited; i++)
                {
                    try
                    {
                        up = (await client.GetAsync("/api/me")).StatusCode == HttpStatusCode.Unauthorized;
                    }
                    catch (HttpRequestException)
                    {
                        await Task.Delay(500);
                    }
                }

                Assert.IsTrue(up, "the API process never answered: " + output);

                var login = await client.PostAsync("/api/auth/login", JsonContent.Create(new { account = name, password = "secret" }));
                Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());

                var me = new HttpRequestMessage(HttpMethod.Get, "/api/me");
                me.Headers.Add("Cookie", MarketApiHost.SessionCookie(login));
                var meResponse = await client.SendAsync(me);

                Assert.AreEqual(HttpStatusCode.OK, meResponse.StatusCode);
                StringAssert.Contains(await meResponse.Content.ReadAsStringAsync(), name);
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
        }

        [TestMethod]
        public void Process_MarketTablesMissing_ExitsWithAnError()
        {
            using var process = StartApiProcess(MarketApiTestData.BareShardDatabase, FreePort(), out var output);

            var exited = process.WaitForExit(TimeSpan.FromSeconds(60));

            if (!exited)
                process.Kill(true);

            Assert.IsTrue(exited, "the API kept running without market tables: " + output);
            process.WaitForExit();
            Assert.AreNotEqual(0, process.ExitCode);
            StringAssert.Contains(output.ToString(), "missing: market_vault_item");
        }

        /// <summary>
        /// Runs the built ACE.MarketApi.dll with dotnet, on ACE's Config.js and the given scratch shard
        /// </summary>
        private static Process StartApiProcess(string shardDatabase, int port, out StringBuilder output)
        {
            var dir = AppContext.BaseDirectory;
            var log = new StringBuilder();

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(Path.Combine(dir, "ACE.MarketApi.dll"));
            startInfo.ArgumentList.Add("--urls");
            startInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
            startInfo.ArgumentList.Add($"--Market:AceConfigPath={Path.Combine(dir, "Config.js")}");
            startInfo.ArgumentList.Add($"--Market:AuthDatabase={MarketApiTestData.AuthDatabase}");
            startInfo.ArgumentList.Add($"--Market:ShardDatabase={shardDatabase}");
            startInfo.ArgumentList.Add($"--Market:KeysPath={MarketApiHost.NewKeysPath()}");

            var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            output = log;
            return process;
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
