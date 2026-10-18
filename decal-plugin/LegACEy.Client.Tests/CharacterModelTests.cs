using System.Linq;
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
        // Narrower than tall, so the view frames a character by its height alone.
        Assert.True(2 * naked.Radius < naked.Top - naked.Bottom, $"radius {naked.Radius}");
        Assert.All(naked.Meshes, mesh => Assert.True(mesh.Texture.Width > 1, "every body surface has a decoded texture"));

        // clothing table 0x1000002E's sleeves for this setup: four arm parts swapped and recoloured
        var clothed = CharacterModel.Build(dat, Appearance(
            new[] { new PartChange(10, 0x01001222), new PartChange(11, 0x01001225), new PartChange(13, 0x01001220), new PartChange(14, 0x01001226) }));
        Assert.NotEqual(naked.TriangleCount, clothed.TriangleCount);
    }

    [Fact]
    public void Real_dat_human_holds_a_wielded_sword_in_the_right_hand()
    {
        var path = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var dat = new PortalDat(path);

        var naked = CharacterModel.Build(dat, Appearance());
        // the academy sword (setup 0x02000147) at ParentLocation.RightHand
        var sword = new CharacterAppearance(0x02000147, 0, Array.Empty<SubPalette>(), Array.Empty<TextureChange>(), Array.Empty<PartChange>());
        var armed = Appearance(held: new[] { new HeldItem(1, 1.1f, sword) });
        var holding = CharacterModel.Build(dat, armed);

        Assert.True(holding.TriangleCount > naked.TriangleCount);
        // The sword's vertices hang at the hand, beside the body and below the shoulders: none floats off above the head or past the feet.
        var swordVertices = holding.Meshes.SelectMany(mesh => mesh.Vertices).Skip(naked.Meshes.Sum(mesh => mesh.Vertices.Count)).ToList();
        Assert.NotEmpty(swordVertices);
        Assert.All(swordVertices, v => Assert.InRange(v.Z, naked.Bottom - 0.3f, naked.Top));
        Assert.InRange(swordVertices.Average(v => v.Z), 0.3f, 1.4f);
    }

    private static CharacterAppearance Appearance(PartChange[]? parts = null, HeldItem[]? held = null) =>
        new(HumanMale, SkinPalette, Array.Empty<SubPalette>(), Array.Empty<TextureChange>(), parts ?? Array.Empty<PartChange>(), held);
}
