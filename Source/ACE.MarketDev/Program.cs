using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using ACE.Common;
using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;

namespace ACE.MarketDev
{
    /// <summary>
    /// Development-only tools for the local market stack (scripts/market). Every command that writes runs behind the development guard,
    /// which checks the auth and shard databases named by the ACE configuration against its allow-lists and the development marker.
    ///
    ///   check   runs the guard and reports the first failed check (writes nothing)
    ///   mark    marks the configured auth and shard databases as development databases, after a typed confirmation
    ///   seed    fills the databases with accounts, characters, Vault items, balances and listings
    ///   fresh   drops, recreates and seeds only the fixed end-to-end database pair
    ///   fixture creates ticket-status examples (--character), or moves one ticket forward (--ticket, --to, --code, --seconds),
    ///           after proving the game server is stopped
    ///
    /// Options: --config &lt;Config.js&gt; (the ACE configuration naming the databases, default ./Config.js), --password &lt;password&gt; (seed accounts).
    /// Settings: MARKET_DEV_ALLOWED_ENDPOINTS, MARKET_DEV_ALLOWED_AUTH_DATABASES and MARKET_DEV_ALLOWED_SHARD_DATABASES (comma-separated)
    /// replace the guard's allow-lists.
    /// </summary>
    public static class Program
    {
        public const string DefaultSeedPassword = "marketdev";

        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
                return Usage(0);

            var command = args[0];
            var options = ParseOptions(args.Skip(1).ToArray());

            if (options == null)
                return Usage(2);

            ConfigManager.Initialize(options.GetValueOrDefault("config", "Config.js"));

            var targets = Targets();
            var settings = GuardSettings();

            switch (command)
            {
                case "check":
                    return Report(DevelopmentGuard.Check(targets, settings));

                case "audit":
                    var auditExitCode = 1;
                    var auditGuard = DevelopmentGuard.Run(targets, settings, () => auditExitCode = AuditLedger());
                    return auditGuard.Passed ? auditExitCode : Report(auditGuard);

                case "mark":
                    return Mark(targets, settings);

                case "seed":
                    var password = SeedPassword(options);
                    var exitCode = 1;
                    var result = DevelopmentGuard.Run(targets, settings, () =>
                    {
                        try
                        {
                            exitCode = Seeder.Seed(password, DevelopmentGuard.IsEndToEndPair(targets));
                        }
                        catch (Exception)
                        {
                            Console.Error.WriteLine("Seeding stopped part way; the databases are partly seeded. Start over: drop the market databases and run scripts/market/bootstrap.sh.");
                            throw;
                        }
                    });

                    return result.Passed ? exitCode : Report(result);

                case "fresh":
                    var freshExitCode = 1;
                    var freshResult = DevelopmentGuard.RunFresh(targets, () =>
                    {
                        try
                        {
                            ResetFreshDatabases();
                            freshExitCode = Seeder.Seed(SeedPassword(options), endToEnd: true);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Fresh setup stopped part way: {ex.Message}");
                            Console.Error.WriteLine("The end-to-end databases may be empty or partly initialized; retry scripts/market/fresh.sh.");
                            freshExitCode = 1;
                        }
                    });

                    return freshResult.Passed ? freshExitCode : Report(freshResult);

                case "fixture":
                    Func<bool> fixture;
                    if (options.TryGetValue("ticket", out var ticketText))
                    {
                        if (!long.TryParse(ticketText, out var ticketId) || !options.TryGetValue("to", out var to))
                            return Usage(2);

                        int? seconds = null;
                        if (options.TryGetValue("seconds", out var secondsText))
                        {
                            if (!int.TryParse(secondsText, out var parsed) || parsed < 0)
                                return Usage(2);
                            seconds = parsed;
                        }

                        fixture = () => TicketFixture.Move(ticketId, to, options.GetValueOrDefault("code"), seconds);
                    }
                    else
                    {
                        var character = options.GetValueOrDefault("character");
                        if (string.IsNullOrWhiteSpace(character))
                            return Usage(2);

                        fixture = () => TicketFixture.CreateExamples(character);
                    }

                    var fixtureExitCode = 1;
                    var fixtureGuard = DevelopmentGuard.Run(targets, settings, () => fixtureExitCode = fixture() ? 0 : 4);
                    return fixtureGuard.Passed ? fixtureExitCode : Report(fixtureGuard);

                default:
                    return Usage(2);
            }
        }

