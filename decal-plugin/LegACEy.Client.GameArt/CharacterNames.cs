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
    /// <summary>
    /// The creature type enum mapper in the portal DAT. OpenAC names creature types from it (CreatureDisplayNameResolver.MapperDid, MIT), and
    /// the installed portal DAT agrees with ACE's CreatureType values: 1 Olthoi, 2 Banderling, 3 Drudge, 7 Mite, 10 Rat.
    /// </summary>
    public const uint CreatureTypeMapper = 0x2200000E;
    /// <summary>The character title enum mapper in the portal DAT (ACE: Player_Character.EnumMapper_CharacterTitle_FileID).</summary>
    public const uint TitleMapper = 0x22000041;
    /// <summary>The character title strings in the English language DAT (client_local_English.dat), ACE's StringTable.CharacterTitle_FileID.</summary>
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
    /// Adds an enum mapper's names to <paramref name="names"/> (a name already there is kept). The mapper is its own id, its base mapper's
    /// id (not followed: the creature and title mappers hold their names themselves), a numbering byte, then a compressed count of
    /// (key, one-byte-length string) pairs. Read as ACE.DatLoader's EnumMapper reads it.
    /// </summary>
    public static void ParseEnumMapper(byte[] file, IDictionary<uint, string> names)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the mapper's own id
        reader.ReadUInt32(); // the base mapper's id
        reader.ReadByte(); // the numbering type
        var count = DatModels.ReadCompressed(reader);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadUInt32();
            var length = reader.ReadByte();
            var bytes = reader.ReadBytes(length);
            if (bytes.Length < length) throw new EndOfStreamException("The enum mapper ends inside a name.");
            if (!names.ContainsKey(key)) names[key] = Latin1(bytes);
        }
    }

    /// <summary>
    /// The string table's first string by the hash of its key. Each entry is its key, the variable names and variables (each a 16-bit count
    /// of unicode strings), the strings (a 32-bit count), then comments (a 32-bit count of 32-bit values), then a byte. Read as ACE.DatLoader's
    /// StringTable and StringTableData read them.
    /// </summary>
    public static Dictionary<uint, string> ParseStringTable(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the table's own id
        reader.ReadUInt32(); // the language, English
        reader.ReadByte();
        var entries = DatModels.ReadCompressed(reader);
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

    /// <summary>
    /// The hash a string table keys its entries by, as ACE's SpellTable.ComputeHash computes it (Source/ACE.DatLoader/FileTypes/SpellTable.cs,
    /// lines 32-51): each byte is read signed, the running value is shifted a nibble, and the top nibble is folded back in. ACE does not clamp
    /// the result. The bytes are Windows-1252: a character up to 0xFF is its byte, and anything beyond is written as '?'.
    /// </summary>
    public static uint Hash(string value)
    {
        long result = 0;
        foreach (var c in value)
        {
            var signed = unchecked((sbyte)(c <= 0xFF ? (byte)c : (byte)'?'));
            result = signed + (result << 4);
            if ((result & 0xF0000000L) != 0)
                result = (result ^ ((result & 0xF0000000L) >> 24)) & 0x0FFFFFFFL;
        }
        return unchecked((uint)result);
    }

    private static void SkipUnicode(BinaryReader reader, int count)
    {
        for (var i = 0; i < count; i++) ReadUnicode(reader);
    }

    /// <summary>A compressed length, then that many 16-bit characters.</summary>
    private static string ReadUnicode(BinaryReader reader)
    {
        var length = DatModels.ReadCompressed(reader);
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
