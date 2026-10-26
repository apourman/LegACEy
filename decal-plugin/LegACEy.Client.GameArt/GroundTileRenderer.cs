using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LegACEy.Client.GameArt;

/// <summary>
/// Draws a landblock's ground as one 384 × 384 image, 2 px per metre, north up: terrain textures blended per 24 m cell with a
/// little noise at the seams, roads as bands along the edges between road vertices, hills shaded with the light from the
/// north-west, and buildings and walls as outlines with a soft shadow. Trees and plants are not drawn: GroundDat leaves out static
/// objects under 6 m² and over 400 m², and those between are drawn as walls, as the buildings are.
/// </summary>
internal static class GroundTileRenderer
{
    /// <summary>The tile's width and height: a landblock is 192 m across at 2 px per metre.</summary>
    public const int Size = 384;

    private const float PixelsPerMetre = 2f;
    private const float PixelMetres = 1 / PixelsPerMetre;
    private const float CellMetres = 24f; // a texture repeats once per cell
    private const int RoadType = 0x20;
    private const float RoadBand = 5f / 24; // half a road's width, in cells
    private const float ShadowAlpha = 90f / 255f;
    private const float OutlineHalfWidth = 0.225f; // a 0.45 m outline
    private const float HighlightHalfWidth = 0.15f; // a 0.3 m highlight
    private const float HighlightAlpha = 70f / 255f;
    private const float SceneryFillAlpha = 220f / 255f;
    private const float SceneryLineAlpha = 200f / 255f;
    private static readonly Rgb BuildingFill = new(0x6B, 0x55, 0x3C);
    private static readonly Rgb BuildingLine = new(0x2A, 0x1E, 0x12);
    private static readonly Rgb BuildingHighlight = new(0xC9, 0xA4, 0x5C);
    private static readonly Rgb SceneryFill = new(0x5A, 0x52, 0x48);
    private static readonly Rgb SceneryLine = new(0x2A, 0x24, 0x1E);
    private static readonly GroundPoint ShadowOffset = new(1.6f, -1.6f); // east and south, away from the north-west light
    private static readonly GroundPoint HighlightOffset = new(-0.5f, 0.5f);
    private static readonly (float X, float Y, float Z) LightDirection = Normalize(-1, 1, 1.6f);

    /// <summary>Renders the tile of <paramref name="centre"/>.</summary>
    /// <param name="around">The landblocks whose footprints can reach into the tile: the centre and its neighbours.</param>
    /// <param name="texture">A terrain type's texture, or null for a type with none.</param>
    public static GameImage Render(GroundLandblock centre, IReadOnlyList<GroundLandblock> around, Func<uint, GameImage?> texture)
    {
        var textures = new GameImage?[RoadType + 1];
        for (var type = 0; type < textures.Length; type++) textures[type] = texture((uint)type);

        // The scratch is about 3 MB, on the large object heap; one set is kept and reused, and a build that finds it taken makes its own.
        var scratch = Interlocked.Exchange(ref _spare, null) ?? new Scratch();
        try
        {
            return Render(centre, around, textures, scratch);
        }
        finally
        {
            _spare = scratch;
        }
    }

    private static Scratch? _spare;

    private sealed class Scratch
    {
        public readonly Rgb[] Canvas = new Rgb[Size * Size];
        public readonly float[] Shadow = new float[Size * Size];
        public readonly float[] Blur = new float[Size * Size];
    }

    private static GameImage Render(GroundLandblock centre, IReadOnlyList<GroundLandblock> around, GameImage?[] textures, Scratch scratch)
    {
        var blocks = Grid(centre, around);
        var canvas = scratch.Canvas; // every pixel is written below
        Parallel.For(0, Size, row =>
        {
            for (var col = 0; col < Size; col++) canvas[(row * Size) + col] = Ground(blocks, textures, col, row);
        });

        var footprints = Place(centre, around);
        var shadow = scratch.Shadow;
        Array.Clear(shadow, 0, shadow.Length);
        foreach (var (hull, _) in footprints)
            Visit(Shift(hull, ShadowOffset), .5f, (i, s) => shadow[i] = Math.Min(1, shadow[i] + Clamp01((s / PixelMetres) + .5f)));
        Blur(shadow, scratch.Blur);
        for (var i = 0; i < canvas.Length; i++) canvas[i] = canvas[i] * (1 - (ShadowAlpha * shadow[i]));

        // Scenery first, so a building's outline is never under a wall's fill.
        foreach (var (hull, building) in footprints.OrderBy(f => f.Building))
        {
            Fill(canvas, hull, building ? BuildingFill : SceneryFill, building ? 1 : SceneryFillAlpha);
            Stroke(canvas, hull, OutlineHalfWidth, building ? BuildingLine : SceneryLine, building ? 1 : SceneryLineAlpha);
            if (building) Stroke(canvas, Shift(hull, HighlightOffset), HighlightHalfWidth, BuildingHighlight, HighlightAlpha);
        }
        return ToImage(canvas);
    }

