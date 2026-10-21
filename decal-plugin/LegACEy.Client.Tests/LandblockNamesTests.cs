using System;
using System.IO;
using System.Linq;
using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class LandblockNamesTests
{
    [Fact]
    public void The_embedded_list_has_one_valid_line_per_landblock_with_unique_ids()
    {
        using var stream = typeof(LandblockNames).Assembly.GetManifestResourceStream("landblock-names.tsv")!;
        var lines = new StreamReader(stream).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1081, lines.Length);
        Assert.All(lines, line => Assert.Equal(2, line.Split('\t').Length));
        Assert.Equal(lines.Length, lines.Select(line => line.Split('\t')[0]).Distinct().Count());
    }

    [Fact]
    public void Lines_split_on_CRLF_as_well_as_LF()
    {
        var parsed = LandblockNames.Parse("0099\tBlack Totem Temple\r\n00C9\tChamber of Scrolls|Chamber of Air\r\n");
        Assert.Equal(new[] { "Black Totem Temple" }, parsed[0x0099]);
        Assert.Equal(new[] { "Chamber of Scrolls", "Chamber of Air" }, parsed[0x00C9]);
    }

    [Fact]
    public void A_shared_landblock_returns_every_name_and_an_unlisted_one_returns_none()
    {
        Assert.Equal(new[] { "Black Totem Temple" }, LandblockNames.Get(0x0099));
        Assert.Equal(new[] { "Chamber of Scrolls", "Chamber of Air", "Chamber of Earth", "Chamber of Water" }, LandblockNames.Get(0x00C9));
        Assert.Empty(LandblockNames.Get(0x0000));
    }
}
