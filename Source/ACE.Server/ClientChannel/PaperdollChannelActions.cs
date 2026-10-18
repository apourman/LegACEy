using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Request: u32 guid of an object the player can see or holds (their own guid for themselves). Reply: that object's look, as
        /// <see cref="Look"/> sends it, then u16 count of what it wears: (u32 guid, u32 wielded location, u32 icon, u32 underlay,
        /// u32 overlay, u32 ui effects, string name). Only creatures wear anything.
        /// </summary>
        public const string Inspect = "paperdoll.inspect";

        public static void Register()
        {
            ServerChannel.Register(Look, context => context.Reply(Body(context.Player)));
            ServerChannel.Register(Inspect, context =>
            {
                uint guid;
                using (var reader = context.Body())
                    guid = reader.BaseStream.Length == 4 ? reader.ReadUInt32() : 0;
                var target = guid == context.Player.Guid.Full ? context.Player : context.Player.FindObject(guid, Player.SearchLocations.Everywhere);
                if (target == null)
                {
                    context.Fail(ChannelStatus.BadRequest, "That is not in sight.");
                    return;
                }
                context.Reply(ChannelWire.Body(w =>
                {
                    w.Write(Body(target));
                    var worn = target is Creature creature ? creature.EquippedObjects.Values.ToList() : new List<WorldObject>();
                    w.Write((ushort)worn.Count);
                    foreach (var item in worn)
                    {
                        w.Write(item.Guid.Full);
                        w.Write((uint)(item.CurrentWieldedLocation ?? 0));
                        w.Write(item.IconId);
                        w.Write(item.IconUnderlayId ?? 0);
                        w.Write(item.IconOverlayId ?? 0);
                        w.Write((uint)(item.UiEffects ?? 0));
                        ChannelWire.WriteString(w, item.Name);
                    }
                }));
            });
        }

        public static void PushChanged(Player player)
        {
            if (ServerChannel.IsConnected(player))
                ServerChannel.Push(player, Changed, System.Array.Empty<byte>());
        }

        private static byte[] Body(WorldObject player)
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
