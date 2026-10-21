using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// Radar pings: a player Ctrl+clicks a spot on the LegACEy radar and their fellowship sees it there. Fellows in the same instance with
    /// the LegACEy client get it on their radar; every fellow gets a fellowship chat line with the coordinates. Without a fellowship only
    /// the sender sees it.
    /// </summary>
    public static class PingChannelActions
    {
        /// <summary>Request: u32 cell id, f32 x, f32 y, f32 z (landblock metres). Reply: empty. The client's PingProtocol writes it.</summary>
        public const string Place = "ping.place";

        /// <summary>Pushed to the sender and their fellows: string sender's name, then the request's cell, x, y and z.</summary>
        public const string Placed = "ping.placed";

        public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);

        private static readonly ConcurrentDictionary<ulong, DateTime> lastPing = new ConcurrentDictionary<ulong, DateTime>();

        public static void Register() => ServerChannel.Register(Place, context =>
        {
            if (context.Request.Body.Length != 16)
            {
                context.Fail(ChannelStatus.BadRequest, "A ping is one cell id and three coordinates.");
                return;
            }

            uint cell;
            float x, y, z;
            using (var reader = context.Body())
            {
                cell = reader.ReadUInt32();
                x = reader.ReadSingle();
                y = reader.ReadSingle();
                z = reader.ReadSingle();
            }

            var player = context.Player;
            var at = new Position(cell, x, y, z, 0, 0, 0, 1);

            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || player.Location == null || !InReach(player.Location, at))
            {
                context.Fail(ChannelStatus.BadRequest, "You can only ping a spot on your radar.");
                return;
            }

            var now = DateTime.UtcNow;
            if (lastPing.TryGetValue(player.Guid.Full, out var last) && now - last < Cooldown)
            {
                context.Fail(ChannelStatus.RateLimited, "Wait a moment before pinging again.");
                return;
            }
            lastPing[player.Guid.Full] = now;

            var body = ChannelWire.Body(w =>
            {
                ChannelWire.WriteString(w, player.Name);
                w.Write(cell);
                w.Write(x);
                w.Write(y);
                w.Write(z);
            });
            var line = ChatLine(player.Name, at);

            foreach (var member in Audience(player))
            {
                if (member != player && member.SquelchManager.Squelches.Contains(player, ChatMessageType.Fellowship))
                    continue;
                if (member.Location?.Instance == player.Location.Instance)
                    ServerChannel.Push(member, Placed, body);
                if (player.Fellowship != null)
                    member.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Fellowship));
            }

            context.Reply(Array.Empty<byte>());
        });

        // The sender and their fellows, or the sender alone.
        private static IEnumerable<Player> Audience(Player player) =>
            player.Fellowship?.GetFellowshipMembers().Values.Append(player).Distinct() ?? new[] { player };

        /// <summary>
        /// Whether the spot could be on the sender's radar: their own landblock or one beside it (the radar reaches 150 m), and indoors only
        /// their own landblock.
        /// </summary>
        internal static bool InReach(Position player, Position at)
        {
            var dx = Math.Abs((int)(at.Cell >> 24) - (int)(player.Cell >> 24));
            var dy = Math.Abs((int)((at.Cell >> 16) & 0xFF) - (int)((player.Cell >> 16) & 0xFF));
            var indoors = (player.Cell & 0xFFFF) >= 0x100;
            return indoors ? dx == 0 && dy == 0 : dx <= 1 && dy <= 1;
        }

        /// <summary>The fellowship chat line: the coordinates outdoors, which GoArrow and the like can follow, else the place's name.</summary>
        internal static string ChatLine(string sender, Position at)
        {
            if (at.GetMapCoordStr() is { } coordinates)
                return $"{sender} pinged {coordinates}.";
            var place = PlaceChannelActions.NameFor((ushort)(at.Cell >> 16), null);
            return place.Length > 0 ? $"{sender} pinged a spot in {place}." : $"{sender} pinged a spot nearby.";
        }
    }
}
