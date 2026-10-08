namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The channel's hello: the client asks what the server can serve, and the reply lists every registered action.
    /// The Decal plugin reads it as version, character name, action count and action names.
    /// </summary>
    public static class ChannelHelloActions
    {
        public const string Hello = "channel.hello";

        public static void Register() => ServerChannel.Register(Hello, context => context.Reply(Body(context.Player.Name)));

        internal static byte[] Body(string playerName) => ChannelWire.Body(w =>
        {
            var actions = ServerChannel.ActionNames();

            w.Write(ChannelWire.Version);
            ChannelWire.WriteString(w, playerName);
            w.Write(actions.Count);
            foreach (var action in actions)
                ChannelWire.WriteString(w, action);
        });
    }
}
