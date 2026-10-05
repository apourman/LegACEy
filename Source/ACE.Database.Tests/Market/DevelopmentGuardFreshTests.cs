using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database.Market;

namespace ACE.Database.Tests.Market
{
    [TestClass]
    public class DevelopmentGuardFreshTests
    {
        private static IReadOnlyList<DevelopmentTarget> Targets(string auth = "ace_market_e2e_auth", string shard = "ace_market_e2e_shard", string host = "127.0.0.1", uint port = 3310) => new[]
        {
            Target(DevelopmentTarget.AuthRole, auth, host, port),
            Target(DevelopmentTarget.ShardRole, shard, host, port),
        };

        private static DevelopmentTarget Target(string role, string database, string host, uint port) => new DevelopmentTarget(role, new MySqlConfiguration
        {
            Host = host,
            Port = port,
            Database = database,
            Username = "unused",
            Password = "unused",
        });

        [TestMethod]
        public void Fresh_RefusesDevelopmentDatabaseNamesWithoutCallingWriter()
        {
            var writes = 0;

            var result = DevelopmentGuard.RunFresh(Targets("ace_market_auth", "ace_market_shard"), () => writes++);

            Assert.IsFalse(result.Passed);
            Assert.AreEqual(DevelopmentCheck.DatabaseName, result.Check);
            Assert.AreEqual(0, writes);
        }

        [TestMethod]
        public void Fresh_RefusesUnknownDatabaseNamesWithoutCallingWriter()
        {
            var writes = 0;

            var result = DevelopmentGuard.RunFresh(Targets("ace_market_e2e_auth", "unknown_market_shard"), () => writes++);

            Assert.IsFalse(result.Passed);
            Assert.AreEqual(DevelopmentCheck.DatabaseName, result.Check);
            Assert.AreEqual(0, writes);
        }

        [TestMethod]
        public void Fresh_RefusesDisallowedEndpointWithoutCallingWriter()
        {
            var writes = 0;

            var result = DevelopmentGuard.RunFresh(Targets(host: "192.0.2.10"), () => writes++);

            Assert.IsFalse(result.Passed);
            Assert.AreEqual(DevelopmentCheck.Endpoint, result.Check);
            Assert.AreEqual(0, writes);
        }

        [TestMethod]
        public void Fresh_AllowsOnlyTheEndToEndPairOnTheLocalMysqlEndpoint()
        {
            var writes = 0;

            var result = DevelopmentGuard.RunFresh(Targets(), () => writes++);

            Assert.IsTrue(result.Passed, result.Detail);
            Assert.AreEqual(1, writes);
        }
    }
}
