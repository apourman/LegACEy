using System.Linq;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Tests;

public sealed class WaterTests
{
    private const ushort Grass = 1 << 2, Sea = 20 << 2;

    [Fact]
    public void A_landblock_without_water_has_no_triangles()
    {
        var dry = new GroundLandblock(0x97, 0x22, Enumerable.Repeat(Grass, 81).ToArray(), new float[81], Array.Empty<GroundFootprint>());
        Assert.Empty(Water.Triangles(dry));
    }

    [Fact]
    public void One_wet_vertex_draws_the_four_cells_around_it_fading_to_the_dry_shore()
    {
        var terrain = Enumerable.Repeat(Grass, 81).ToArray();
        terrain[4 * 9 + 4] = Sea;
        var height = Enumerable.Range(0, 81).Select(i => (float)i).ToArray();
        var points = Water.Triangles(new GroundLandblock(0x97, 0x22, terrain, height, Array.Empty<GroundFootprint>()));

        Assert.Equal(4 * 6, points.Length);
        var wet = Assert.Single(points.Where(p => p.Wet == 1).Select(p => p.Position).Distinct());
        // Global game metres: landblock 0x97 × 192 + 4 cells × 24, and the vertex's own height.
        Assert.Equal(new System.Numerics.Vector3(0x97 * 192 + 96, 0x22 * 192 + 96, 40), wet);
        Assert.All(points.Where(p => p.Wet == 0), p => Assert.True(p.Open <= 1 / 9f + 0.001f));
    }

    [Fact]
    public void Cells_split_along_the_same_diagonal_as_ace_physics()
    {
        // ACE's physics (LandblockStruct.ConstructPolygons) cuts south-west to north-east exactly where this cuts the other way.
        for (var lx = 0; lx < 255; lx += 17)
            for (var ly = 0; ly < 255; ly += 13)
                for (var cx = 0; cx < 8; cx++)
                    for (var cy = 0; cy < 8; cy++)
                        Assert.Equal(!PhysicsCutsSouthWest(lx, ly, cx, cy), Water.SplitsNorthWest(lx, ly, cx, cy));
    }

    [Fact]
    public void The_shaders_ship_in_the_client_and_their_waves_average_to_flat_water()
    {
        using var stream = typeof(Water).Assembly.GetManifestResourceStream("Water.hlsl");
        var source = new System.IO.StreamReader(stream!).ReadToEnd();
        Assert.All(new[] { " Vs(", " Tint(", " Light(" }, entry => Assert.Contains(entry, source));
        var (east, north, height) = Water.WaveField();
        Assert.All(new[] { east, north, height }, channel => Assert.InRange(channel.Average(), 0.49f, 0.51f));
    }

    [Fact]
    public void The_waves_centre_on_mid_grey_so_calm_water_is_left_as_retail_drew_it()
    {
        var pixels = Water.WavePixels();
        Assert.InRange(pixels.Average(), 0.48f, 0.53f);
        Assert.All(pixels, p => Assert.InRange(p, 0.3f, 1f));
    }

    [Fact]
    public void Water_command_turns_it_off_and_on_again_at_the_strength_it_had()
    {
        var half = WaterChoice.Default.Apply(" 50%");
        Assert.Equal(new WaterChoice(true, 50), half);
        var off = half!.Value.Apply("OFF");
        Assert.Equal(0, off!.Value.Strength);
        Assert.Equal(0.5f, off.Value.Apply("on")!.Value.Strength);
        Assert.Equal(off, off.Value.Apply(""));
        Assert.Equal(2f, off.Value.Apply("200")!.Value.Strength);
        Assert.Null(WaterChoice.Default.Apply("201"));
        Assert.Null(WaterChoice.Default.Apply("-5"));
        Assert.Null(WaterChoice.Default.Apply("loud"));
    }

    [Fact]
    public void Real_dats_have_open_sea_off_the_coast_and_dry_land_at_arwic_when_a_dat_folder_is_provided()
    {
        var directory = Environment.GetEnvironmentVariable("LEGACEY_DAT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        using var portal = new PortalDat(Path.Combine(directory, "client_portal.dat"));
        using var ground = new GroundDat(portal);
        var sea = Water.Triangles(ground.ReadVertices(0x10, 0x10)!);
        Assert.Equal(64 * 6, sea.Length);
        Assert.All(sea, p => Assert.Equal(1, p.Wet));
        Assert.Empty(Water.Triangles(ground.ReadVertices(0xC6, 0xA9)!));
    }

    private static bool PhysicsCutsSouthWest(int lx, int ly, int cx, int cy)
    {
        unchecked
        {
            uint seedA = (uint)(lx * 8) * 214614067u + 214614067u * (uint)cx, seedB = (uint)(lx * 8) * 1109124029u + 1109124029u * (uint)cx;
            var split = (uint)(ly * 8 + cy) * (seedA + 1813693831u) - seedB - 1369149221u;
            return !(split * 2.3283064e-10 < 0.5f);
        }
    }
}
