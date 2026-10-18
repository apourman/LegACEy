using ACE.Server.Managers;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The snow test: the LegACEy client snows on the 3×3 landblocks around one landblock, lightly or as a blizzard. Admins set it with @snow;
    /// it lives in memory only, so a restart clears it.
    /// </summary>
    public static class SnowChannelActions
    {
        public enum Intensity : byte { None = 0, Light = 1, Blizzard = 2 }

        /// <summary>Reply: u8 intensity (0 none, 1 light, 2 blizzard), then, unless none, u8 landblock x and u8 landblock y. The client's SnowProtocol reads it.</summary>
        public const string Zone = "snow.zone";

        /// <summary>Pushed to every connected client when the snow changes, with the same body as <see cref="Zone"/>'s reply.</summary>
        public const string Changed = "snow.changed";

        private static readonly object gate = new object();
        private static (Intensity Intensity, byte X, byte Y) snow;

        public static void Register() => ServerChannel.Register(Zone, context => { lock (gate) context.Reply(Body(snow)); });

        /// <summary>Snows on the landblocks around this one, or stops the snow with <see cref="Intensity.None"/>.</summary>
        public static void Set(Intensity intensity, byte x, byte y)
        {
            byte[] body;
            lock (gate)
            {
                snow = (intensity, x, y);
                body = Body(snow);
            }
            foreach (var player in PlayerManager.GetAllOnline())
                if (ServerChannel.IsConnected(player))
                    ServerChannel.Push(player, Changed, body);
        }

        internal static byte[] Body((Intensity Intensity, byte X, byte Y) snow) => ChannelWire.Body(w =>
        {
            w.Write((byte)snow.Intensity);
            if (snow.Intensity != Intensity.None)
            {
                w.Write(snow.X);
                w.Write(snow.Y);
            }
        });
    }
}
