using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClientChannel;
using ACE.Server.Realms;

namespace ACE.Server.Tests.ClientChannel
{
    [TestClass]
    public class PlaceChannelActionsTests
    {
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
    }
}
