using System;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The station channel. The server pushes station.open when a player's station session starts and station.close when it ends,
    /// each with the station name and the object's guid. The client sends station.leave, with no body, when the player closes the station's window.
    /// </summary>
    public static class StationChannelActions
    {
        public const string Open = "station.open";

        public const string Close = "station.close";

        public const string Leave = "station.leave";

        public static void Register() => ServerChannel.Register(Leave, context =>
        {
            context.Player.EndStation();
            context.Reply(Array.Empty<byte>());
        });

        internal static byte[] Body(string station, uint stationGuid) => ChannelWire.Body(w =>
        {
            ChannelWire.WriteString(w, station);
            w.Write(stationGuid);
        });
    }
}
