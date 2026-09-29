global using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.MarketApi.Tests.Support;

[assembly: DoNotParallelize]

namespace ACE.MarketApi.Tests
{
    [TestClass]
    public static class TestAssembly
    {
        [AssemblyInitialize]
        public static void Initialize(TestContext context)
        {
            MarketApiTestData.CreateDatabases();
        }

        [AssemblyCleanup]
        public static void Cleanup()
        {
            MarketApiTestData.DropDatabases();
        }
    }
}
