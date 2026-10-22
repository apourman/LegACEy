using System.Collections.Generic;
using System.IO;

using ACE.Entity.Enum;
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
        /// <see cref="Look"/> sends the player's.
        /// </summary>
        public const string Inspect = "paperdoll.inspect";

        public static void Register()
        {
            ServerChannel.Register(Look, context => context.Reply(LookBody(context.Player)));
            ServerChannel.Register(Inspect, context =>
            {
                uint guid;
                using (var reader = context.Body())
                    guid = reader.BaseStream.Length == 4 ? reader.ReadUInt32() : 0;
                var target = guid == context.Player.Guid.Full ? context.Player : context.Player.FindObject(guid, Player.SearchLocations.Everywhere);
                if (target != null)
                    context.Reply(LookBody(target));
                // Not in the world: it may be in the player's Vault, which looks it up off the world thread.
                else if (guid != 0 && Market.Vault.Available)
                    Market.VaultAppraisal.Look(context, guid);
                else
                    context.Fail(ChannelStatus.BadRequest, "That is not in sight.");
            });
        }

        public static void PushChanged(Player player)
        {
            if (ServerChannel.IsConnected(player))
                ServerChannel.Push(player, Changed, System.Array.Empty<byte>());
        }

        /// <summary>
        /// The look: the object's setup and ObjDesc, then u16 count of what it holds (a wielded weapon or shield, which the client draws
        /// apart from the body), each as u32 parent location (ACE's ParentLocation), f32 scale, then the held item's setup and ObjDesc.
        /// </summary>
        internal static byte[] LookBody(WorldObject target) => ChannelWire.Body(w =>
        {
            WriteLook(w, target);
            var held = new List<WorldObject>();
            if (target is Creature creature)
                foreach (var item in creature.EquippedObjects.Values)
                    if (item.ParentLocation is { } location && location != ParentLocation.None && item.SetupTableId != 0)
                        held.Add(item);
            w.Write((ushort)held.Count);
            foreach (var item in held)
            {
                w.Write((uint)item.ParentLocation.Value);
                w.Write(item.ObjScale ?? 1f);
                WriteLook(w, item);
            }
        });

        private static void WriteLook(BinaryWriter w, WorldObject target)
        {
            var look = target.CalculateObjDesc();
            w.Write(target.SetupTableId);
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
        }
    }
}
