using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.GameArt;

/// <summary>A spell component as the portal's component table gives it, with the weenie class its items are made from.</summary>
public sealed record SpellComponentInfo(uint Id, string Name, uint Icon, uint Type, uint Wcid);

/// <summary>
/// The portal's spell component table (file 0x0E00000F, read as ACE.DatLoader's SpellComponentsTable and SpellComponentBase read it), joined
/// with the mapper from component id to weenie class (0x27000002), which the server's desired-component levels and the items are keyed by.
/// </summary>
public sealed class SpellComponentTable
{
    public const uint FileId = 0x0E00000F;
    public const uint WcidMapper = 0x27000002;

    public static readonly SpellComponentTable None = new(new Dictionary<uint, SpellComponentInfo>());

    private readonly Dictionary<uint, SpellComponentInfo> _byWcid = new();

    public SpellComponentTable(IReadOnlyDictionary<uint, SpellComponentInfo> components)
    {
        Components = components;
        foreach (var component in components.Values)
            if (component.Wcid != 0) _byWcid[component.Wcid] = component;
    }

    /// <summary>The components by their id in the table, which spell formulas use.</summary>
    public IReadOnlyDictionary<uint, SpellComponentInfo> Components { get; }

    /// <summary>The component its items' weenie class makes, or null when the class is no spell component.</summary>
    public SpellComponentInfo? ByWcid(uint wcid) => _byWcid.TryGetValue(wcid, out var component) ? component : null;

    /// <summary>Reads the table, giving each component the weenie class the mapper names for it (0 when it names none).</summary>
    public static SpellComponentTable Parse(byte[] file, IReadOnlyDictionary<uint, uint> wcids)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the table's own id
        var count = reader.ReadUInt16();
        reader.ReadUInt16(); // the bucket size
        var components = new Dictionary<uint, SpellComponentInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadUInt32();
            var name = ReadString(reader);
            Align(reader);
            reader.ReadUInt32(); // the category
            var icon = reader.ReadUInt32();
            var type = reader.ReadUInt32();
            reader.ReadUInt32(); // the gesture
            reader.ReadSingle(); // the time
            ReadString(reader); // the spell words
            Align(reader);
            reader.ReadSingle(); // the burn rate
            components[id] = new SpellComponentInfo(id, name, icon, type, wcids.TryGetValue(id, out var wcid) ? wcid : 0);
        }
        return new SpellComponentTable(components);
    }

    /// <summary>A 16-bit length, then the bytes, each with its nibbles swapped back and read as Windows-1252.</summary>
    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var bytes = reader.ReadBytes(length);
        if (bytes.Length < length) throw new EndOfStreamException("The component table ends inside a string.");
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = Windows1252.ToChar((byte)((bytes[i] >> 4) | (bytes[i] << 4)));
        return new string(chars);
    }

    private static void Align(BinaryReader reader)
    {
        var remainder = reader.BaseStream.Position % 4;
        if (remainder != 0) reader.BaseStream.Seek(4 - remainder, SeekOrigin.Current);
    }
}
