using System.IO;
using System.Text;
using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>The portal DAT's spell table: a small table built to the format, so the reader's walk through each record is pinned.</summary>
public sealed class SpellTableTests
{
    [Fact]
    public void Each_record_is_walked_past_its_fields_so_the_next_spell_reads_aligned_and_its_strings_decode_as_windows_1252()
    {
        var table = new TableWriter(3)
            .Spell(1, "Strength Self I", "Increases the caster's Strength by 10 points.", metaType: 0)
            // An enchantment record carries a duration and two degrade values; the 0x92 and 0x96 bytes are the apostrophe and the dash.
            .Spell(2, "Aura of Atlan\u0092s Alacrity", "A\u0096B", metaType: 1)
            // A portal summon carries a lifetime. Its strings end off a four-byte boundary, so the reader has to skip padding.
            .Spell(3, "Portal", string.Empty, metaType: 7)
            .Build();

        var spells = SpellTable.Parse(table);

        Assert.Equal(3, spells.Count);
        Assert.Equal(("Strength Self I", "Increases the caster's Strength by 10 points."), (spells[1].Name, spells[1].Description));
        Assert.Equal(("Aura of Atlan’s Alacrity", "A–B"), (spells[2].Name, spells[2].Description));
        Assert.Equal(("Portal", string.Empty), (spells[3].Name, spells[3].Description));
    }

    /// <summary>Writes a spell table the way the DAT stores it: the strings nibble-swapped and padded to four bytes, the records in full.</summary>
    private sealed class TableWriter
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;

        public TableWriter(int spellCount)
        {
            _writer = new BinaryWriter(_stream);
            _writer.Write(0x0E00000Eu);
            _writer.Write((ushort)spellCount);
            _writer.Write((ushort)8);
        }

        public TableWriter Spell(uint id, string name, string description, uint metaType)
        {
            _writer.Write(id);
            ObfuscatedString(name);
            ObfuscatedString(description);
            for (var i = 0; i < 11; i++) _writer.Write(0u); // school through component loss
            _writer.Write(metaType);
            _writer.Write(0u); // the meta spell id
            switch (metaType)
            {
                case 1 or 12:
                    _writer.Write(0.0);
                    _writer.Write(0f);
                    _writer.Write(0f);
                    break;
                case 7:
                    _writer.Write(0.0);
                    break;
            }
            for (var i = 0; i < 8; i++) _writer.Write(0u); // the eight formula components
            for (var i = 0; i < 3; i++) _writer.Write(0u); // the caster, target and fizzle effects
            _writer.Write(0.0); // the recovery interval
            _writer.Write(0f); // the recovery amount
            for (var i = 0; i < 3; i++) _writer.Write(0u); // display order, non-component target type and mana mod
            return this;
        }

        public byte[] Build()
        {
            _writer.Flush();
            return _stream.ToArray();
        }

        private void ObfuscatedString(string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            _writer.Write((ushort)bytes.Length);
            foreach (var b in bytes) _writer.Write((byte)((b >> 4) | (b << 4)));
            while (_stream.Position % 4 != 0) _writer.Write((byte)0);
        }
    }
}
