using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database.Market;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Seam 3: the development guard the seed tool (and later the ticket fixture) runs before writing anything.
    /// Every refusal names the failed check and leaves every target exactly as it was.
    /// </summary>
    [TestClass]
    public class DevelopmentGuardTests
    {
        private const string Auth = "ace_auth_market_guard";
        private const string Shard = "ace_shard_market_guard";

        /// <summary>
        /// Marked, on the allowed endpoint, but not a name on the allow-list
        /// </summary>
        private const string AlternateShard = "ace_shard_market_guard_alt";

        /// <summary>
        /// On the allow-list in the marker test, but never marked
        /// </summary>
        private const string UnmarkedShard = "ace_shard_market_guard_bare";

        [ClassInitialize]
        public static void CreateDatabases(TestContext context)
        {
            MarketTestDatabase.InitializeConfig();

            MarketTestDatabase.CreateAuth(Auth);
            MarketTestDatabase.CreateFromBase(Shard);
            MarketTestDatabase.CreateFromBase(AlternateShard);
            MarketTestDatabase.CreateFromBase(UnmarkedShard);

            DevelopmentGuard.Mark(Target(Auth).Connection);
            DevelopmentGuard.Mark(Target(Shard).Connection);
            DevelopmentGuard.Mark(Target(AlternateShard).Connection);
        }

        [ClassCleanup]
        public static void DropDatabases()
        {
            MarketTestDatabase.Drop(Auth);
            MarketTestDatabase.Drop(Shard);
            MarketTestDatabase.Drop(AlternateShard);
            MarketTestDatabase.Drop(UnmarkedShard);
        }

        private static string AllowedEndpoint => $"{ConfigManager.Config.MySql.Shard.Host}:{ConfigManager.Config.MySql.Shard.Port}";

        private static DevelopmentTarget Target(string database, string host = null, uint? port = null)
        {
            var config = ConfigManager.Config.MySql.Shard;
            var role = database.StartsWith("ace_auth", StringComparison.Ordinal) ? DevelopmentTarget.AuthRole : DevelopmentTarget.ShardRole;

            return new DevelopmentTarget(role, new MySqlConfiguration
            {
                Host = host ?? config.Host,
                Port = port ?? config.Port,
                Database = database,
                Username = config.Username,
                Password = config.Password,
            });
        }

        private static DevelopmentGuardSettings Settings(params string[] databases) => new DevelopmentGuardSettings
        {
            AllowedEndpoints = new[] { AllowedEndpoint },
            AllowedDatabases = databases.Length > 0 ? databases : new[] { Auth, Shard },
        };

        /// <summary>
        /// Every table of the database with its exact checksum, so any write shows up
        /// </summary>
        private static List<string> Fingerprint(string database)
        {
            var tables = MarketTestDatabase.Rows(database, $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{database}' ORDER BY table_name;");

            return MarketTestDatabase.Rows(database, "CHECKSUM TABLE " + string.Join(", ", tables.Select(t => $"`{t}`")) + " EXTENDED;");
        }

        private static readonly string[] Everything = { Auth, Shard, AlternateShard, UnmarkedShard };

        private static Dictionary<string, List<string>> FingerprintAll() => Everything.ToDictionary(d => d, Fingerprint);

        private static void AssertUnchanged(Dictionary<string, List<string>> before)
        {
            foreach (var (database, fingerprint) in before)
                CollectionAssert.AreEqual(fingerprint, Fingerprint(database), $"{database} was written");
        }

        /// <summary>
        /// What the seed tool would do: write to both targets
        /// </summary>
        private static Action WriteBoth(DevelopmentTarget auth, DevelopmentTarget shard, List<string> wrote) => () =>
        {
            MarketTestDatabase.Execute(auth.Connection.Database, "INSERT INTO account (accountName, passwordHash, passwordSalt, accessLevel) VALUES ('guardprobe', 'x', 'x', 0);");
            MarketTestDatabase.Execute(shard.Connection.Database, "INSERT INTO `character` (id, account_Id, name, is_Plussed, is_Deleted) VALUES (1342177999, 1, 'Guardprobe', 0, 0);");
            wrote.Add("both");
        };

        // ---- refusals

        [TestMethod]
        public void AlternateShardName_OnTheAllowedEndpoint_IsRefused_AndNothingIsWritten()
        {
            var auth = Target(Auth);
            var shard = Target(AlternateShard);
            var before = FingerprintAll();
            var wrote = new List<string>();

            var result = DevelopmentGuard.Run(new[] { auth, shard }, Settings(), WriteBoth(auth, shard, wrote));

            Assert.IsFalse(result.Passed);
            Assert.AreEqual("shard database name", result.FailedCheck, result.Detail);
            StringAssert.Contains(result.Detail, AlternateShard);
            Assert.AreEqual(0, wrote.Count);
            AssertUnchanged(before);
        }

        [TestMethod]
        public void DisallowedAuthTarget_WithAnAllowedShard_IsRefused_AndNothingIsWritten()
        {
            var shard = Target(Shard);
            var before = FingerprintAll();
            var wrote = new List<string>();

            // another server (TEST-NET-1, never connected to: endpoints are checked before any connection)
            var remoteAuth = Target(Auth, host: "192.0.2.10");
            var remote = DevelopmentGuard.Run(new[] { remoteAuth, shard }, Settings(), WriteBoth(remoteAuth, shard, wrote));

            Assert.IsFalse(remote.Passed);
            Assert.AreEqual("auth endpoint", remote.FailedCheck, remote.Detail);
            StringAssert.Contains(remote.Detail, "192.0.2.10");

            // the right server, but the shared auth database rather than the market's own
            var otherAuth = Target("ace_auth");
            var other = DevelopmentGuard.Run(new[] { otherAuth, shard }, Settings(), WriteBoth(otherAuth, shard, wrote));

            Assert.IsFalse(other.Passed);
            Assert.AreEqual("auth database name", other.FailedCheck, other.Detail);

            Assert.AreEqual(0, wrote.Count);
            AssertUnchanged(before);
        }

        [TestMethod]
        public void DatabaseWithoutTheMarker_IsRefused_AndNothingIsWritten()
        {
            var auth = Target(Auth);
            var shard = Target(UnmarkedShard);
            var before = FingerprintAll();
            var wrote = new List<string>();

            var result = DevelopmentGuard.Run(new[] { auth, shard }, Settings(Auth, UnmarkedShard), WriteBoth(auth, shard, wrote));

            Assert.IsFalse(result.Passed);
            Assert.AreEqual("shard marker", result.FailedCheck, result.Detail);
            StringAssert.Contains(result.Detail, UnmarkedShard);
            Assert.AreEqual(0, wrote.Count);
            AssertUnchanged(before);
        }

        [TestMethod]
        public void MissingTarget_IsRefused()
        {
            var result = DevelopmentGuard.Run(new[] { Target(Shard) }, Settings(), () => Assert.Fail("wrote without an auth target"));

            Assert.IsFalse(result.Passed);
            Assert.AreEqual("auth target", result.FailedCheck, result.Detail);
        }

        // ---- passing

        [TestMethod]
        public void EveryCheckPasses_TheWriteRuns()
        {
            var auth = Target(Auth);
            var shard = Target(Shard);
            var wrote = new List<string>();

            var result = DevelopmentGuard.Run(new[] { auth, shard }, Settings(), WriteBoth(auth, shard, wrote));

            Assert.IsTrue(result.Passed, result.Detail);
            Assert.AreEqual(1, wrote.Count);
            Assert.AreEqual(1L, MarketTestDatabase.Scalar(Auth, "SELECT COUNT(*) FROM account WHERE accountName = 'guardprobe';"));

            MarketTestDatabase.Execute(Auth, "DELETE FROM account WHERE accountName = 'guardprobe';");
            MarketTestDatabase.Execute(Shard, "DELETE FROM `character` WHERE id = 1342177999;");
        }

        [TestMethod]
        public void Mark_Again_KeepsOneMarkerRow()
        {
            DevelopmentGuard.Mark(Target(Shard).Connection);

            Assert.AreEqual(1L, MarketTestDatabase.Scalar(Shard, $"SELECT COUNT(*) FROM `{DevelopmentGuard.MarkerTable}`;"));
            Assert.IsTrue(DevelopmentGuard.Check(new[] { Target(Auth), Target(Shard) }, Settings()).Passed);
        }
    }
}
