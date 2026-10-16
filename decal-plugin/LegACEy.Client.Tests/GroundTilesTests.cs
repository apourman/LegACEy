using System.Threading;
using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class GroundTilesTests
{
    private const ushort Arwic = 0xC6A9;
    private const int Green = 3, Red = 6, Road = 0x20;

    [Fact]
    public void A_hand_made_landblock_puts_its_road_terrain_seam_and_building_where_they_belong()
    {
        // The west half is green, the east red; a road runs along the row of vertices at y = 48 m; a 20 m building sits at 100 to 120 m.
        var terrain = new ushort[81];
        for (var x = 0; x < 9; x++)
            for (var y = 0; y < 9; y++)
                terrain[(x * 9) + y] = (ushort)(((x <= 3 ? Green : Red) << 2) | (y == 2 ? 1 : 0));
        var building = new GroundFootprint(true, new[] { new Vec2(100, 100), new Vec2(120, 100), new Vec2(120, 120), new Vec2(100, 120) });
        var block = new GroundLandblock(0xC6, 0xA9, terrain, new float[81], new[] { building });

        var tile = GroundTileRenderer.Render(block, new[] { block }, Texture);

        Assert.Equal(384, tile.Width);
        // The road is blue, along the road vertices; the green and red are far from the seam.
        Assert.True(IsBlue(At(tile, 30, 48)), "road");
        Assert.True(IsGreen(At(tile, 30, 20)), "green west of the seam");
        Assert.True(IsRed(At(tile, 130, 30)), "red east of the seam");
        // The seam is the cell between the green and red vertices (72 to 96 m): blended, not one or the other.
        var seam = At(tile, 84, 30);
        Assert.True(seam.R > 40 && seam.G > 40, $"seam blends, got {seam}");
        // The building is opaque and flat, and its shadow falls east of it on the ground, not on the building.
        Assert.Equal((107f, 85f, 60f), At(tile, 110, 110));
        var shadowed = At(tile, 120.5f, 110);
        var clear = At(tile, 135, 110);
        Assert.True(Luma(shadowed) < .9 * Luma(clear), $"shadow {shadowed} against {clear}");
    }

    [Fact]
    public void Building_a_tile_runs_off_the_calling_thread()
    {
        var source = new FakeGround();
        source.Gate.Reset();
        var tiles = new GroundTiles(source);

        tiles.Follow(Arwic);

        // Follow returned while the source is still held: nothing was read on this thread.
        Assert.Null(tiles.Tile(Arwic));
        source.Gate.Set();
        Assert.True(WaitFor(() => tiles.Tile(Arwic) != null));
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, source.ReadingThreads);
    }

    [Fact]
    public void Moving_the_player_keeps_the_3_by_3_and_drops_the_landblocks_farther_off()
    {
        var tiles = new GroundTiles(new FakeGround());

        tiles.Follow(Arwic);
        Assert.True(WaitFor(() => tiles.Tile(Arwic) != null));

        tiles.Follow(0xC6AA); // one landblock north: Arwic is still around the player
        Assert.NotNull(tiles.Tile(Arwic));

        tiles.Follow(0xC0A0); // far off: Arwic is dropped at once, and the new one is built
        Assert.Null(tiles.Tile(Arwic));
        Assert.True(WaitFor(() => tiles.Tile(0xC0A0) != null));
    }

    private static GameImage? Texture(uint type) => type switch
    {
        Green => Solid(0x00, 0xC0, 0x00),
        Red => Solid(0x00, 0x00, 0xC0),
        Road => Solid(0xFF, 0x00, 0x00),
        _ => null,
    };

    private static GameImage Solid(byte b, byte g, byte r)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 0xFF;
        }
        return new GameImage(4, 4, pixels);
    }

    private static (float R, float G, float B) At(GameImage tile, float x, float y)
    {
        var column = (int)(x * 2);
        var row = (int)(384 - (y * 2));
        var i = ((row * tile.Width) + column) * 4;
        return (tile.Pixels[i + 2], tile.Pixels[i + 1], tile.Pixels[i]);
    }

    private static bool IsBlue((float R, float G, float B) c) => c.B > 150 && c.R < 60 && c.G < 60;
    private static bool IsGreen((float R, float G, float B) c) => c.G > 120 && c.R < 40;
    private static bool IsRed((float R, float G, float B) c) => c.R > 120 && c.G < 40;
    private static double Luma((float R, float G, float B) c) => c.R + c.G + c.B;

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>Flat ground of one type; reads can be held at <see cref="Gate"/> to show they happen off the caller's thread.</summary>
    private sealed class FakeGround : IGroundSource
    {
        public ManualResetEventSlim Gate { get; } = new(true);
        public List<int> ReadingThreads { get; } = new();

        public GroundLandblock? ReadLandblock(int x, int y)
        {
            lock (ReadingThreads) ReadingThreads.Add(Environment.CurrentManagedThreadId);
            Gate.Wait(TimeSpan.FromSeconds(10));
            var terrain = new ushort[81];
            Array.Fill(terrain, (ushort)(Green << 2));
            return new GroundLandblock(x, y, terrain, new float[81], Array.Empty<GroundFootprint>());
        }

        public GameImage? TerrainTexture(uint terrainType) => Texture(terrainType);
    }
}
