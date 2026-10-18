using ACE.Server.Managers;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The weather test: the LegACEy client draws snow, a blizzard or a thunderstorm on the 3×3 landblocks around one landblock. Admins set it
    /// with @snow and @thunderstorm; it lives in memory only, so a restart clears it.
    /// </summary>
    public static class WeatherChannelActions
    {
        public enum Kind : byte { None = 0, Snow = 1, Blizzard = 2, Thunderstorm = 3 }

        /// <summary>Reply: u8 kind (0 clear, 1 snow, 2 blizzard, 3 thunderstorm), then, unless clear, u8 landblock x and u8 landblock y. The client's WeatherProtocol reads it.</summary>
        public const string Zone = "weather.zone";

        /// <summary>Pushed to every connected client when the weather changes, with the same body as <see cref="Zone"/>'s reply.</summary>
        public const string Changed = "weather.changed";

        private static readonly object gate = new object();
        private static (Kind Kind, byte X, byte Y) weather;

        public static void Register() => ServerChannel.Register(Zone, context => { lock (gate) context.Reply(Body(weather)); });

        /// <summary>Sets the weather around this landblock, replacing any other, or clears it with <see cref="Kind.None"/>.</summary>
        public static void Set(Kind kind, byte x, byte y)
        {
            byte[] body;
            lock (gate)
            {
                weather = (kind, x, y);
                body = Body(weather);
            }
            foreach (var player in PlayerManager.GetAllOnline())
                if (ServerChannel.IsConnected(player))
                    ServerChannel.Push(player, Changed, body);
        }

        internal static byte[] Body((Kind Kind, byte X, byte Y) weather) => ChannelWire.Body(w =>
        {
            w.Write((byte)weather.Kind);
            if (weather.Kind != Kind.None)
            {
                w.Write(weather.X);
                w.Write(weather.Y);
            }
        });
    }
}
