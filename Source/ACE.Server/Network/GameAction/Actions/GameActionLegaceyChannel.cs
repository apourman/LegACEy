using ACE.Server.ClientChannel;

namespace ACE.Server.Network.GameAction.Actions
{
    public static class GameActionLegaceyChannel
    {
        [GameAction(GameActionType.LegaceyChannel)]
        public static void Handle(ClientMessage message, Session session)
        {
            ServerChannel.Receive(session, message.Payload);
        }
    }
}
