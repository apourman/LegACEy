using ACE.Common;
using ACE.DatLoader;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The note a LegACEy server with a world of its own sends at character select, before any player exists: the world's name, the cell
    /// dat edition the server runs and the world's update-list address. The LegACEy plugin lays that world over the retail dats; a server
    /// that sends none is plain, and the plugin reads the retail dats. Retail ignores the event.
    /// </summary>
    public static class WorldNote
    {
        /// <summary>Push body: string name, i32 cell dat edition, string update-list address. The client's WorldNoteProtocol reads it.</summary>
        public const string Topic = "legacey.world";

        /// <summary>Sends the note when Config.js names an update list; it goes before the DDD exchange ends, which is how the plugin
        /// knows a server without one sends none.</summary>
        public static void Send(Session session)
        {
            var server = ConfigManager.Config.Server;
            if (string.IsNullOrWhiteSpace(server.LegaceyWorldUpdateUrl))
                return;

            var name = string.IsNullOrWhiteSpace(server.LegaceyWorldName) ? server.WorldName : server.LegaceyWorldName;
            session.Network.EnqueueSend(new GameEventLegaceyChannel(session, ChannelEventKind.Push, ChannelStatus.Ok, 0, Topic,
                Body(name, DatManager.CellDat.Iteration, server.LegaceyWorldUpdateUrl.Trim())));
        }

        public static byte[] Body(string name, int edition, string updateList) => ChannelWire.Body(w =>
        {
            ChannelWire.WriteString(w, name, 256);
            w.Write(edition);
            ChannelWire.WriteString(w, updateList, 2048);
        });
    }
}
