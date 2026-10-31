using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.GameArt;

/// <summary>
/// A spell as the portal DAT's spell table gives it: its name and description, and what the spellbook and its examination read (ACE.DatLoader's
/// SpellBase). <see cref="Formula"/> is the table's own formula, decrypted; a player casts the one <see cref="SpellFormula.ForAccount"/> makes.
/// </summary>
public sealed record SpellInfo(string Name, string Description)
{
    /// <summary>ACE's MagicSchool: 1 War, 2 Life, 3 Item, 4 Creature, 5 Void.</summary>
    public uint School { get; init; }
    public uint Icon { get; init; }
    public uint Category { get; init; }
    public uint Bitfield { get; init; }
    public uint BaseMana { get; init; }
    public float BaseRangeConstant { get; init; }
    public float BaseRangeMod { get; init; }
    public uint Power { get; init; }
    public uint FormulaVersion { get; init; }
    /// <summary>ACE's SpellType: 1 an enchantment, 12 a fellowship enchantment, which last <see cref="Duration"/> seconds.</summary>
    public uint SpellType { get; init; }
    public double Duration { get; init; }
    /// <summary>The extra mana per target of a fellowship spell, which retail's examination adds to its Mana line.</summary>
    public uint ManaMod { get; init; }
    /// <summary>The spell components, by their id in the component table, in casting order.</summary>
    public IReadOnlyList<uint> Formula { get; init; } = System.Array.Empty<uint>();
}

/// <summary>
/// The portal DAT's spell table (file 0x0E00000E), read as ACE.DatLoader's SpellTable and SpellBase read it: a file id, then the spells as
/// a packed hash table of spell id and spell record. Each record's strings are obfuscated (each byte's nibbles are swapped), and its formula
/// is shifted by a key from their hashes. The effects, recovery and display fields are read past.
/// </summary>
public static class SpellTable
{
    /// <summary>The spell table's file id in the portal DAT.</summary>
    public const uint FileId = 0x0E00000E;

    // The SpellType values (ACE.Entity SpellType) whose records carry extra fields.
    private const uint EnchantmentType = 1, PortalSummonType = 7, FellowEnchantmentType = 12;

    /// <summary>Reads the spell table's file. Its strings are read as Windows-1252 (see <see cref="ReadBytes"/>).</summary>
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

    // The highest component id; a decrypted id above it keeps its low byte, as ACE.DatLoader corrects spells with extended characters.
    private const uint HighestComponent = 198;

    private static SpellInfo ReadSpell(BinaryReader reader)
    {
        var name = ReadBytes(reader);
        Align(reader);
        var description = ReadBytes(reader);
        Align(reader);
        var school = reader.ReadUInt32();
        var icon = reader.ReadUInt32();
        var category = reader.ReadUInt32();
        var bitfield = reader.ReadUInt32();
        var mana = reader.ReadUInt32();
        var rangeConstant = reader.ReadSingle();
        var rangeMod = reader.ReadSingle();
        var power = reader.ReadUInt32();
        Skip(reader, 4); // the spell economy mod
        var formulaVersion = reader.ReadUInt32();
        Skip(reader, 4); // the component loss
        var type = reader.ReadUInt32();
        Skip(reader, 4); // the meta spell id
        var duration = 0.0;
        switch (type)
        {
            case EnchantmentType:
            case FellowEnchantmentType:
                duration = reader.ReadDouble();
                Skip(reader, 4 + 4); // degrade modifier and limit
                break;
            case PortalSummonType:
                Skip(reader, 8); // portal lifetime
                break;
        }
        // The formula is stored shifted by a key made from the name and description hashes.
        var key = SpellFormula.Hash(name) % 0x12107680 + SpellFormula.Hash(description) % 0xBEADCF45;
        var formula = new List<uint>(8);
        for (var i = 0; i < 8; i++)
        {
            var stored = reader.ReadUInt32();
            if (stored == 0) continue;
            var component = stored - key;
            formula.Add(component > HighestComponent ? component & 0xFF : component);
        }
        Skip(reader, 4 + 4 + 4 + 8 + 4 + 4 + 4); // caster, target and fizzle effects, recovery, display order, target type
        var manaMod = reader.ReadUInt32();
        return new SpellInfo(Text(name), Text(description))
        {
            School = school, Icon = icon, Category = category, Bitfield = bitfield, BaseMana = mana, BaseRangeConstant = rangeConstant,
            BaseRangeMod = rangeMod, Power = power, FormulaVersion = formulaVersion, SpellType = type, Duration = duration, Formula = formula,
            ManaMod = manaMod
        };
    }

    /// <summary>A 16-bit length, then the bytes, each with its nibbles swapped back: Windows-1252 text.</summary>
    internal static byte[] ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var bytes = reader.ReadBytes(length);
        if (bytes.Length < length) throw new EndOfStreamException("The spell table ends inside a string.");
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((bytes[i] >> 4) | (bytes[i] << 4));
        return bytes;
    }

    internal static string Text(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = Windows1252.ToChar(bytes[i]);
        return new string(chars);
    }

    /// <summary>Strings end on a four-byte boundary of the file, as the DAT's own reader aligns them.</summary>
    internal static void Align(BinaryReader reader)
    {
        var remainder = reader.BaseStream.Position % 4;
        if (remainder != 0) Skip(reader, (int)(4 - remainder));
    }

    private static void Skip(BinaryReader reader, int count) => reader.BaseStream.Seek(count, SeekOrigin.Current);
}
