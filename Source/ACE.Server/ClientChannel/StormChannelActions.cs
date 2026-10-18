using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The storm fog test: one safe landblock whose ring of adjacent landblocks the LegACEy client fills with fog. Admins toggle it with
    /// @stormfog; it lives in memory only, so a restart clears it.
    /// </summary>
    public static class StormChannelActions
    {
        /// <summary>Reply: u8 1 then u8 landblock x and u8 landblock y, or u8 0 when there is no storm. The client's StormProtocol reads it.</summary>
        public const string Zone = "storm.zone";

        /// <summary>Pushed to every connected client when the zone changes, with the same body as <see cref="Zone"/>'s reply.</summary>
        public const string Changed = "storm.changed";

        private static readonly object gate = new object();
        private static (byte X, byte Y)? zone;

        public static (byte X, byte Y)? Current { get { lock (gate) return zone; } }

        public static void Register() => ServerChannel.Register(Zone, context => context.Reply(Body(Current)));

        /// <summary>Makes the landblock the safe zone, or clears the storm when it already is. Returns the new zone.</summary>
        public static (byte X, byte Y)? Toggle(byte x, byte y)
        {
            (byte X, byte Y)? now;
            lock (gate)
                now = zone = zone == (x, y) ? null : (x, y);

            var body = Body(now);
            foreach (var player in PlayerManager.GetAllOnline())
                if (ServerChannel.IsConnected(player))
                    ServerChannel.Push(player, Changed, body);
            return now;
        }

        internal static byte[] Body((byte X, byte Y)? zone) => ChannelWire.Body(w =>
        {
            w.Write((byte)(zone == null ? 0 : 1));
            if (zone is { } z)
            {
                w.Write(z.X);
                w.Write(z.Y);
            }
        });
    }
}
