using System;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.GameArt;

/// <summary>
/// An indoor cell as the DATs give it: its polygons in the cell's own space, its frame in the landblock, and its portals, each
/// portal's polygon and the cell beyond it (0xFFFF is outside).
/// </summary>
public sealed class IndoorCellShape
{
    public IndoorCellShape(ushort id, Frame frame, IReadOnlyDictionary<ushort, Vec3[]> polygons, IReadOnlyList<(ushort Polygon, ushort OtherCell)> portals)
    {
        Id = id;
        Frame = frame;
        Polygons = polygons;
        Portals = portals;
    }

    public ushort Id { get; }
    public Frame Frame { get; }
    public IReadOnlyDictionary<ushort, Vec3[]> Polygons { get; }
    public IReadOnlyList<(ushort Polygon, ushort OtherCell)> Portals { get; }
}

/// <summary>
/// One indoor cell in landblock metres (x east, y north, z up): its floors, its walls as bottom edges seen from above, its doors (the
/// centre of a portal polygon and the cell beyond it, whose id is 0xFFFF when the door leads outside) and its height, the lowest of its
/// floors (a stair's floors step up, so their mean would fall between two storeys).
/// </summary>
public sealed class IndoorCell
{
    public IndoorCell(ushort id, float height, IReadOnlyList<IReadOnlyList<GroundPoint>> floors, IReadOnlyList<(GroundPoint A, GroundPoint B)> walls, IReadOnlyList<(Vec3 Centre, ushort Beyond)> doors)
    {
        Id = id;
        Height = height;
        Floors = floors;
        Walls = walls;
        Doors = doors;
    }

    public ushort Id { get; }
    public float Height { get; }
    public IReadOnlyList<IReadOnlyList<GroundPoint>> Floors { get; }
    public IReadOnlyList<(GroundPoint A, GroundPoint B)> Walls { get; }
    public IReadOnlyList<(Vec3 Centre, ushort Beyond)> Doors { get; }
}

/// <summary>A level: the cells whose heights step down by no more than 2.5 m, with their floors and walls, drawn as one path each.</summary>
public sealed class IndoorLevel
{
    public IndoorLevel(IReadOnlyList<IndoorCell> cells)
    {
        Cells = cells;
        Floors = cells.SelectMany(c => c.Floors).ToList();
        Walls = cells.SelectMany(c => c.Walls).ToList();
    }

    public IReadOnlyList<IndoorCell> Cells { get; }
    public IReadOnlyList<IReadOnlyList<GroundPoint>> Floors { get; }
    public IReadOnlyList<(GroundPoint A, GroundPoint B)> Walls { get; }
}

/// <summary>
/// A landblock's indoor cells with their levels, top floor first. Pure: built from cell shapes, so tests can give hand-made cells.
/// </summary>
public sealed class IndoorPlan
{
    private const float LevelGap = 2.5f;
    private const float FloorNormalZ = 0.7f; // a polygon facing up this much is a floor
    private const float WallNormalZ = 0.3f; // one facing this flat is a wall
    private const float WallBottom = 0.2f; // a wall's vertices this close to its lowest are its bottom edge
    private const float MinWall = 0.3f; // a shorter bottom edge is a sliver

    private readonly Dictionary<ushort, IndoorCell> _cells;
    private readonly Dictionary<ushort, int> _levelOf = new();

    public IndoorPlan(int x, int y, IReadOnlyList<IndoorCell> cells)
    {
        X = x;
        Y = y;
        Cells = cells;
        _cells = cells.ToDictionary(c => c.Id);
        Levels = ClusterLevels(cells);
        for (var level = 0; level < Levels.Count; level++)
            foreach (var cell in Levels[level].Cells) _levelOf[cell.Id] = level;
    }

    /// <summary>The landblock's column and row: the 0xXX and 0xYY of its id.</summary>
    public int X { get; }
    public int Y { get; }
    public IReadOnlyList<IndoorCell> Cells { get; }
    public IReadOnlyList<IndoorLevel> Levels { get; }

    /// <summary>The cell with this id, the low word of the cell's full id (0x0100 to 0xFFFE), or null if the plan has none.</summary>
    public IndoorCell? Cell(ushort id) => _cells.TryGetValue(id, out var cell) ? cell : null;

    /// <summary>
    /// The index in <see cref="Levels"/> of the cell's level (0 is the top), taking the cell's low word as <see cref="Cell"/> does;
    /// -1 if the plan has no such cell.
    /// </summary>
    public int LevelOf(ushort id) => _levelOf.TryGetValue(id, out var level) ? level : -1;

