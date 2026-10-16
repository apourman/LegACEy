using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LegACEy.Client.GameArt;

/// <summary>A point on the ground, in metres.</summary>
public readonly struct GroundPoint
{
    public GroundPoint(float x, float y) { X = x; Y = y; }
    public float X { get; }
    public float Y { get; }
}

/// <summary>
/// A building or wall seen from above: the convex hull of its model's vertices, counter-clockwise, in metres from the
/// landblock's south-west corner (so a footprint may reach past the landblock's edge).
/// </summary>
public sealed class GroundFootprint
{
    public GroundFootprint(bool building, IReadOnlyList<GroundPoint> hull) { Building = building; Hull = hull; }
    public bool Building { get; }
    public IReadOnlyList<GroundPoint> Hull { get; }
}

/// <summary>One landblock's ground: its 9 × 9 vertices and the footprints of its buildings and walls.</summary>
public sealed class GroundLandblock
{
    public GroundLandblock(int x, int y, ushort[] terrain, float[] height, IReadOnlyList<GroundFootprint> footprints)
    {
        X = x;
        Y = y;
        Terrain = terrain;
        Height = height;
        Footprints = footprints;
    }

    /// <summary>The landblock's column and row: the 0xXX and 0xYY of its id.</summary>
    public int X { get; }
    public int Y { get; }

    /// <summary>The vertex words, index x * 9 + y: the terrain type in bits 2 to 6, the road in bits 0 and 1 (CellLandblock's masks).</summary>
    public ushort[] Terrain { get; }

    /// <summary>Each vertex's height in metres, in the same order as <see cref="Terrain"/>.</summary>
    public float[] Height { get; }

    public IReadOnlyList<GroundFootprint> Footprints { get; }
}

/// <summary>Where the ground comes from. <see cref="GroundDat"/> reads the DATs; tests give landblocks by hand.</summary>
public interface IGroundSource
{
    /// <summary>The landblock at column x and row y (each 0 to 254), or null where the DATs have none.</summary>
    GroundLandblock? ReadLandblock(int x, int y);

    /// <summary>A terrain type's texture (straight-alpha BGRA, repeating once per 24 m cell), or null. The tile reads only its colour.</summary>
    GameImage? TerrainTexture(uint terrainType);
}

/// <summary>
/// The ground's DATs, read as ACE.DatLoader reads them: client_cell_1.dat for each landblock's vertices (CellLandblock) and its
/// buildings and walls (LandblockInfo), and client_portal.dat for the height table and terrain textures (RegionDesc) and for the
/// models the footprints come from. client_highres.dat supplies a terrain texture where it is larger. GroundDat opens its own
/// copies of the portal, cell and high-res DATs beside the given portal, so its reads never wait on the caller's portal.
/// </summary>
public sealed class GroundDat : IGroundSource, IDisposable
{
    private const uint RegionId = 0x13000000;
    private const int MaxModelDepth = 3;
    private const float MinScenery = 6, MaxScenery = 400; // m²: smaller is a tree or a plant, larger is a bigger piece of scenery

    private readonly PortalDat _portal;
    private readonly PortalDat _cell;
    private readonly PortalDat? _highRes;
    private readonly float[] _heights;
    private readonly Dictionary<uint, uint> _surfaces;
    private readonly Dictionary<uint, GameImage?> _textures = new();
    private readonly Dictionary<(uint Model, int Depth), Vec3[]> _modelPoints = new(); // a setup cut off at a depth has fewer points than at none

    public GroundDat(PortalDat portal)
    {
        var directory = Path.GetDirectoryName(portal.Path)!;
        _portal = new PortalDat(portal.Path);
        PortalDat? cell = null;
        PortalDat? highRes = null;
        try
        {
            cell = new PortalDat(Path.Combine(directory, "client_cell_1.dat"));
            var highResPath = Path.Combine(directory, "client_highres.dat");
            highRes = File.Exists(highResPath) ? new PortalDat(highResPath) : null;
            var region = _portal.ReadFile(RegionId) ?? throw new InvalidDataException("RegionDesc 0x13000000 is not in portal.dat.");
            (_heights, _surfaces) = ReadRegion(region);
        }
        catch
        {
            // The constructor failed, so there is no GroundDat to dispose: close the DATs it opened.
            _portal.Dispose();
            cell?.Dispose();
            highRes?.Dispose();
            throw;
        }
        _cell = cell;
        _highRes = highRes;
    }

    public GroundLandblock? ReadLandblock(int x, int y)
    {
        if (x is < 0 or > 254 || y is < 0 or > 254) return null;
        var id = ((uint)x << 24) | ((uint)y << 16);
        var cell = _cell.ReadFile(id | 0xFFFF);
        if (cell == null) return null;

        var reader = new BinaryReader(new MemoryStream(cell));
        reader.ReadUInt32(); // the id
        reader.ReadUInt32(); // has objects
        var terrain = new ushort[81];
        for (var i = 0; i < terrain.Length; i++) terrain[i] = reader.ReadUInt16();
        var height = new float[81];
        for (var i = 0; i < height.Length; i++) height[i] = _heights[reader.ReadByte()];

        var info = _cell.ReadFile(id | 0xFFFE);
        return new GroundLandblock(x, y, terrain, height, info == null ? Array.Empty<GroundFootprint>() : ReadFootprints(info));
    }

