using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class CharacterModelTests
{
    private const uint HumanMale = 0x02000001;
    private const uint SkinPalette = 0x0400007E;

    [Fact]
    public void Real_dat_human_assembles_standing_and_takes_a_clothing_part_swap()
    {
        var path = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var dat = new PortalDat(path);

        var naked = CharacterModel.Build(dat, Appearance());
        Assert.InRange(naked.TriangleCount, 300, 2000);
        Assert.InRange(naked.Top - naked.Bottom, 1.6f, 2.0f); // a standing human, about 1.8 m
        Assert.All(naked.Meshes, mesh => Assert.True(mesh.Texture.Width > 1, "every body surface has a decoded texture"));

        // clothing table 0x1000002E's sleeves for this setup: four arm parts swapped and recoloured
        var clothed = CharacterModel.Build(dat, Appearance(
            new[] { new PartChange(10, 0x01001222), new PartChange(11, 0x01001225), new PartChange(13, 0x01001220), new PartChange(14, 0x01001226) }));
        Assert.NotEqual(naked.TriangleCount, clothed.TriangleCount);
    }

    private static CharacterAppearance Appearance(PartChange[]? parts = null) =>
        new(HumanMale, SkinPalette, Array.Empty<SubPalette>(), Array.Empty<TextureChange>(), parts ?? Array.Empty<PartChange>());
}
