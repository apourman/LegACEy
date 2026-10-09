using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClientChannel;
using ACE.Server.Market;

namespace ACE.Server.Tests.ClientChannel
{
    [TestClass]
    public class ChannelHelloTests
    {
        [TestMethod]
        public void Hello_ListsPaperdollVaultStationAndHelloActions()
        {
            ChannelHelloActions.Register();
            PaperdollChannelActions.Register();
            VaultChannelActions.Register();
            StationChannelActions.Register();

            var reader = new BinaryReader(new MemoryStream(ChannelHelloActions.Body("Tester")), Encoding.UTF8);

            Assert.AreEqual(ChannelWire.Version, reader.ReadUInt16());
            Assert.AreEqual("Tester", ChannelWire.ReadString(reader));
            var count = reader.ReadInt32();
            var names = Enumerable.Range(0, count).Select(_ => ChannelWire.ReadString(reader)).ToArray();

            CollectionAssert.IsSubsetOf(
                new[]
                {
                    ChannelHelloActions.Hello,
                    PaperdollChannelActions.Look,
                    VaultChannelActions.List,
                    VaultChannelActions.Deposit,
                    VaultChannelActions.Withdraw,
                    VaultChannelActions.Check,
                    VaultChannelActions.Move,
                    VaultChannelActions.WithdrawBatch,
                    StationChannelActions.Leave,
                },
                names);
        }
    }
}
