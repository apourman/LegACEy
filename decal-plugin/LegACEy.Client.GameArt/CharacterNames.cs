using System;
using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.GameArt;

/// <summary>
/// The names an appraisal gives only as ids: a creature's type (the portal's creature enum mapper) and a character's title (the portal's
/// title enum mapper, then the English string table that names each title). Read as ACE.DatLoader's EnumMapper and StringTable read them.
/// </summary>
public sealed class CharacterNames
{
    /// <summary>The creature type enum mapper in the portal DAT.</summary>
    public const uint CreatureTypeMapper = 0x2200000E;
    /// <summary>The character title enum mapper in the portal DAT.</summary>
    public const uint TitleMapper = 0x22000041;
    /// <summary>The character title strings in the English language DAT (client_local_English.dat).</summary>
    public const uint TitleStringTable = 0x2300000E;

    public static readonly CharacterNames None = new(new Dictionary<uint, string>(), new Dictionary<uint, string>());

    public CharacterNames(IReadOnlyDictionary<uint, string> creatureTypes, IReadOnlyDictionary<uint, string> titles)
    {
        CreatureTypes = creatureTypes ?? throw new ArgumentNullException(nameof(creatureTypes));
        Titles = titles ?? throw new ArgumentNullException(nameof(titles));
    }

    /// <summary>A creature type's name by the server's CreatureType value.</summary>
    public IReadOnlyDictionary<uint, string> CreatureTypes { get; }

    /// <summary>A character title's text by the server's CharacterTitleId value.</summary>
    public IReadOnlyDictionary<uint, string> Titles { get; }

    /// <summary>
    /// Adds an enum mapper's names to <paramref name="names"/> (a name already there is kept) and returns its base mapper's id, or zero. The
    /// mapper is the id, its base id, a numbering byte, then a compressed count of (key, one-byte-length string) pairs.
    /// </summary>
    public static uint ParseEnumMapper(byte[] file, IDictionary<uint, string> names)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the mapper's own id
        var baseId = reader.ReadUInt32();
        reader.ReadByte(); // the numbering type
        var count = ReadCompressed(reader);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadUInt32();
            var length = reader.ReadByte();
            var bytes = reader.ReadBytes(length);
            if (bytes.Length < length) throw new EndOfStreamException("The enum mapper ends inside a name.");
            if (!names.ContainsKey(key)) names[key] = Latin1(bytes);
        }
        return baseId;
    }

    /// <summary>
    /// The string table's first string by the hash of its key. Each entry is a key, the variable names and variables (each a 16-bit or
    /// 32-bit count of unicode strings), the strings, then comments that are skipped.
    /// </summary>
    public static Dictionary<uint, string> ParseStringTable(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the table's own id
        reader.ReadUInt32(); // the language, English
        reader.ReadByte();
        var entries = ReadCompressed(reader);
        var strings = new Dictionary<uint, string>();
        for (var i = 0; i < entries; i++)
        {
            var key = reader.ReadUInt32();
            SkipUnicode(reader, reader.ReadUInt16()); // variable names
            SkipUnicode(reader, reader.ReadUInt16()); // variables
            var count = reader.ReadUInt32();
            string? first = null;
            for (var s = 0; s < count; s++)
            {
                var text = ReadUnicode(reader);
                first ??= text;
            }
            var comments = reader.ReadUInt32();
            reader.BaseStream.Seek(comments * 4L, SeekOrigin.Current);
            reader.ReadByte();
            if (first != null && !strings.ContainsKey(key)) strings[key] = first;
        }
        return strings;
    }

    /// <summary>The hash a string table keys its entries by: ACE's SpellTable.ComputeHash, which is the same as the DAT's own.</summary>
    public static uint Hash(string value)
    {
        uint result = 0u;
        foreach (var c in value)
        {
            result = unchecked((result << 4) + (byte)c);
            var high = result & 0xF0000000u;
            if (high != 0u) result = ((high >> 24) ^ result) & 0x0FFFFFFFu;
        }
        return result == uint.MaxValue ? uint.MaxValue - 1u : result;
    }

    private static uint ReadCompressed(BinaryReader reader)
    {
        var b0 = reader.ReadByte();
        if ((b0 & 0x80) == 0) return b0;
        var b1 = reader.ReadByte();
        if ((b0 & 0x40) == 0) return (uint)(((b0 & 0x7F) << 8) | b1);
        var s = reader.ReadUInt16();
        return (uint)(((((b0 & 0x3F) << 8) | b1) << 16) | s);
    }

    private static void SkipUnicode(BinaryReader reader, int count)
    {
        for (var i = 0; i < count; i++) ReadUnicode(reader);
    }

    /// <summary>A compressed length, then that many 16-bit characters.</summary>
    private static string ReadUnicode(BinaryReader reader)
    {
        var length = ReadCompressed(reader);
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)reader.ReadUInt16();
        return new string(chars);
    }

    private static string Latin1(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
        return new string(chars);
    }
}
