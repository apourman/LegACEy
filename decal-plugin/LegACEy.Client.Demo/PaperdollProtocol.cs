using System.Collections.Generic;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>Bodies of the paperdoll's channel actions; they must match ACE.Server.ClientChannel.PaperdollChannelActions.</summary>
public static class PaperdollProtocol
{
    public const string Look = "paperdoll.look";
    public const string Changed = "paperdoll.changed";
    public const string Inspect = "paperdoll.inspect";

    public static CharacterAppearance ReadLook(byte[] body) => ReadLook(ChannelWire.Reader(body));

    public static byte[] InspectBody(uint objectId) => ChannelWire.Body(writer => writer.Write(objectId));

    /// <summary>An object's look, then what it wears (nothing, for anything but a creature).</summary>
    public static (CharacterAppearance Look, IReadOnlyList<WornGear> Gear) ReadInspect(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var look = ReadLook(reader);
        var gear = new List<WornGear>();
        for (int i = 0, count = reader.ReadUInt16(); i < count; i++)
        {
            var id = reader.ReadUInt32();
            var location = reader.ReadUInt32();
            var visual = new ItemVisual(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
            gear.Add(new WornGear(id, location, visual, ChannelWire.ReadString(reader)));
        }
        return (look, gear);
    }

    private static CharacterAppearance ReadLook(System.IO.BinaryReader reader)
    {
        var setup = reader.ReadUInt32();
        var palette = Known(reader.ReadUInt32(), 0x04000000);
        var palettes = new List<SubPalette>();
        for (int i = 0, count = reader.ReadUInt16(); i < count; i++)
            palettes.Add(new SubPalette(Known(reader.ReadUInt32(), 0x04000000), reader.ReadUInt16(), reader.ReadUInt16()));
        var textures = new List<TextureChange>();
        for (int i = 0, count = reader.ReadUInt16(); i < count; i++)
            textures.Add(new TextureChange(reader.ReadByte(), Known(reader.ReadUInt32(), 0x05000000), Known(reader.ReadUInt32(), 0x05000000)));
        var parts = new List<PartChange>();
        for (int i = 0, count = reader.ReadUInt16(); i < count; i++)
            parts.Add(new PartChange(reader.ReadByte(), Known(reader.ReadUInt32(), 0x01000000)));
        return new CharacterAppearance(setup, palette, palettes, textures, parts);
    }

    /// <summary>
    /// ACE keeps some ids without their file type (clothing palettes are cut to 16 bits); the retail wire's
    /// "packed dword of known type" puts it back, and so does this.
    /// </summary>
    private static uint Known(uint id, uint type) => id != 0 && (id & 0xFF000000) == 0 ? id | type : id;
}

/// <summary>An item someone wears, as paperdoll.inspect sends it: its id, the slots it is wielded in (an EquipMask), its icon and name.</summary>
public sealed record WornGear(uint Id, uint Location, ItemVisual Visual, string Name);
