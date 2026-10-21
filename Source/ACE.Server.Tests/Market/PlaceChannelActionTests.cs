using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClientChannel;
using ACE.Server.Tests.ClientChannel;

namespace ACE.Server.Tests.Market
{
    // In the Vault tests: the started world is shared, and starts once a run.
    public partial class VaultTests
    {
        [TestMethod]
        public void The_registered_place_handler_answers_the_shared_name_for_a_landblock_the_player_is_not_in()
        {
            PlaceChannelActions.Register();
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            var named = ChannelTestClient.Request(player, PlaceChannelActions.Name, PlaceCellBody(0x00990101));
            Assert.AreEqual(ChannelStatus.Ok, named.Status, ChannelTestClient.Text(named.Body));
            Assert.AreEqual("Black Totem Temple", ChannelTestClient.Text(named.Body));

            var shared = ChannelTestClient.Request(player, PlaceChannelActions.Name, PlaceCellBody(0x00C90101));
            Assert.AreEqual(ChannelStatus.Ok, shared.Status);
            Assert.AreEqual(string.Empty, ChannelTestClient.Text(shared.Body), "a shared landblock is no name");
        }

        private static byte[] PlaceCellBody(uint cell) => ChannelWire.Body(writer => writer.Write(cell));
    }
}
