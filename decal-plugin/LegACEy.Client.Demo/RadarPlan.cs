using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// One level of a place as the radar draws it: its cells, and its floors and walls, each as one path in landblock metres (x east,
/// y north), so a level is filled and stroked in one call each. The paths are built the first time a frame draws them, once per level.
/// </summary>
public sealed class RadarPlanLevel
{
    private readonly IndoorLevel _level;
    private Geometry? _floors;
    private Geometry? _walls;

    public RadarPlanLevel(IndoorLevel level)
    {
        _level = level;
        Cells = level.Cells.Select(cell => cell.Id).ToArray();
    }

    /// <summary>The cells' ids, the low word of each full cell id.</summary>
    public IReadOnlyList<ushort> Cells { get; }

    /// <summary>The floors as one path. Built on first use, so a plan read for the tests makes no platform geometry.</summary>
    public Geometry Floors => _floors ??= FloorPath(_level);

    /// <summary>The walls as bottom edges, seen from above, as one path.</summary>
    public Geometry Walls => _walls ??= WallPath(_level);

    private static Geometry FloorPath(IndoorLevel level)
    {
        // Overlapping floors fill as one, as a canvas path does: a non-zero fill, which only a path geometry can be given.
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
/// The place a player is in, as the radar draws it: its levels, top floor first, and the level the player stands on. Made once for each
/// cell, since it reads the place's levels; a frame draws them as they are.
/// </summary>
public sealed class RadarPlan
{
    // A door to 0xFFFF leads outside: the place is a building, which keeps the town's ground under it.
    private const ushort Outside = 0xFFFF;

    /// <param name="landblock">The plan of the player's landblock, read from the DATs.</param>
    /// <param name="cell">The player's cell: the low word of its full id.</param>
    public RadarPlan(IndoorPlan landblock, ushort cell)
    {
        var place = landblock.Place(cell);
        Levels = place.Levels.Select(level => new RadarPlanLevel(level)).ToArray();
        PlayerLevel = place.LevelOf(cell);
        Building = place.Cells.Any(c => c.Doors.Any(door => door.Beyond == Outside));
    }

    public IReadOnlyList<RadarPlanLevel> Levels { get; }
    /// <summary>The index in <see cref="Levels"/> of the player's level, or -1 if the place has no such cell.</summary>
    public int PlayerLevel { get; }
    /// <summary>Whether a door leads outside: a building, not a dungeon.</summary>
    public bool Building { get; }
}
