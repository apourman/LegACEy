using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClientChannel;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Tests.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.ClientChannel
{
    /// <summary>
    /// Sends channel requests as the plugin does, and reads the replies and pushes back from the queued game events. Needs a started world (VaultTestWorld).
    /// </summary>
    internal static class ChannelTestClient
    {
        private static uint nextRequestId;

        public static string Text(byte[] body) => body.Length >= 2 ? ChannelWire.ReadString(new BinaryReader(new MemoryStream(body), Encoding.UTF8)) : string.Empty;

        public static ChannelEvent Request(Player player, string action, byte[] body)
        {
            var id = Send(player, action, body);

            return WaitForEvent(player, e => e.Kind == ChannelEventKind.Reply && e.RequestId == id);
        }

        /// <summary>
        /// Sends a request without waiting for its reply. Returns the request id.
        /// </summary>
        public static uint Send(Player player, string action, byte[] body)
        {
            var id = ++nextRequestId;
            var payload = ChannelWire.Body(w =>
            {
                w.Write(ChannelWire.Version);
                w.Write(id);
                ChannelWire.WriteString(w, action);
                w.Write((uint)body.Length);
                w.Write(body);
            });

            VaultTestWorld.OnWorldThread(() => ServerChannel.Receive(player.Session, new BinaryReader(new MemoryStream(payload))));

            return id;
        }

        public static ChannelEvent WaitForEvent(Player player, Func<ChannelEvent, bool> match) => WaitForEvents(player, match).Last();

        /// <summary>
        /// Every channel event up to and including the first one that matches, in the order they arrived
        /// </summary>
        public static List<ChannelEvent> WaitForEvents(Player player, Func<ChannelEvent, bool> match)
        {
            var seen = new List<ChannelEvent>();

            VaultTestWorld.WaitUntil(() =>
            {
                seen.AddRange(VaultTestWorld.TakeSent(player).OfType<GameEventLegaceyChannel>().Select(Decode));
                return seen.Any(match);
            }, "a LegACEy channel event");

            return seen.GetRange(0, seen.FindIndex(e => match(e)) + 1);
        }

        public static ChannelEvent Decode(GameMessage message)
        {
            // opcode, character, event sequence, event type, then the channel payload
            var reader = new BinaryReader(new MemoryStream(message.Data.ToArray()), Encoding.UTF8);
            reader.ReadBytes(16);
            Assert.AreEqual(ChannelWire.Version, reader.ReadUInt16());
            var e = new ChannelEvent { Kind = (ChannelEventKind)reader.ReadByte(), Status = (ChannelStatus)reader.ReadByte(), RequestId = reader.ReadUInt32(), Topic = ChannelWire.ReadString(reader) };
            e.Body = reader.ReadBytes((int)reader.ReadUInt32());
            return e;
        }
    }

    internal sealed class ChannelEvent
    {
        public ChannelEventKind Kind;
        public ChannelStatus Status;
        public uint RequestId;
        public string Topic;
        public byte[] Body;
    }
}
