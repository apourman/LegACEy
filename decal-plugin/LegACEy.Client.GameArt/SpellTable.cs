using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.GameArt;

/// <summary>A spell's name and description, as the portal DAT's spell table gives them.</summary>
public readonly struct SpellInfo(string Name, string Description)
{
    public string Name { get; } = Name;
    public string Description { get; } = Description;
}

/// <summary>
/// The portal DAT's spell table (file 0x0E00000E), read as ACE.DatLoader's SpellTable and SpellBase read it: a file id, then the spells as
/// a packed hash table of spell id and spell record. Each record's strings are obfuscated (each byte's nibbles are swapped). Only the
/// names and descriptions are kept; the rest of each record is read past to reach the next one.
/// </summary>
public static class SpellTable
{
    /// <summary>The spell table's file id in the portal DAT.</summary>
    public const uint FileId = 0x0E00000E;

    // The SpellType values (ACE.Entity SpellType) whose records carry extra fields.
    private const uint EnchantmentType = 1, PortalSummonType = 7, FellowEnchantmentType = 12;

    /// <summary>Reads the spell table's file. Its strings are read as Windows-1252 (see <see cref="ReadString"/>).</summary>
    public static IReadOnlyDictionary<uint, SpellInfo> Parse(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the table's own id
        var count = reader.ReadUInt16();
        reader.ReadUInt16(); // the bucket size: a hash detail the reader does not need
        var spells = new Dictionary<uint, SpellInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadUInt32();
            spells[id] = ReadSpell(reader);
        }
        return spells;
    }

    private static SpellInfo ReadSpell(BinaryReader reader)
    {
        var name = ReadString(reader);
        Align(reader);
        var description = ReadString(reader);
        Align(reader);
        // School through ComponentLoss: eleven words this reader does not keep.
        Skip(reader, 11 * 4);
        var type = reader.ReadUInt32();
        Skip(reader, 4); // the meta spell id
        switch (type)
        {
            case EnchantmentType:
            case FellowEnchantmentType:
                Skip(reader, 8 + 4 + 4); // duration, degrade modifier and limit
                break;
            case PortalSummonType:
                Skip(reader, 8); // portal lifetime
                break;
        }
        Skip(reader, 8 * 4); // the eight formula components
        Skip(reader, 4 + 4 + 4 + 8 + 4 + 4 + 4 + 4); // caster, target and fizzle effects, recovery, display order, target type, mana mod
        return new SpellInfo(name, description);
    }

    /// <summary>A 16-bit length, then the bytes, each with its nibbles swapped back and read as Windows-1252.</summary>
    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var bytes = reader.ReadBytes(length);
        if (bytes.Length < length) throw new EndOfStreamException("The spell table ends inside a string.");
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var source = (byte)((bytes[i] >> 4) | (bytes[i] << 4));
            chars[i] = source is >= 0x80 and <= 0x9F ? Windows1252High[source - 0x80] : (char)source;
        }
        return new string(chars);
    }

    // Windows-1252 for bytes 0x80-0x9F, where it differs from Latin-1 (the apostrophe in "Asheron's" is 0x92, the dash in "80 - 200" is 0x96).
    // The five undefined bytes keep their control characters, as the code page does.
    private const string Windows1252High =
        "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F" +
        "\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    /// <summary>Strings end on a four-byte boundary of the file, as the DAT's own reader aligns them.</summary>
    private static void Align(BinaryReader reader)
    {
        var remainder = reader.BaseStream.Position % 4;
        if (remainder != 0) Skip(reader, (int)(4 - remainder));
    }

    private static void Skip(BinaryReader reader, int count) => reader.BaseStream.Seek(count, SeekOrigin.Current);
}
