using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// One level of a place as the radar draws it: its floors and walls, each as one path in landblock metres (x east, y north), so a level is
/// filled and stroked in one call each. Each path is built the first time a frame draws it, then kept with the level.
/// </summary>
public sealed class RadarPlanLevel
{
    private readonly IndoorLevel _level;
    private Geometry? _floors;
    private Geometry? _walls;

    public RadarPlanLevel(IndoorLevel level)
    {
        _level = level;
        // Halfway between its lowest and highest cell: the height the ladder and the dots' heights are read against.
        Height = (level.Cells.Max(cell => cell.Height) + level.Cells.Min(cell => cell.Height)) / 2;
    }

    /// <summary>The level's height in landblock metres.</summary>
    public float Height { get; }

    /// <summary>A spot on this level: in the cell whose floor it is over, at that floor's height; null when it is over none of the level's floors.</summary>
    public RadarPosition? On(RadarPosition at)
    {
        foreach (var cell in _level.Cells)
            if (cell.Floors.Any(floor => Inside(floor, at.X, at.Y)))
                return at with { Cell = (at.Cell & 0xFFFF0000) | cell.Id, Z = cell.Height };
        return null;
    }

    private static bool Inside(IReadOnlyList<GroundPoint> polygon, float x, float y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].Y > y) != (polygon[j].Y > y) &&
                x < (polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>The floors as one path. Built on first use, so the seam's tests never make platform geometry.</summary>
    public Geometry Floors => _floors ??= FloorPath(_level);

    /// <summary>The walls as bottom edges, seen from above, as one path.</summary>
    public Geometry Walls => _walls ??= WallPath(_level);

    // Overlapping floors fill as one, as a canvas path does.
    private static Geometry FloorPath(IndoorLevel level) => Path(context =>
    {
        context.SetFillRule(FillRule.NonZero);
        foreach (var floor in level.Floors)
        {
            context.BeginFigure(At(floor[0]), true);
            for (var i = 1; i < floor.Count; i++) context.LineTo(At(floor[i]));
            context.EndFigure(true);
        }
    });

    private static Geometry WallPath(IndoorLevel level) => Path(context =>
    {
        foreach (var (a, b) in level.Walls)
        {
            context.BeginFigure(At(a), false);
            context.LineTo(At(b));
            context.EndFigure(false);
        }
    });

    private static Geometry Path(Action<StreamGeometryContext> draw)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open()) draw(context);
        return geometry;
    }

    private static Avalonia.Point At(GroundPoint point) => new(point.X, point.Y);
}

/// <summary>
/// The place a player is in, as the radar draws it: its levels, top floor first. It is made once for a place and kept while the player's
/// cell stays in it, so its levels and their paths are not made again on each cell change. Only the player's level changes with the cell.
/// </summary>
public sealed class RadarPlan
{
    // A door to 0xFFFF leads outside: the place is a building, which keeps the town's ground under it.
    private const ushort Outside = 0xFFFF;

    private readonly IndoorPlan _place;

    /// <param name="landblock">The plan of the player's landblock, read from the DATs.</param>
    /// <param name="cell">The player's cell: the low word of its full id.</param>
    public RadarPlan(IndoorPlan landblock, ushort cell)
    {
        _place = landblock.Place(cell);
        Levels = _place.Levels.Select(level => new RadarPlanLevel(level)).ToArray();
        // The levels run top first, so the lowest level with a door outside is the largest index among the cells with one.
        Ground = _place.Cells.Where(c => c.Doors.Any(door => door.Beyond == Outside))
            .Select(c => _place.LevelOf(c.Id)).DefaultIfEmpty(-1).Max();
    }

    public IReadOnlyList<RadarPlanLevel> Levels { get; }

    /// <summary>The index in <see cref="Levels"/> of a building's ground floor, the lowest level with a door outside; -1 in a dungeon.</summary>
    public int Ground { get; }

    /// <summary>Whether a door leads outside: a building, not a dungeon.</summary>
    public bool Building => Ground >= 0;

    /// <summary>
    /// A level's name in a building, as the plaque gives it: Ground; Upstairs for the level above it, then Floor n for the levels above that;
    /// Cellar for the level below it, then Cellar n.
    /// </summary>
    public string FloorName(int level)
    {
        if (level < Ground) return Ground - level == 1 ? "Upstairs" : $"Floor {Ground - level + 1}";
        if (level == Ground) return "Ground";
        return level - Ground == 1 ? "Cellar" : $"Cellar {level - Ground}";
    }

    /// <summary>The index in <see cref="Levels"/> of the cell's level, or -1 if the cell is not in this place.</summary>
    public int LevelOf(ushort cell) => _place.LevelOf(cell);

    /// <summary>
    /// The index in <see cref="Levels"/> of the level nearest a height in landblock metres, or -1 when the place has no levels. A dot's
    /// level is read from its height this way.
    /// </summary>
    public int LevelOfZ(float z)
    {
        var nearest = -1;
        for (var level = 0; level < Levels.Count; level++)
            if (nearest < 0 || Math.Abs(Levels[level].Height - z) < Math.Abs(Levels[nearest].Height - z)) nearest = level;
        return nearest;
    }

    /// <summary>
    /// Where a ping lands for a radar showing <paramref name="playerLevel"/> and <paramref name="peekLevel"/>: on the level drawn on top at
    /// the spot (the solid level, then the faint ones over the others), in the cell under it, at its floor. Over no floor, it keeps its cell
    /// and takes the solid level's height.
    /// </summary>
    public RadarPosition Pin(RadarPosition at, int playerLevel, int peekLevel = -1)
    {
        var draws = DrawOrder(playerLevel, peekLevel);
        for (var i = draws.Count - 1; i >= 0; i--)
            if (draws[i].Level.On(at) is { } on) return on;
        return draws.Count == 0 ? at : at with { Z = draws[draws.Count - 1].Level.Height };
    }

    /// <summary>
    /// The levels in drawing order for a player on <paramref name="playerLevel"/>: those below the solid level, then those above, then the solid
    /// one last so it sits on the others. The solid level is the peeked one when <paramref name="peekLevel"/> is 0 or more (drawn teal), else
    /// the player's own. While a level is peeked the player's own is left out, so the peeked one is the only solid level.
    /// </summary>
    public IReadOnlyList<RadarPlanDraw> DrawOrder(int playerLevel, int peekLevel = -1)
    {
        var peeking = peekLevel >= 0;
        var solid = peeking ? peekLevel : playerLevel;
        var draws = new List<RadarPlanDraw>(Levels.Count);
        if (solid >= 0)
        {
            for (var level = solid + 1; level < Levels.Count; level++)
                if (level != playerLevel) draws.Add(new RadarPlanDraw(Levels[level], false));
            for (var level = 0; level < solid; level++)
                if (level != playerLevel) draws.Add(new RadarPlanDraw(Levels[level], false));
            draws.Add(new RadarPlanDraw(Levels[solid], true, peeking));
        }
        return draws;
    }
}