        /// <summary>
        /// The databases a command would write, as the ACE configuration names them
        /// </summary>
        private static IReadOnlyList<DevelopmentTarget> Targets() => new[]
        {
            new DevelopmentTarget(DevelopmentTarget.AuthRole, ConfigManager.Config.MySql.Authentication),
            new DevelopmentTarget(DevelopmentTarget.ShardRole, ConfigManager.Config.MySql.Shard),
        };

        private static DevelopmentGuardSettings GuardSettings()
        {
            var settings = new DevelopmentGuardSettings();

            var endpoints = List(Environment.GetEnvironmentVariable("MARKET_DEV_ALLOWED_ENDPOINTS"));
            if (endpoints != null)
                settings.AllowedEndpoints = endpoints;

            var authDatabases = List(Environment.GetEnvironmentVariable("MARKET_DEV_ALLOWED_AUTH_DATABASES"));
            if (authDatabases != null)
                settings.AllowedAuthDatabases = authDatabases;

            var shardDatabases = List(Environment.GetEnvironmentVariable("MARKET_DEV_ALLOWED_SHARD_DATABASES"));
            if (shardDatabases != null)
                settings.AllowedShardDatabases = shardDatabases;

            return settings;
        }

        private static string[] List(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string SeedPassword(Dictionary<string, string> options) =>
            options.GetValueOrDefault("password", Environment.GetEnvironmentVariable("MARKET_SEED_PASSWORD") ?? DefaultSeedPassword);

        private static int Report(DevelopmentGuardResult result)
        {
            if (result.Passed)
            {
                Console.WriteLine("Development guard: every check passed.");
                return 0;
            }

            Console.Error.WriteLine($"Development guard refused ({result.FailedCheck}): {result.Detail}. Nothing was written.");
            return 3;
        }

        /// <summary>
        /// The explicit step that marks existing databases. Only targets that pass the endpoint and name checks can be marked, and only after
        /// the developer types the confirmation at a terminal.
        /// </summary>
        private static int Mark(IReadOnlyList<DevelopmentTarget> targets, DevelopmentGuardSettings settings)
        {
            var result = DevelopmentGuard.Check(targets, settings);

            if (result.Passed)
            {
                Console.WriteLine("Both databases are already marked as development databases.");
                return 0;
            }

            // the guard checks every endpoint, then every name, then the markers: a marker failure means the endpoints and names are allowed
            if (result.Check != DevelopmentCheck.Marker)
                return Report(result);

            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("mark is interactive: run it at a terminal.");
                return 2;
            }

            Console.WriteLine("This marks these databases as local development databases, which the seed tool and the ticket fixture may then overwrite:");
            foreach (var target in targets)
                Console.WriteLine($"  {target.Role}: '{target.Connection.Database}' at {target.Endpoint}");
            Console.Write("Never do this on a server. Type MARK to continue: ");

            if (Console.ReadLine()?.Trim() != "MARK")
            {
                Console.Error.WriteLine("Not marked.");
                return 1;
            }

            foreach (var target in targets)
                DevelopmentGuard.Mark(target.Connection);

            Console.WriteLine("Marked.");
            return 0;
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var options = new Dictionary<string, string>();

            for (var i = 0; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                    return null;

                options[args[i].Substring(2)] = args[i + 1];
            }

            return options;
        }

        private static int Usage(int exitCode)
        {
            Console.Error.WriteLine("Usage: ACE.MarketDev <check|mark|seed|fresh|audit|fixture> [--config <Config.js>] [--password <seed account password>] [--character <name>] [--ticket <id> --to <stage> [--code <reason>] [--seconds <countdown>]]");
            return exitCode;
        }

        private static int AuditLedger()
        {
            using var shard = new ShardDbContext();
            var audit = LedgerAudit.Run(shard);
            Console.WriteLine(audit.Summary);
            foreach (var failure in audit.Failures)
                Console.WriteLine($"  {failure.Check}: {failure.Detail}");
            return audit.Passed ? 0 : 1;
        }

        /// <summary>
        /// Drops and recreates the fixed e2e pair after RunFresh has checked both configured names and endpoints.
        /// The SQL script contains only those fixed names and talks to the already-running local MySQL container.
        /// </summary>
        private static void ResetFreshDatabases()
        {
            var root = Environment.GetEnvironmentVariable("MARKET_DEV_ROOT");
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("MARKET_DEV_ROOT is required; use scripts/market/fresh.sh.");

            var script = Path.Combine(root, "scripts", "market", "fresh-databases.sh");
            var start = new ProcessStartInfo("bash") { UseShellExecute = false };
            start.ArgumentList.Add(script);
            start.Environment["MARKET_DEV_ROOT"] = root;

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the end-to-end database reset script.");
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The end-to-end database reset script exited with status {process.ExitCode}.");
        }
    }
}