    /// <summary>The landblocks by their offset from the centre, in landblocks; the centre is at (0, 0).</summary>
    private static Dictionary<(int X, int Y), GroundLandblock> Grid(GroundLandblock centre, IReadOnlyList<GroundLandblock> around)
    {
        var grid = new Dictionary<(int X, int Y), GroundLandblock> { [(0, 0)] = centre };
        foreach (var block in around) grid[(block.X - centre.X, block.Y - centre.Y)] = block;
        return grid;
    }

    private static Rgb Ground(IReadOnlyDictionary<(int X, int Y), GroundLandblock> blocks, GameImage?[] textures, int col, int row)
    {
        var x = (col + .5f) * PixelMetres;
        var y = (Size - row - .5f) * PixelMetres;
        var gx = x / CellMetres;
        var gy = y / CellMetres;
        var vx = (int)gx;
        var vy = (int)gy;
        var fx = gx - vx;
        var fy = gy - vy;
        var c00 = Vertex(blocks, vx, vy);
        var c10 = Vertex(blocks, vx + 1, vy);
        var c01 = Vertex(blocks, vx, vy + 1);
        var c11 = Vertex(blocks, vx + 1, vy + 1);

        // Blend the four corners' textures, with noise so the seams look painted rather than bilinear.
        var j1 = (Noise(x / 3.5f, y / 3.5f) - .5f) * .55f;
        var j2 = (Noise((x / 3.5f) + 40, (y / 3.5f) + 17) - .5f) * .55f;
        var sx = Smooth(Clamp01(fx + j1));
        var sy = Smooth(Clamp01(fy + j2));
        var colour = (Sample(textures, c00.Type, x, y) * ((1 - sx) * (1 - sy)))
                   + (Sample(textures, c10.Type, x, y) * (sx * (1 - sy)))
                   + (Sample(textures, c01.Type, x, y) * ((1 - sx) * sy))
                   + (Sample(textures, c11.Type, x, y) * (sx * sy));

        // Roads: a band along each edge between two road vertices, and a disc at a lone road vertex. Distances are in cells.
        var dist = 9f;
        var r00 = c00.Road != 0;
        var r10 = c10.Road != 0;
        var r01 = c01.Road != 0;
        var r11 = c11.Road != 0;
        void Edge(bool a, bool b, float ax, float ay, float bx, float by)
        {
            if (!a || !b) return;
            var dx = bx - ax;
            var dy = by - ay;
            var t = Clamp01((((fx - ax) * dx) + ((fy - ay) * dy)) / ((dx * dx) + (dy * dy)));
            var px = fx - ax - (t * dx);
            var py = fy - ay - (t * dy);
            dist = Math.Min(dist, (float)Math.Sqrt((px * px) + (py * py)));
        }
        void Disc(bool on, float ax, float ay)
        {
            if (on) dist = Math.Min(dist, (float)Math.Sqrt(((fx - ax) * (fx - ax)) + ((fy - ay) * (fy - ay))) * 1.25f);
        }
        Edge(r00, r10, 0, 0, 1, 0);
        Edge(r01, r11, 0, 1, 1, 1);
        Edge(r00, r01, 0, 0, 0, 1);
        Edge(r10, r11, 1, 0, 1, 1);
        if (r00 && r11 && !r10 && !r01) Edge(r00, r11, 0, 0, 1, 1);
        if (r10 && r01 && !r00 && !r11) Edge(r10, r01, 1, 0, 0, 1);
        Disc(r00, 0, 0);
        Disc(r10, 1, 0);
        Disc(r01, 0, 1);
        Disc(r11, 1, 1);
        var edge = RoadBand + ((Noise(x / 2, y / 2) - .5f) * .06f);
        if (dist < edge)
        {
            var k = Clamp01((edge - dist) / .04f);
            colour = (colour * (1 - k)) + (Sample(textures, RoadType, x, y) * k);
        }

        // Hills: shade by the slope against the light.
        var hx = Height(blocks, x + 1.5f, y) - Height(blocks, x - 1.5f, y);
        var hy = Height(blocks, x, y + 1.5f) - Height(blocks, x, y - 1.5f);
        var normal = Normalize(-hx / 3, -hy / 3, 1);
        var lit = Clamp01((normal.X * LightDirection.X) + (normal.Y * LightDirection.Y) + (normal.Z * LightDirection.Z));
        return colour * (.55f + (.6f * lit));
    }

