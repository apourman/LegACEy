using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClientChannel;
using ACE.Server.Tests.Market;

namespace ACE.Server.Tests.ClientChannel
{
    [TestClass]
    public class PaperdollInspectTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            VaultTestWorld.Start();
            PaperdollChannelActions.Register();
        }

        [ClassCleanup]
        public static void TestCleanup() => VaultTestWorld.Stop();

        [TestMethod]
        public void Inspect_Self_SendsTheSameLookAsLook()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            using (VaultTestWorld.Online(player))
            {
                var look = ChannelTestClient.Request(player, PaperdollChannelActions.Look, Array.Empty<byte>());
                var inspect = ChannelTestClient.Request(player, PaperdollChannelActions.Inspect, BitConverter.GetBytes((uint)player.Guid.Full));

                Assert.AreEqual(ChannelStatus.Ok, inspect.Status, ChannelTestClient.Text(inspect.Body));
                CollectionAssert.AreEqual(look.Body, inspect.Body);
            }
        }

        [TestMethod]
        public void Inspect_UnknownObject_Fails()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            using (VaultTestWorld.Online(player))
            {
                var reply = ChannelTestClient.Request(player, PaperdollChannelActions.Inspect, BitConverter.GetBytes(0x7FFFFFF0u));

                Assert.AreEqual(ChannelStatus.BadRequest, reply.Status);
            }
        }
    }
}
