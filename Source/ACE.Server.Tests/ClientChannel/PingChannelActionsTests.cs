using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.ClientChannel;

namespace ACE.Server.Tests.ClientChannel
{
    [TestClass]
    public class PingChannelActionsTests
    {
        private static Position At(uint cell) => new Position(cell, 96, 96, 0, 0, 0, 0, 1);

        [TestMethod]
        public void A_ping_reaches_the_landblocks_beside_the_sender_outdoors_and_only_their_own_indoors()
        {
            Assert.IsTrue(PingChannelActions.InReach(At(0xA9B40019), At(0xAAB50001)));
            Assert.IsFalse(PingChannelActions.InReach(At(0xA9B40019), At(0xABB40001)), "two landblocks away is past the radar");
            Assert.IsTrue(PingChannelActions.InReach(At(0x00990110), At(0x00990120)));
            Assert.IsFalse(PingChannelActions.InReach(At(0x00990110), At(0x009A0120)), "indoors only the sender's landblock");
        }

        [TestMethod]
        public void The_chat_line_gives_coordinates_outdoors_and_the_place_indoors()
        {
            StringAssert.Matches(PingChannelActions.ChatLine("Rawzz", At(0xA9B40019)), new System.Text.RegularExpressions.Regex(@"^Rawzz pinged \d+\.\dN, \d+\.\dE\.$"));
            Assert.AreEqual("Rawzz pinged a spot in Black Totem Temple.", PingChannelActions.ChatLine("Rawzz", At(0x00990110)));
        }
    }
}