    /// <summary>
    /// A vertex of the landblock grid. A vertex past the centre's edge is the neighbour's vertex on the shared edge, so the slope
    /// is the same on both sides of the edge; where that neighbour is not in the grid, the centre's own edge repeats.
    /// </summary>
    private static (int Type, int Road, float Height) Vertex(IReadOnlyDictionary<(int X, int Y), GroundLandblock> blocks, int vx, int vy)
    {
        var dx = vx < 0 ? -1 : vx > 8 ? 1 : 0;
        var dy = vy < 0 ? -1 : vy > 8 ? 1 : 0;
        if (!blocks.TryGetValue((dx, dy), out var block))
        {
            dx = 0;
            dy = 0;
            block = blocks[(0, 0)];
        }
        var k = (Math.Min(Math.Max(vx - (8 * dx), 0), 8) * 9) + Math.Min(Math.Max(vy - (8 * dy), 0), 8);
        var word = block.Terrain[k];
        return ((word & 0x7C) >> 2, word & 0x3, block.Height[k]);
    }

    private static float Height(IReadOnlyDictionary<(int X, int Y), GroundLandblock> blocks, float x, float y)
    {
        var gx = x / CellMetres;
        var gy = y / CellMetres;
        var vx = (int)gx;
        var vy = (int)gy;
        var fx = gx - vx;
        var fy = gy - vy;
        var a = Vertex(blocks, vx, vy).Height;
        var b = Vertex(blocks, vx + 1, vy).Height;
        var c = Vertex(blocks, vx, vy + 1).Height;
        var d = Vertex(blocks, vx + 1, vy + 1).Height;
        return (((a * (1 - fx)) + (b * fx)) * (1 - fy)) + (((c * (1 - fx)) + (d * fx)) * fy);
    }

    /// <summary>A terrain texture at a point, repeating once per cell with v running south across it.</summary>
    private static Rgb Sample(GameImage?[] textures, int type, float x, float y)
    {
        var texture = textures[type];
        if (texture == null) return new Rgb(90, 90, 90);
        var u = x / CellMetres * texture.Width;
        var v = (1 - (y / CellMetres % 1)) * texture.Height;
        var px = Wrap((int)u, texture.Width);
        var py = Wrap((int)v, texture.Height);
        var i = ((py * texture.Width) + px) * 4;
        return new Rgb(texture.Pixels[i + 2], texture.Pixels[i + 1], texture.Pixels[i]);
    }

    private static int Wrap(int n, int size) => ((n % size) + size) % size;

    private static float Smooth(float t) => t * t * (3 - (2 * t));

    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static float Noise(float x, float y)
    {
        var xi = (int)Math.Floor(x);
        var yi = (int)Math.Floor(y);
        var fx = x - xi;
        var fy = y - yi;
        var sx = Smooth(fx);
        var sy = Smooth(fy);
        return (((Hash(xi, yi) * (1 - sx)) + (Hash(xi + 1, yi) * sx)) * (1 - sy))
             + (((Hash(xi, yi + 1) * (1 - sx)) + (Hash(xi + 1, yi + 1) * sx)) * sy);
    }

    private static float Hash(int x, int y)
    {
        var h = unchecked((uint)((x * 374761393) + (y * 668265263)));
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
    }

    private static (float X, float Y, float Z) Normalize(float x, float y, float z)
    {
        var length = (float)Math.Sqrt((x * x) + (y * y) + (z * z));
        return (x / length, y / length, z / length);
    }

    /// <summary>Every footprint of the nearby landblocks, moved into the centre's metres.</summary>
    private static List<(GroundPoint[] Hull, bool Building)> Place(GroundLandblock centre, IReadOnlyList<GroundLandblock> around)
    {
        var placed = new List<(GroundPoint[], bool)>();
        foreach (var block in around)
        {
            var offset = new GroundPoint((block.X - centre.X) * 192f, (block.Y - centre.Y) * 192f);
            foreach (var footprint in block.Footprints)
                placed.Add((Shift(footprint.Hull, offset), footprint.Building));
        }
        return placed;
    }

