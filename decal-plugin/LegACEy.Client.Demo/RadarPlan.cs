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

    public RadarPlanLevel(IndoorLevel level, IReadOnlyList<(GroundPoint At, bool Up)> stairs)
    {
        _level = level;
        Stairs = stairs;
        // Halfway between its lowest and highest cell: the height the ladder and the dots' heights are read against.
        Height = (level.Cells.Max(cell => cell.Height) + level.Cells.Min(cell => cell.Height)) / 2;
    }

    /// <summary>The level's height in landblock metres.</summary>
    public float Height { get; }

    /// <summary>The doors from this level to another level of the place, where a stair mark goes: the door's centre, and whether it leads up.</summary>
    public IReadOnlyList<(GroundPoint At, bool Up)> Stairs { get; }

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
    // The draw order is made again only when the solid level or its peek changes: it is kept for the one last asked for.
    private int _orderLevel = -1;
    private bool _orderPeek;
    private IReadOnlyList<RadarPlanDraw> _order = Array.Empty<RadarPlanDraw>();

    /// <param name="landblock">The plan of the player's landblock, read from the DATs.</param>
    /// <param name="cell">The player's cell: the low word of its full id.</param>
    public RadarPlan(IndoorPlan landblock, ushort cell)
    {
        _place = landblock.Place(cell);
        Levels = _place.Levels.Select((level, index) => new RadarPlanLevel(level, StairsOf(level, index))).ToArray();
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
    /// The levels in drawing order for a player on <paramref name="playerLevel"/>: those below the solid level, then those above, then the solid
    /// one last so it sits on the others. The solid level is the peeked one when <paramref name="peekLevel"/> is 0 or more (drawn teal), else
    /// the player's own. The same list while the solid level and its peek stay as they are.
    /// </summary>
    public IReadOnlyList<RadarPlanDraw> DrawOrder(int playerLevel, int peekLevel = -1)
    {
        var peek = peekLevel >= 0;
        var solid = peek ? peekLevel : playerLevel;
        if (solid == _orderLevel && peek == _orderPeek) return _order;
        var draws = new List<RadarPlanDraw>(Levels.Count);
        if (solid >= 0)
        {
            for (var level = solid + 1; level < Levels.Count; level++) draws.Add(new RadarPlanDraw(Levels[level], false));
            for (var level = 0; level < solid; level++) draws.Add(new RadarPlanDraw(Levels[level], false));
            draws.Add(new RadarPlanDraw(Levels[solid], true, peek));
        }
        _orderLevel = solid;
        _orderPeek = peek;
        _order = draws;
        return draws;
    }

    // A door from a level to another level of the place, as a stair mark: up when the other level is higher (a lower index).
    private IReadOnlyList<(GroundPoint At, bool Up)> StairsOf(IndoorLevel level, int index)
    {
        var stairs = new List<(GroundPoint At, bool Up)>();
        foreach (var cell in level.Cells)
            foreach (var (centre, beyond) in cell.Doors)
            {
                // A door out of the place has no level in it (-1), and a door within the level is no stair.
                var other = _place.LevelOf(beyond);
                if (other >= 0 && other != index) stairs.Add((new GroundPoint(centre.X, centre.Y), other < index));
            }
        return stairs;
    }
}