    public GameImage? TerrainTexture(uint terrainType)
    {
        lock (_textures)
        {
            if (!_textures.TryGetValue(terrainType, out var image))
                _textures.Add(terrainType, image = DecodeTerrain(terrainType));
            return image;
        }
    }

    public void Dispose()
    {
        _portal.Dispose();
        _cell.Dispose();
        _highRes?.Dispose();
    }

    private GameImage? DecodeTerrain(uint terrainType)
    {
        if (!_surfaces.TryGetValue(terrainType, out var surfaceTexture)) return null;
        var surfaces = DatModels.ReadSurfaceTextureSurfaces(_portal, surfaceTexture);
        if (surfaces.Length == 0) return null;
        // The last render surface is the full size. client_highres.dat's copy wins where it has one, so the low-res isn't decoded.
        var surface = surfaces[surfaces.Length - 1];
        return (_highRes == null ? null : DatModels.ReadRenderSurface(_highRes, surface)) ?? DatModels.ReadRenderSurface(_portal, surface);
    }

    /// <summary>LandblockInfo: the static objects, then the buildings with their portals (read past, as they aren't drawn).</summary>
    private List<GroundFootprint> ReadFootprints(byte[] data)
    {
        var footprints = new List<GroundFootprint>();
        var reader = new BinaryReader(new MemoryStream(data));
        reader.ReadUInt32(); // the id
        reader.ReadUInt32(); // the number of cells

        var objects = reader.ReadUInt32();
        for (var i = 0; i < objects; i++)
        {
            var model = reader.ReadUInt32();
            var frame = DatModels.ReadFrame(reader);
            AddScenery(footprints, model, frame);
        }

        var buildings = reader.ReadUInt16();
        reader.ReadUInt16(); // pack mask: only tells whether restriction tables follow, which aren't read
        for (var i = 0; i < buildings; i++)
        {
            var model = reader.ReadUInt32();
            var frame = DatModels.ReadFrame(reader);
            reader.ReadUInt32(); // the number of leaves
            var portals = reader.ReadUInt32();
            for (var p = 0; p < portals; p++)
            {
                reader.BaseStream.Seek(6, SeekOrigin.Current); // flags, other cell, other portal
                var stabs = reader.ReadUInt16();
                reader.BaseStream.Seek(stabs * 2, SeekOrigin.Current);
                reader.BaseStream.Position = (reader.BaseStream.Position + 3) / 4 * 4;
            }
            var hull = Outline(model, frame);
            if (hull != null) footprints.Add(new GroundFootprint(true, hull));
        }
        return footprints;
    }

    private void AddScenery(List<GroundFootprint> footprints, uint model, Frame frame)
    {
        var hull = Outline(model, frame);
        if (hull == null) return;
        var area = Area(hull);
        if (area >= MinScenery && area <= MaxScenery) footprints.Add(new GroundFootprint(false, hull));
    }

    /// <summary>The convex hull of a model's vertices from above, placed by the frame; null for an id that isn't a model.</summary>
    private GroundPoint[]? Outline(uint model, Frame frame)
    {
        if ((model >> 24) is not (0x01 or 0x02)) return null;
        var points = ModelPoints(model, 0);
        if (points.Length == 0) return null;
        var placed = new GroundPoint[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var p = frame.Rotate(points[i]) + frame.Origin;
            placed[i] = new GroundPoint(p.X, p.Y);
        }
        var hull = Hull(placed);
        return hull.Length < 3 ? null : hull;
    }

    /// <summary>A model's vertices in its own space; a setup's parts are placed in its default pose, scaled first.</summary>
    private Vec3[] ModelPoints(uint model, int depth)
    {
        if (depth > MaxModelDepth) return Array.Empty<Vec3>();
        lock (_modelPoints)
            if (_modelPoints.TryGetValue((model, depth), out var known)) return known;

        var points = new List<Vec3>();
        if ((model >> 24) == 0x01)
        {
            var gfx = DatModels.ReadGfxObj(_portal, model);
            if (gfx != null) points.AddRange(gfx.Vertices.Values.Select(v => v.Position));
        }
        else
        {
            var setup = DatModels.ReadSetup(_portal, model);
            for (var part = 0; setup != null && part < setup.Parts.Length; part++)
            {
                var frame = part < setup.Frames.Length ? setup.Frames[part] : Frame.Identity;
                var scale = part < setup.Scales.Length ? setup.Scales[part] : new Vec3(1, 1, 1);
                foreach (var p in ModelPoints(setup.Parts[part], depth + 1))
                    points.Add(frame.Rotate(new Vec3(p.X * scale.X, p.Y * scale.Y, p.Z * scale.Z)) + frame.Origin);
            }
        }

        var result = points.ToArray();
        lock (_modelPoints) _modelPoints[(model, depth)] = result;
        return result;
    }

