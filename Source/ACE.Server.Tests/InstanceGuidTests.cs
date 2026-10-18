using System.IO;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The client sees only 32-bit client guids, and the server adds the instance back from where the sender is
    /// </summary>
    [TestClass]
    public class InstanceGuidTests
    {
        private static readonly ObjectGuid InstancedStatic = new ObjectGuid(0x77D64F00, 3);

        [TestMethod]
        public void WriteGuid_WritesOnlyTheClientGuid()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream))
                writer.WriteGuid(InstancedStatic);

            CollectionAssert.AreEqual(new byte[] { 0x00, 0x4F, 0xD6, 0x77 }, stream.ToArray());
        }

        [TestMethod]
        public void ClientGuid_ResolvesInTheSendersLandblockCopy()
        {
            var landblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));
            landblock.Instance = 3;
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            player.CurrentLandblock = landblock;

            Assert.AreEqual(InstancedStatic, player.ResolveGuid(InstancedStatic.ClientGUID));
            Assert.AreEqual(0x80000001UL, player.ResolveGuid(0x80000001).Full, "a dynamic object has no instance");

            player.CurrentLandblock = null;
            Assert.AreEqual(new ObjectGuid(0x77D64F00), player.ResolveGuid(0x77D64F00), "with no landblock, the base world");
        }
    }
}
