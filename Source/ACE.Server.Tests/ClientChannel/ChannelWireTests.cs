using System;
using System.IO;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClientChannel;

namespace ACE.Server.Tests.ClientChannel
{
    /// <summary>
    /// The channel's wire format. The golden request is the one the Decal plugin's ServerChannelTests encode, so the two sides can't drift.
    /// </summary>
    [TestClass]
    public class ChannelWireTests
    {
        private static readonly byte[] GoldenRequest =
        {
            0x01, 0x00,
            0x07, 0x00, 0x00, 0x00,
            0x0A, 0x00, (byte)'v', (byte)'a', (byte)'u', (byte)'l', (byte)'t', (byte)'.', (byte)'l', (byte)'i', (byte)'s', (byte)'t',
            0x02, 0x00, 0x00, 0x00, 0xAB, 0xCD
        };

        [TestMethod]
        public void TryReadRequest_DecodesThePluginsEncoding()
        {
            Assert.IsTrue(ChannelWire.TryReadRequest(new BinaryReader(new MemoryStream(GoldenRequest)), out var request, out var error), error);

            Assert.AreEqual(7u, request.RequestId);
            Assert.AreEqual("vault.list", request.Action);
            CollectionAssert.AreEqual(new byte[] { 0xAB, 0xCD }, request.Body);
        }

        [TestMethod]
        public void TryReadRequest_RefusesOtherVersionsOversizeAndTruncatedRequests()
        {
            var otherVersion = (byte[])GoldenRequest.Clone();
            otherVersion[0] = 2;
            var oversize = ChannelWire.Body(w => { w.Write((ushort)1); w.Write(1u); ChannelWire.WriteString(w, "a"); w.Write((uint)ChannelWire.MaxRequestBody + 1); });

            foreach (var payload in new[] { otherVersion, oversize, GoldenRequest[..^1], Array.Empty<byte>() })
                Assert.IsFalse(ChannelWire.TryReadRequest(new BinaryReader(new MemoryStream(payload)), out _, out _));
        }

        [TestMethod]
        public void WriteEvent_LaysOutReplyAsThePluginReadsIt()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                ChannelWire.WriteEvent(writer, ChannelEventKind.Reply, ChannelStatus.UnknownAction, 7, "x", new byte[] { 9 });

            CollectionAssert.AreEqual(new byte[] { 0x01, 0x00, 0x01, 0x02, 0x07, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)'x', 0x01, 0x00, 0x00, 0x00, 0x09 }, stream.ToArray());
        }
    }
}
