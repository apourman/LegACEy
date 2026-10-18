using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;

namespace ACE.Server.Tests
{
    [TestClass]
    public class ObjectGuidTests
    {
        [TestMethod]
        public void StaticBuiltForInstanceCarriesInstanceInFull()
        {
            var guid = new ObjectGuid(0x70001234, 2);

            Assert.AreEqual(0x0000000270001234UL, guid.Full);
            Assert.AreEqual(0x70001234u, guid.ClientGUID);
            Assert.AreEqual(2u, guid.Instance);
        }

        [TestMethod]
        public void PlayersAndDynamicsIgnoreInstance()
        {
            var player = new ObjectGuid(0x50000001, 2);
            var dynamic = new ObjectGuid(0x80000001, 2);

            Assert.AreEqual(0x50000001UL, player.Full);
            Assert.AreEqual(0x80000001UL, dynamic.Full);
            Assert.AreEqual(0u, player.Instance);
            Assert.AreEqual(0u, dynamic.Instance);
        }

        [TestMethod]
        public void GuidWithoutInstanceIsBaseWorldAndSameStaticInDifferentInstancesDiffers()
        {
            var withoutInstance = new ObjectGuid(0x70001234);
            var baseWorld = new ObjectGuid(0x70001234, 0);
            var instanced = new ObjectGuid(0x70001234, 1);

            Assert.AreEqual(0x70001234UL, withoutInstance.Full);
            Assert.AreEqual(0x70001234u, withoutInstance.ClientGUID);
            Assert.AreEqual(withoutInstance, baseWorld);
            Assert.AreEqual(withoutInstance.GetHashCode(), baseWorld.GetHashCode());
            Assert.AreNotEqual(withoutInstance, instanced);
        }
    }
}
