using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Linq;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    [TestClass]
    public class LandblockNamesTests
    {
        [TestMethod]
        public void SharedLandblock_ReturnsEveryName_AndUnlistedLandblock_ReturnsNone()
        {
            CollectionAssert.AreEqual(new[] { "Black Totem Temple" }, LandblockNames.Get(0x0099).ToArray());
            CollectionAssert.AreEqual(new[] { "Chamber of Scrolls", "Chamber of Air", "Chamber of Earth", "Chamber of Water" }, LandblockNames.Get(0x00C9).ToArray());
            CollectionAssert.AreEqual(new string[0], LandblockNames.Get(0x0000).ToArray());
        }
    }
}
