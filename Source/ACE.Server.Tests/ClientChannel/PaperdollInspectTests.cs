using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClientChannel;
using ACE.Server.Market;
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

        [TestMethod]
        public void Inspect_VaultItem_SendsItsLook()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var guid = item.Guid.ClientGUID;
            using (VaultTestWorld.Online(player))
            {
                var held = ChannelTestClient.Request(player, PaperdollChannelActions.Inspect, BitConverter.GetBytes(guid));
                Assert.AreEqual(ChannelStatus.Ok, held.Status);
                Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(player, guid).Outcome);

                var stored = ChannelTestClient.Request(player, PaperdollChannelActions.Inspect, BitConverter.GetBytes(guid));

                Assert.AreEqual(ChannelStatus.Ok, stored.Status, stored.Status == ChannelStatus.Ok ? "" : ChannelTestClient.Text(stored.Body));
                CollectionAssert.AreEqual(held.Body, stored.Body);
            }
        }
    }
}
