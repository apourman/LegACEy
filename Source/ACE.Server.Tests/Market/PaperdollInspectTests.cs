using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClientChannel;
using ACE.Server.Market;
using ACE.Server.Tests.ClientChannel;

namespace ACE.Server.Tests.Market
{
    // In the Vault tests: the started world is shared, and starts once a run.
    public partial class VaultTests
    {
        [TestMethod]
        public void Inspect_Self_SendsTheSameLookAsLook()
        {
            PaperdollChannelActions.Register();
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
        public void Parts_NameTheWornItemThatDrawsEachBodyPart()
        {
            PaperdollChannelActions.Register();
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var helm = VaultTestWorld.NewItem(VaultTestWorld.HelmWcid);
            Assert.IsTrue(player.TryEquipObject(helm, EquipMask.HeadWear));
            player.SetCharacterOption(CharacterOption.ShowYourHelmOrHeadGear, true); // a hidden helm draws nothing
            using (VaultTestWorld.Online(player))
            {
                var reply = ChannelTestClient.Request(player, PaperdollChannelActions.Parts, Array.Empty<byte>());

                Assert.AreEqual(ChannelStatus.Ok, reply.Status);
                var parts = new System.Collections.Generic.Dictionary<byte, uint>();
                using (var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(reply.Body)))
                    for (int i = 0, count = reader.ReadUInt16(); i < count; i++)
                        parts[reader.ReadByte()] = reader.ReadUInt32();
                // The head is part 16 of the human setups.
                Assert.AreEqual(helm.Guid.ClientGUID, parts[16]);
            }
        }

        [TestMethod]
        public void Inspect_UnknownObject_Fails()
        {
            PaperdollChannelActions.Register();
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
