using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database.Market;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// The development and end-to-end databases are each allowed, but only as their own pair. These refusals happen on the
    /// configuration alone, before the marker check opens a connection, so they need no database.
    /// </summary>
    [TestClass]
    public class DevelopmentGuardPairTests
    {
        private static IReadOnlyList<DevelopmentTarget> Targets(string auth, string shard) => new[]
        {
            new DevelopmentTarget(DevelopmentTarget.AuthRole, Connection(auth)),
            new DevelopmentTarget(DevelopmentTarget.ShardRole, Connection(shard)),
        };

        private static MySqlConfiguration Connection(string database) => new MySqlConfiguration
        {
            Host = "127.0.0.1",
            Port = 3310,
            Database = database,
            Username = "unused",
            Password = "unused",
        };

        [TestMethod]
        [DataRow("ace_market_auth", "ace_market_e2e_shard")]
        [DataRow("ace_market_e2e_auth", "ace_market_shard")]
        public void MixedDevelopmentAndEndToEndNames_AreRefusedWithoutCallingWriter(string auth, string shard)
        {
            var writes = 0;

            var result = DevelopmentGuard.Run(Targets(auth, shard), new DevelopmentGuardSettings(), () => writes++);

            Assert.IsFalse(result.Passed);
            Assert.AreEqual(DevelopmentCheck.DatabaseName, result.Check);
            Assert.AreEqual("database pair", result.FailedCheck, result.Detail);
            Assert.AreEqual(0, writes);
        }

        [TestMethod]
        public void IsEndToEndPair_IsTrueOnlyForBothEndToEndNames()
        {
            Assert.IsTrue(DevelopmentGuard.IsEndToEndPair(Targets("ace_market_e2e_auth", "ace_market_e2e_shard")));
            Assert.IsFalse(DevelopmentGuard.IsEndToEndPair(Targets("ace_market_auth", "ace_market_shard")));
            Assert.IsFalse(DevelopmentGuard.IsEndToEndPair(Targets("ace_market_auth", "ace_market_e2e_shard")));
            Assert.IsFalse(DevelopmentGuard.IsEndToEndPair(Targets("ace_market_e2e_auth", "ace_market_shard")));
        }
    }
}
