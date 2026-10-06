using ACE.Server.ClientChannel;

namespace ACE.Server.Network.GameEvent.Events
{
    /// <summary>
    /// A reply or push on the LegACEy in-band server channel. See ChannelWire for the payload.
    /// </summary>
    public class GameEventLegaceyChannel : GameEventMessage
    {
        public GameEventLegaceyChannel(Session session, ChannelEventKind kind, ChannelStatus status, uint requestId, string topic, byte[] body)
            : base(GameEventType.LegaceyChannel, GameMessageGroup.UIQueue, session, 16 + (body?.Length ?? 0))
        {
            ChannelWire.WriteEvent(Writer, kind, status, requestId, topic, body);
        }
    }
}
