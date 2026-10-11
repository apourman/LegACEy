using System.Buffers.Binary;
using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class PortalDatTests
{
    private const uint BaseId = 0x06000001;
    private const uint PaletteId = 0x04000001;

    [Theory]
    [InlineData(21)]
    [InlineData(20)]
    [InlineData(41)]
    [InlineData(101)]
    public void Decodes_each_supported_interface_pixel_format_from_a_small_dat_fixture(uint format)
    {
        var path = CreateFixture(format);
        try
        {
            using var dat = new PortalDat(path);
            var image = dat.ReadImage(BaseId);
            Assert.NotNull(image);
            Assert.Equal(2, image!.Width);
            Assert.Equal(1, image.Height);
            var expected = format == 20
                ? new byte[] { 30, 20, 10, 255, 3, 2, 1, 255 }
                : new byte[] { 30, 20, 10, 255, 1, 1, 0, 128 };
            Assert.Equal(expected, image.Pixels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Real_dat_images_decode_when_a_portal_path_is_provided()
    {
        var path = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (string.IsNullOrWhiteSpace(path)) return;

        using var dat = new PortalDat(path);
        Assert.NotNull(dat.ReadImage(0x06004CC2));
        for (uint id = 0x060074BF; id <= 0x060074C6; id++)
            Assert.NotNull(dat.ReadImage(id));
    }

    [Fact]
    public void Real_dat_spells_read_as_retail_words_them_when_a_portal_path_is_provided()
    {
        var path = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (string.IsNullOrWhiteSpace(path)) return;

        using var dat = new PortalDat(path);
        var spells = dat.ReadSpells();
        // The Ivory Stick's five spells, as the retail appraisal lists them, and a name with Windows-1252's apostrophe.
        Assert.Equal(("Might of the Lugians", "Increases the caster's Strength by 40 points."), (spells[2087].Name, spells[2087].Description));
        Assert.Equal(("Aura of Infected Caress", "Increases a weapon's damage value by 22 points."), (spells[2096].Name, spells[2096].Description));
        Assert.Equal(("Aura of Atlan's Alacrity", "Improves a weapon's speed by 70 points."), (spells[2116].Name, spells[2116].Description));
        Assert.Equal(("Major Coordination", "Increases the target's Coordination by 15 points. Additional spells can be layered over this."),
            (spells[2572].Name, spells[2572].Description));
        Assert.Equal(("Aura of Incantation of Heart Seeker Self", "Increases a weapon's Attack Skill modifier by 20.0 percentage points."),
            (spells[4405].Name, spells[4405].Description));
        Assert.Equal("Asheron\u2019s Benediction", spells[3810].Name);
    }

    private static string CreateFixture(uint format)
    {
        const uint blockSize = 4096;
        var files = new SortedDictionary<uint, byte[]>();
        var palette = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(palette.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(palette.AsSpan(8), 0xFF0A141E);
        BinaryPrimitives.WriteUInt32LittleEndian(palette.AsSpan(12), 0x80010203);
        files[PaletteId] = palette;

        var image = new byte[format switch { 21 => 8, 20 => 6, 41 => 2, 101 => 4, _ => throw new ArgumentOutOfRangeException(nameof(format)) } + (format is 41 or 101 ? 4 : 0) + 24];
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), format);
        var pixelLength = format switch { 21 => 8, 20 => 6, 41 => 2, _ => 4 };
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(20), pixelLength);
        if (format == 21)
            new byte[] { 30, 20, 10, 255, 3, 2, 1, 128 }.CopyTo(image, 24);
        else if (format == 20)
            new byte[] { 30, 20, 10, 3, 2, 1 }.CopyTo(image, 24);
        else if (format == 41)
            new byte[] { 0, 1 }.CopyTo(image, 24);
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(24), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(26), 1);
        }
        if (format is 41 or 101)
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24 + pixelLength), PaletteId);
        files[BaseId] = image;

        var path = Path.Combine(Path.GetTempPath(), $"portal-fixture-{Guid.NewGuid():N}.dat");
        var directoryOffset = 0x400;
        var dataOffset = 0x2000;
        var directory = new byte[(62 * 4) + 4 + (24 * 61)];
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(62 * 4), files.Count);
        var blockOffset = dataOffset;
        var entryIndex = 0;
        var blocks = new List<(int Offset, byte[] Data)>();
        foreach (var (id, data) in files)
        {
            var entry = (62 * 4) + 4 + (entryIndex++ * 24);
            BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(entry + 4), id);
            BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(entry + 8), (uint)blockOffset);
            BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(entry + 12), data.Length);
            blocks.Add((blockOffset, data));
            blockOffset += (int)blockSize;
        }

        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(blockOffset);
            var header = new byte[40];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), blockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), (uint)directoryOffset);
            stream.Position = 0x140;
            stream.Write(header);
            WriteBlock(stream, directoryOffset, directory, blockSize);
            foreach (var (offset, data) in blocks) WriteBlock(stream, offset, data, blockSize);
        }
        return path;
    }

    private static void WriteBlock(Stream stream, int offset, byte[] data, uint blockSize)
    {
        stream.Position = offset;
        stream.Write(new byte[4]);
        stream.Write(data, 0, Math.Min(data.Length, (int)blockSize - 4));
    }
}
