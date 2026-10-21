using System.IO;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClientChannel;
using ACE.Server.Realms;
using ACE.Server.Tests.Market;

namespace ACE.Server.Tests.ClientChannel
{
    [TestClass]
    public class PlaceChannelActionsTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context) => VaultTestWorld.Start();

        [ClassCleanup]
        public static void TestCleanup() => VaultTestWorld.Stop();

        [TestMethod]
        public void The_base_world_names_a_landblock_from_the_shared_list_when_it_has_one_name()
        {
            Assert.AreEqual("Black Totem Temple", PlaceChannelActions.NameFor(0x0099, RealmDefinition.BaseWorld));
            Assert.AreEqual(string.Empty, PlaceChannelActions.NameFor(0x00C9, RealmDefinition.BaseWorld), "a shared landblock is no name");
            Assert.AreEqual(string.Empty, PlaceChannelActions.NameFor(0x0000, RealmDefinition.BaseWorld));
        }

        [TestMethod]
        public void An_instance_names_its_place_with_its_realms_name_even_where_the_list_has_none_or_several()
        {
            var copy = new RealmDefinition { Id = 3, Name = "Black Totem Crypt", Kind = RealmKind.EphemeralTemplate };

            Assert.AreEqual("Black Totem Crypt", PlaceChannelActions.NameFor(0x0099, copy));
            Assert.AreEqual("Black Totem Crypt", PlaceChannelActions.NameFor(0x00C9, copy));
            Assert.AreEqual("Black Totem Crypt", PlaceChannelActions.NameFor(0x0000, copy));
        }

        [TestMethod]
        public void The_registered_handler_answers_the_shared_name_for_a_landblock_the_player_is_not_in()
        {
            PlaceChannelActions.Register();
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            var named = ChannelTestClient.Request(player, PlaceChannelActions.Name, CellBody(0x00990101));
            Assert.AreEqual(ChannelStatus.Ok, named.Status, ChannelTestClient.Text(named.Body));
            Assert.AreEqual("Black Totem Temple", ChannelTestClient.Text(named.Body));

            var shared = ChannelTestClient.Request(player, PlaceChannelActions.Name, CellBody(0x00C90101));
            Assert.AreEqual(ChannelStatus.Ok, shared.Status);
            Assert.AreEqual(string.Empty, ChannelTestClient.Text(shared.Body), "a shared landblock is no name");
        }

        private static byte[] CellBody(uint cell) => ChannelWire.Body(writer => writer.Write(cell));
    }
}
