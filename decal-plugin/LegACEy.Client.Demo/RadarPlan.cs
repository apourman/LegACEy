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

    public RadarPlanLevel(IndoorLevel level) => _level = level;

    /// <summary>The floors as one path. Built on first use, so the seam's tests never make platform geometry.</summary>
    public Geometry Floors => _floors ??= FloorPath(_level);

    /// <summary>The walls as bottom edges, seen from above, as one path.</summary>
    public Geometry Walls => _walls ??= WallPath(_level);

    private static Geometry FloorPath(IndoorLevel level)
    {
        // Overlapping floors fill as one, as a canvas path does. Avalonia 11.3's StreamGeometryContext has no fill rule to set, so the
        // floors are a path geometry, whose FillRule is non-zero.
        var geometry = new PathGeometry { FillRule = FillRule.NonZero };
        foreach (var floor in level.Floors)
        {
            var figure = new PathFigure { StartPoint = At(floor[0]), IsClosed = true, IsFilled = true };
            for (var i = 1; i < floor.Count; i++) figure.Segments!.Add(new LineSegment { Point = At(floor[i]) });
            geometry.Figures!.Add(figure);
        }
        return geometry;
    }

    private static Geometry WallPath(IndoorLevel level)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        foreach (var (a, b) in level.Walls)
        {
            context.BeginFigure(At(a), false);
            context.LineTo(At(b));
            context.EndFigure(false);
        }
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
    // The draw order is made again only when the player's level changes: it is kept for the level last asked for.
    private int _orderLevel = -1;
    private IReadOnlyList<RadarPlanDraw> _order = Array.Empty<RadarPlanDraw>();

    /// <param name="landblock">The plan of the player's landblock, read from the DATs.</param>
    /// <param name="cell">The player's cell: the low word of its full id.</param>
    public RadarPlan(IndoorPlan landblock, ushort cell)
    {
        _place = landblock.Place(cell);
        Levels = _place.Levels.Select(level => new RadarPlanLevel(level)).ToArray();
        Building = _place.Cells.Any(c => c.Doors.Any(door => door.Beyond == Outside));
    }

    public IReadOnlyList<RadarPlanLevel> Levels { get; }

    /// <summary>Whether a door leads outside: a building, not a dungeon.</summary>
    public bool Building { get; }

    /// <summary>Whether the cell, the low word of its full id, is in this place.</summary>
    public bool Contains(ushort cell) => _place.LevelOf(cell) >= 0;

    /// <summary>The index in <see cref="Levels"/> of the cell's level, or -1 if the cell is not in this place.</summary>
    public int LevelOf(ushort cell) => _place.LevelOf(cell);

    /// <summary>
    /// The levels in drawing order for a player on <paramref name="playerLevel"/>: those below it, then those above, then the player's own,
    /// solid last so it sits on the others. The same list while the player stays on the level.
    /// </summary>
    public IReadOnlyList<RadarPlanDraw> DrawOrder(int playerLevel)
    {
        if (playerLevel == _orderLevel) return _order;
        var draws = new List<RadarPlanDraw>(Levels.Count);
        if (playerLevel >= 0)
        {
            for (var level = playerLevel + 1; level < Levels.Count; level++) draws.Add(new RadarPlanDraw(Levels[level], false));
            for (var level = 0; level < playerLevel; level++) draws.Add(new RadarPlanDraw(Levels[level], false));
            draws.Add(new RadarPlanDraw(Levels[playerLevel], true));
        }
        _orderLevel = playerLevel;
        _order = draws;
        return draws;
    }
}
