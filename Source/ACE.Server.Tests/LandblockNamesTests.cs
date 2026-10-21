using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Linq;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    [TestClass]
    public class LandblockNamesTests
    {
        [TestMethod]
        public void Lines_SplitOnCRLF_AsWellAsLF()
        {
            var parsed = LandblockNames.Parse("0099\tBlack Totem Temple\r\n00C9\tChamber of Scrolls|Chamber of Air\r\n");
            CollectionAssert.AreEqual(new[] { "Black Totem Temple" }, parsed[0x0099]);
            CollectionAssert.AreEqual(new[] { "Chamber of Scrolls", "Chamber of Air" }, parsed[0x00C9]);
        }

        [TestMethod]
        public void SharedLandblock_ReturnsEveryName_AndUnlistedLandblock_ReturnsNone()
        {
            CollectionAssert.AreEqual(new[] { "Black Totem Temple" }, LandblockNames.Get(0x0099).ToArray());
            CollectionAssert.AreEqual(new[] { "Chamber of Scrolls", "Chamber of Air", "Chamber of Earth", "Chamber of Water" }, LandblockNames.Get(0x00C9).ToArray());
            CollectionAssert.AreEqual(new string[0], LandblockNames.Get(0x0000).ToArray());
        }
    }
}