    /// <summary>Andrew's monotone chain, counter-clockwise, without collinear points.</summary>
    private static GroundPoint[] Hull(GroundPoint[] points)
    {
        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted.ToArray();
        var hull = new List<GroundPoint>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull.ToArray();
    }

    private static float Cross(GroundPoint o, GroundPoint a, GroundPoint b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

    private static float Area(GroundPoint[] polygon)
    {
        var sum = 0f;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }
        return Math.Abs(sum) / 2;
    }

    /// <summary>RegionDesc up to its terrain: the height table (metres per height index) and each terrain type's SurfaceTexture.</summary>
    private static (float[] Heights, Dictionary<uint, uint> Surfaces) ReadRegion(byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data));
        r.ReadUInt32(); // the id
        r.ReadUInt32(); // the region number
        r.ReadUInt32(); // the version
        SkipString(r); // "Dereth"

        r.BaseStream.Seek(8 * 4, SeekOrigin.Current); // the eight land definitions before the table
        var heights = new float[256];
        for (var i = 0; i < heights.Length; i++) heights[i] = r.ReadSingle();

        // GameTime: the clock's zero, year, day length, year name, times of day, days of the week and seasons
        r.ReadDouble();
        r.ReadUInt32();
        r.ReadSingle();
        r.ReadUInt32();
        SkipString(r);
        for (var n = r.ReadUInt32(); n > 0; n--)
        {
            r.ReadSingle();
            r.ReadUInt32();
            SkipString(r);
        }
        for (var n = r.ReadUInt32(); n > 0; n--) SkipString(r);
        for (var n = r.ReadUInt32(); n > 0; n--)
        {
            r.ReadUInt32();
            SkipString(r);
        }

        var parts = r.ReadUInt32();
        if ((parts & 0x10) != 0) // sky: tick sizes, then day groups of sky objects and sky times
        {
            r.ReadDouble();
            r.ReadDouble();
            Align(r);
            for (var n = r.ReadUInt32(); n > 0; n--)
            {
                r.ReadSingle();
                SkipString(r);
                for (var s = r.ReadUInt32(); s > 0; s--) r.BaseStream.Seek(9 * 4, SeekOrigin.Current);
                for (var t = r.ReadUInt32(); t > 0; t--)
                {
                    r.BaseStream.Seek(11 * 4, SeekOrigin.Current);
                    for (var o = r.ReadUInt32(); o > 0; o--) r.BaseStream.Seek(6 * 4, SeekOrigin.Current);
                }
            }
        }
        if ((parts & 0x01) != 0) // sound: ambient sounds per sound table
        {
            for (var n = r.ReadUInt32(); n > 0; n--)
            {
                r.ReadUInt32();
                for (var s = r.ReadUInt32(); s > 0; s--) r.BaseStream.Seek(5 * 4, SeekOrigin.Current);
            }
        }
        if ((parts & 0x02) != 0) // scene: scene types per scene table
        {
            for (var n = r.ReadUInt32(); n > 0; n--)
            {
                r.ReadUInt32();
                for (var s = r.ReadInt32(); s > 0; s--) r.ReadUInt32();
            }
        }

        // TerrainDesc: the terrain types, then the land surface's merge maps and each type's texture
        for (var n = r.ReadUInt32(); n > 0; n--)
        {
            SkipString(r);
            r.ReadUInt32();
            for (var s = r.ReadInt32(); s > 0; s--) r.ReadUInt32();
        }
        r.ReadUInt32(); // the land surface's type, always 0
        r.ReadUInt32(); // base texture size
        for (var n = r.ReadUInt32(); n > 0; n--) r.BaseStream.Seek(2 * 4, SeekOrigin.Current); // corner terrain maps
        for (var n = r.ReadUInt32(); n > 0; n--) r.BaseStream.Seek(2 * 4, SeekOrigin.Current); // side terrain maps
        for (var n = r.ReadUInt32(); n > 0; n--) r.BaseStream.Seek(2 * 4, SeekOrigin.Current); // road maps

        var surfaces = new Dictionary<uint, uint>();
        for (var n = r.ReadUInt32(); n > 0; n--)
        {
            var terrainType = r.ReadUInt32();
            var surfaceTexture = r.ReadUInt32();
            r.BaseStream.Seek(9 * 4, SeekOrigin.Current); // the texture's tiling, brightness, saturation, hue and detail
            surfaces[terrainType] = surfaceTexture;
        }
        return (heights, surfaces);
    }

    /// <summary>A portal string (a 2-byte length, then the text), then aligned to four bytes as the DAT stores it.</summary>
    private static void SkipString(BinaryReader reader)
    {
        reader.BaseStream.Seek(reader.ReadUInt16(), SeekOrigin.Current);
        Align(reader);
    }

    private static void Align(BinaryReader reader) => reader.BaseStream.Position = (reader.BaseStream.Position + 3) / 4 * 4;
}
