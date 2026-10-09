using System.Collections.Generic;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>Bodies of the paperdoll's channel actions; they must match ACE.Server.ClientChannel.PaperdollChannelActions.</summary>
public static class PaperdollProtocol
{
    public const string Look = "paperdoll.look";
    public const string Changed = "paperdoll.changed";

    public static CharacterAppearance ReadLook(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
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