    private static GroundPoint[] Shift(IReadOnlyList<GroundPoint> hull, GroundPoint by) => hull.Select(p => new GroundPoint(p.X + by.X, p.Y + by.Y)).ToArray();

    /// <summary>
    /// Calls <paramref name="visit"/> with each pixel near the hull and its signed distance in metres from the hull's edges
    /// (positive inside a counter-clockwise hull). Pixels farther than <paramref name="reach"/> from it are not visited.
    /// </summary>
    private static void Visit(GroundPoint[] hull, float reach, Action<int, float> visit)
    {
        var minX = hull.Min(p => p.X) - reach;
        var maxX = hull.Max(p => p.X) + reach;
        var minY = hull.Min(p => p.Y) - reach;
        var maxY = hull.Max(p => p.Y) + reach;
        var col0 = Math.Max(0, (int)Math.Floor(minX / PixelMetres));
        var col1 = Math.Min(Size - 1, (int)Math.Ceiling(maxX / PixelMetres));
        var row0 = Math.Max(0, (int)Math.Floor(Size - (maxY / PixelMetres)));
        var row1 = Math.Min(Size - 1, (int)Math.Ceiling(Size - (minY / PixelMetres)));
        for (var row = row0; row <= row1; row++)
        {
            var y = (Size - row - .5f) * PixelMetres;
            for (var col = col0; col <= col1; col++)
                visit((row * Size) + col, Signed(hull, (col + .5f) * PixelMetres, y));
        }
    }

    private static float Signed(GroundPoint[] hull, float x, float y)
    {
        var least = float.MaxValue;
        for (var i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var length = (float)Math.Sqrt((ex * ex) + (ey * ey));
            if (length == 0) continue;
            least = Math.Min(least, ((ex * (y - a.Y)) - (ey * (x - a.X))) / length);
        }
        return least;
    }

    private static void Fill(Rgb[] canvas, GroundPoint[] hull, Rgb colour, float alpha) =>
        Visit(hull, PixelMetres, (i, s) => canvas[i] = Mix(canvas[i], colour, alpha * Clamp01((s / PixelMetres) + .5f)));

    private static void Stroke(Rgb[] canvas, GroundPoint[] hull, float halfWidth, Rgb colour, float alpha) =>
        Visit(hull, halfWidth + PixelMetres, (i, s) =>
            canvas[i] = Mix(canvas[i], colour, alpha * Clamp01(((halfWidth - Math.Abs(s)) / PixelMetres) + .5f)));

    /// <summary>Three box blurs of radius 3 px: close to the prototype's Gaussian shadow edge.</summary>
    private static void Blur(float[] mask, float[] scratch)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            BoxBlur(mask, scratch, 1);
            BoxBlur(scratch, mask, Size);
        }
    }

    /// <summary>One box blur of radius 3 px along a stride: 1 across a row, <see cref="Size"/> down a column. Past the edge counts as zero.</summary>
    private static void BoxBlur(float[] from, float[] to, int stride)
    {
        const int radius = 3;
        for (var i = 0; i < from.Length; i++)
        {
            var along = stride == 1 ? i % Size : i / Size;
            float sum = 0;
            for (var d = -radius; d <= radius; d++)
                if (along + d >= 0 && along + d < Size) sum += from[i + (d * stride)];
            to[i] = sum / (2 * radius + 1);
        }
    }

    private static Rgb Mix(Rgb from, Rgb to, float t) => (from * (1 - t)) + (to * t);

    private static GameImage ToImage(Rgb[] canvas)
    {
        var pixels = new byte[canvas.Length * 4];
        for (var i = 0; i < canvas.Length; i++)
        {
            pixels[i * 4] = ToByte(canvas[i].B);
            pixels[(i * 4) + 1] = ToByte(canvas[i].G);
            pixels[(i * 4) + 2] = ToByte(canvas[i].R);
            pixels[(i * 4) + 3] = 0xFF;
        }
        return new GameImage(Size, Size, pixels);
    }

    private static byte ToByte(float v) => (byte)Math.Min(255, Math.Max(0, (int)(v + .5f)));

    /// <summary>A colour in 0 to 255 per channel, so the arithmetic above stays in floats until the last step.</summary>
    private readonly struct Rgb
    {
        public Rgb(float r, float g, float b) { R = r; G = g; B = b; }
        public float R { get; }
        public float G { get; }
        public float B { get; }
        public static Rgb operator +(Rgb a, Rgb b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
        public static Rgb operator *(Rgb a, float k) => new(a.R * k, a.G * k, a.B * k);
    }
}