    /// <summary>
    /// The cells reachable through doors from a cell, as a plan of their own with their own levels, empty if the plan has no such cell.
    /// It allocates and re-clusters the levels, so call it once per cell or landblock change, not per frame.
    /// </summary>
    public IndoorPlan Place(ushort id)
    {
        var reached = new HashSet<ushort>();
        var queue = new Queue<ushort>();
        if (_cells.ContainsKey(id))
        {
            reached.Add(id);
            queue.Enqueue(id);
        }
        while (queue.Count > 0)
            foreach (var (_, beyond) in _cells[queue.Dequeue()].Doors)
                if (_cells.ContainsKey(beyond) && reached.Add(beyond)) queue.Enqueue(beyond);
        return new IndoorPlan(X, Y, Cells.Where(c => reached.Contains(c.Id)).ToList());
    }

    /// <summary>The plan of a landblock's cells: each polygon moved by its cell's frame into landblock metres, then classified.</summary>
    public static IndoorPlan Build(int x, int y, IEnumerable<IndoorCellShape> shapes) => new(x, y, shapes.Select(BuildCell).ToList());

    private static IReadOnlyList<IndoorLevel> ClusterLevels(IReadOnlyList<IndoorCell> cells)
    {
        var levels = new List<IndoorLevel>();
        var level = new List<IndoorCell>();
        foreach (var cell in cells.OrderByDescending(c => c.Height))
        {
            if (level.Count > 0 && level[level.Count - 1].Height - cell.Height > LevelGap)
            {
                levels.Add(new IndoorLevel(level));
                level = new List<IndoorCell>();
            }
            level.Add(cell);
        }
        if (level.Count > 0) levels.Add(new IndoorLevel(level));
        return levels;
    }

    private static IndoorCell BuildCell(IndoorCellShape shape)
    {
        var world = shape.Polygons.ToDictionary(p => p.Key, p => p.Value.Select(v => ToLandblock(shape.Frame, v)).ToArray());
        var portals = new HashSet<ushort>(shape.Portals.Select(p => p.Polygon));
        var doors = shape.Portals
            .Where(p => world.ContainsKey(p.Polygon))
            .Select(p => (Centre: Centre(world[p.Polygon]), Beyond: p.OtherCell))
            .ToList();

        var floors = new List<IReadOnlyList<GroundPoint>>();
        var walls = new List<(GroundPoint A, GroundPoint B)>();
        var floorHeights = new List<double>();
        foreach (var polygon in world)
        {
            var points = polygon.Value;
            if (portals.Contains(polygon.Key) || points.Length < 3) continue; // a doorway is neither a floor nor a wall
            var normalZ = NormalZ(points);
            if (normalZ > FloorNormalZ)
            {
                floors.Add(points.Select(Flat).ToArray());
                floorHeights.Add(points.Average(p => p.Z));
            }
            else if (Math.Abs(normalZ) < WallNormalZ && WallEdge(points) is { } edge)
            {
                walls.Add(edge);
            }
        }

        var height = floorHeights.Count > 0 ? (float)floorHeights.Min() : shape.Frame.Origin.Z;
        return new IndoorCell(shape.Id, height, floors, walls, doors);
    }

    /// <summary>A wall's bottom edge, seen from above: its two lowest ends, unless they are a sliver.</summary>
    private static (GroundPoint A, GroundPoint B)? WallEdge(Vec3[] points)
    {
        var lowest = points.Min(p => p.Z);
        var bottom = points.Where(p => p.Z < lowest + WallBottom).ToList();
        if (bottom.Count < 2) return null;
        var (a, b) = (bottom[0], bottom[bottom.Count - 1]);
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        if ((dx * dx) + (dy * dy) < MinWall * MinWall) return null;
        return (Flat(a), Flat(b));
    }

    /// <summary>
    /// The z of a polygon's normal, unit length, by Newell's method over all its corners, so a polygon whose first corners are in a
    /// line still has one. NaN for a degenerate polygon, which is neither floor nor wall.
    /// </summary>
    private static float NormalZ(Vec3[] points)
    {
        float nx = 0, ny = 0, nz = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }
        return nz / (float)Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
    }

    private static Vec3 Centre(Vec3[] points) => new((float)points.Average(p => p.X), (float)points.Average(p => p.Y), (float)points.Average(p => p.Z));

    private static Vec3 ToLandblock(Frame frame, Vec3 point) => frame.Rotate(point) + frame.Origin;

    private static GroundPoint Flat(Vec3 point) => new(point.X, point.Y);
}
