using System.Collections.Generic;
using System.IO;
using System.Text;
using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// The DAT parsers behind the creature and title names, on small fixtures built to the layout ACE.DatLoader reads (EnumMapper and
/// StringTable). The hash values are ACE's SpellTable.ComputeHash, computed by its own algorithm.
/// </summary>
public sealed class CharacterNamesTests
{
    [Fact]
    public void An_enum_mapper_gives_each_key_its_name_and_a_string_table_finds_each_name_by_its_hash()
    {
        var mapper = EnumMapperFile(0x22000041, (764, "Adventurer"), (5, "Blademaster"));
        var names = new Dictionary<uint, string>();
        CharacterNames.ParseEnumMapper(mapper, names);
        Assert.Equal(new Dictionary<uint, string> { [764] = "Adventurer", [5] = "Blademaster" }, names);

        var table = StringTableFile(("Adventurer", "Adventurer of Dereth"), ("Blademaster", "Blademaster of the Blade"));
        var strings = CharacterNames.ParseStringTable(table);
        Assert.Equal("Adventurer of Dereth", strings[CharacterNames.Hash(names[764])]);
        Assert.Equal("Blademaster of the Blade", strings[CharacterNames.Hash(names[5])]);
    }

    [Fact]
    public void The_hash_is_ACEs_signed_nibble_hash_with_no_clamp()
    {
        // Golden values from ACE's SpellTable.ComputeHash (Source/ACE.DatLoader/FileTypes/SpellTable.cs, lines 32-51).
        Assert.Equal(0x0C5FB302u, CharacterNames.Hash("Adventurer"));
        Assert.Equal(0x0C718D62u, CharacterNames.Hash("Blademaster"));
        Assert.Equal(0u, CharacterNames.Hash(string.Empty));
    }

    /// <summary>An enum mapper as ACE reads it: its id, a base id, a numbering byte, a count, then each key and its one-byte-length name.</summary>
    private static byte[] EnumMapperFile(uint id, params (uint Key, string Name)[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(id);
            writer.Write(0u);
            writer.Write((byte)1);
            writer.Write((byte)entries.Length);
            foreach (var (key, name) in entries)
            {
                writer.Write(key);
                writer.Write((byte)name.Length);
                writer.Write(Encoding.ASCII.GetBytes(name));
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// A string table as ACE reads it: its id, the language, a byte, a compressed count of entries, then each entry: its hash key, the variable
    /// names and variables (16-bit counts), one string (a 32-bit count), comments (a 32-bit count, here one comment), and a byte.
    /// </summary>
    private static byte[] StringTableFile(params (string Key, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x2300000Eu);
            writer.Write(1u);
            writer.Write((byte)0);
            writer.Write((byte)entries.Length);
            foreach (var (key, text) in entries)
            {
                writer.Write(CharacterNames.Hash(key));
                writer.Write((ushort)1);
                WriteUnicode(writer, "PLAYER");
                writer.Write((ushort)0);
                writer.Write(1u);
                WriteUnicode(writer, text);
                writer.Write(1u);
                writer.Write(0x7u);
                writer.Write((byte)0);
            }
        }
        return stream.ToArray();
    }

    private static void WriteUnicode(BinaryWriter writer, string text)
    {
        writer.Write((byte)text.Length);
        foreach (var c in text) writer.Write((ushort)c);
    }
}
