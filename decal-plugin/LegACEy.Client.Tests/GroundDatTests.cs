using System.Linq;
using System.Runtime.InteropServices;
using LegACEy.Client.GameArt;
using SkiaSharp;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class GroundDatTests
{
    [Fact]
    public void Real_dat_ground_reads_arwic_as_the_prototype_did_when_a_dat_folder_is_provided()
    {
        var directory = Environment.GetEnvironmentVariable("LEGACEY_DAT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        using var portal = new PortalDat(Path.Combine(directory, "client_portal.dat"));
        using var ground = new GroundDat(portal);
        var arwic = ground.ReadLandblock(0xC6, 0xA9);

        Assert.NotNull(arwic);
        // Terrain: four types, nine road vertices, heights from the height table (2 m per index) between 22 and 52 m.
        Assert.Equal(new[] { 3, 6, 8, 9 }, arwic!.Terrain.Select(t => (t & 0x7C) >> 2).Distinct().OrderBy(t => t));
        Assert.Equal(9, arwic.Terrain.Count(t => (t & 0x3) != 0));
        Assert.Equal(22f, arwic.Height.Min());
        Assert.Equal(52f, arwic.Height.Max());
        // Buildings: 30, each an outline of at least three points. Walls are the static objects of a wall's size; trees are dropped.
        Assert.Equal(30, arwic.Footprints.Count(f => f.Building));
        Assert.All(arwic.Footprints, f => Assert.True(f.Hull.Count >= 3));
        Assert.Equal(512, ground.TerrainTexture(3)!.Width);

        var around = new List<GroundLandblock> { arwic };
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0) around.Add(ground.ReadLandblock(0xC6 + dx, 0xA9 + dy)!);
        var tile = GroundTileRenderer.Render(arwic, around, ground.TerrainTexture);
        Assert.Equal(384, tile.Width);
        Assert.Equal(384, tile.Height);

        // Set LEGACEY_GROUND_PNG to a path to write the tile there, to compare it with the mockup's ground by eye.
        var png = Environment.GetEnvironmentVariable("LEGACEY_GROUND_PNG");
        if (!string.IsNullOrWhiteSpace(png)) WritePng(png, tile);
    }

    private static void WritePng(string path, GameImage image)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        using var encoded = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, encoded.ToArray());
    }
}
