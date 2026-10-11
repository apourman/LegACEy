using ACE.Server.WorldObjects;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The paperdoll prototype's actions on the in-band server channel: the LegACEy client draws the player in 3D from portal.dat,
    /// and asks the server how the player looks, since the server already works it out for everyone who sees them.
    /// </summary>
    public static class PaperdollChannelActions
    {
        /// <summary>
        /// Reply: u32 setup, u32 base palette, u16 count of (u32 palette, u16 offset, u16 length) in blocks of 8 colours,
        /// u16 count of (u8 part, u32 old texture, u32 new texture), u16 count of (u8 part, u32 model). The client's PaperdollProtocol reads it.
        /// </summary>
        public const string Look = "paperdoll.look";

        /// <summary>
        /// Pushed, with no body, whenever the player's appearance is sent to the players around them
        /// </summary>
        public const string Changed = "paperdoll.changed";

        public static void Register() => ServerChannel.Register(Look, context => context.Reply(Body(context.Player)));

        public static void PushChanged(Player player)
        {
            if (ServerChannel.IsConnected(player))
                ServerChannel.Push(player, Changed, System.Array.Empty<byte>());
        }

        private static byte[] Body(Player player)
        {
            var look = player.CalculateObjDesc();

            return ChannelWire.Body(w =>
            {
                w.Write(player.SetupTableId);
                w.Write(look.PaletteID);
                w.Write((ushort)look.SubPalettes.Count);
                foreach (var palette in look.SubPalettes)
                {
                    w.Write(palette.SubPaletteId);
                    w.Write(palette.Offset);
                    w.Write(palette.Length);
                }
                w.Write((ushort)look.TextureChanges.Count);
                foreach (var texture in look.TextureChanges)
                {
                    w.Write(texture.PartIndex);
                    w.Write(texture.OldTexture);
                    w.Write(texture.NewTexture);
                }
                w.Write((ushort)look.AnimPartChanges.Count);
                foreach (var part in look.AnimPartChanges)
                {
                    w.Write(part.Index);
                    w.Write(part.AnimationId);
                }
            });
        }
    }
}
