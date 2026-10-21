using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Dereth radar's settings, kept per character: whether the map is drawn (the land outdoors, the floor plan indoors),
/// north-up or heading-up, the range rings, and the zoom in metres outdoors and indoors.
/// </summary>
public sealed record RadarOptions(bool Map = true, bool NorthUp = false, bool Rings = false, int Zoom = RadarZoom.Default, int IndoorZoom = RadarZoom.IndoorDefault)
{
    // The bits of the flags kept in the settings store's X, the indoor zoom above them; the zoom is its Y.
    private const int MapBit = 1, NorthUpBit = 2, RingsBit = 4, IndoorZoomShift = 8;

    /// <summary>The flags and the zoom, as two numbers the per-character settings store keeps.</summary>
    public (int Flags, int Zoom) Encode() =>
        ((Map ? MapBit : 0) | (NorthUp ? NorthUpBit : 0) | (Rings ? RingsBit : 0) | (IndoorZoom << IndoorZoomShift), Zoom);

    public static RadarOptions Decode(int flags, int zoom)
    {
        // Flags saved before the indoor zoom existed carry none: the default stands in.
        var indoor = (flags >> IndoorZoomShift) & 0xFF;
        return new((flags & MapBit) != 0, (flags & NorthUpBit) != 0, (flags & RingsBit) != 0, RadarZoom.Clamp(zoom),
            indoor == 0 ? RadarZoom.IndoorDefault : RadarZoom.ClampIndoor(indoor));
    }
}

/// <summary>
/// The radar's zoom: the range in metres from the centre to its rim. Outdoors it is 25 to 150, 70 by default; indoors 12 to 150, 40 by
/// default, kept apart.
/// </summary>
public static class RadarZoom
{
    public const int Min = 25, Max = 150, Default = 70;
    public const int IndoorMin = 12, IndoorDefault = 40;
    // The zoom options step through these.
    public static readonly IReadOnlyList<int> Presets = new[] { 25, 40, 70, 110, 150 };
    public static readonly IReadOnlyList<int> IndoorPresets = new[] { 12, 25, 40, 70, 110, 150 };
    // Each wheel notch changes the range by this factor.
    private const double Notch = 1.15;

    public static int Clamp(int metres) => Within(metres, Min);

    public static int ClampIndoor(int metres) => Within(metres, IndoorMin);

    /// <summary>The range after the wheel moves <paramref name="notches"/> notches: up (positive) zooms in, a shorter range.</summary>
    public static int Wheel(int range, int notches, bool indoors = false) =>
        Within((int)Math.Round(range * Math.Pow(Notch, -notches), MidpointRounding.AwayFromZero), indoors ? IndoorMin : Min);

    /// <summary>The next preset above the range, or the smallest after the largest.</summary>
    public static int Next(int range, bool indoors = false)
    {
        var presets = indoors ? IndoorPresets : Presets;
        foreach (var preset in presets)
            if (preset > range) return preset;
        return presets[0];
    }

    private static int Within(int metres, int min) => Math.Max(min, Math.Min(Max, metres));
}

/// <summary>A dot on the radar: an object in retail's radar colour, where it sits on the scope, and its name, range and bearing.</summary>
public sealed record RadarScopeDot(uint Id, string Name, RadarColor Colour, double Distance, double Bearing, double Alpha, Point At);

/// <summary>
/// One landblock's ground on the scope: the landblock, and its rectangle in metres from the player, x east and y south (as the screen
/// runs), before the scope turns and scales it.
/// </summary>
public sealed record RadarScopeTile(ushort Landblock, Rect Metres);

/// <summary>One level of the indoor plan to draw: the player's own level is solid, the others are faint.</summary>
public sealed record RadarPlanDraw(RadarPlanLevel Level, bool Solid);

/// <summary>What the radar shows this frame. Dots and the centre are in DIPs from the scope's top-left.</summary>
public sealed class RadarScopeFrame
{
    public RadarScopeFrame(IReadOnlyList<RadarScopeDot> dots, IReadOnlyList<RadarScopeTile> tiles, double turn, double pixelsPerMetre, string? coordinates, double heading,
        IReadOnlyList<RadarPlanDraw> planLevels, Point planOrigin)
    {
        Dots = dots;
        Tiles = tiles;
        Turn = turn;
        PixelsPerMetre = pixelsPerMetre;
        Coordinates = coordinates;
        Heading = heading;
        PlanLevels = planLevels;
        PlanOrigin = planOrigin;
    }

    public IReadOnlyList<RadarScopeDot> Dots { get; }
    /// <summary>The ground around the player: outdoors, or under a building's plan. Empty when the map is off, or in a dungeon.</summary>
    public IReadOnlyList<RadarScopeTile> Tiles { get; }
    /// <summary>
    /// The indoor plan's levels in drawing order: the others below the player's, then the others above, then the player's own, solid.
    /// Empty outdoors, with the map off, or until the plan is built.
    /// </summary>
    public IReadOnlyList<RadarPlanDraw> PlanLevels { get; }
    /// <summary>The player's position in landblock metres, from which the plan's paths are drawn.</summary>
    public Point PlanOrigin { get; }
    /// <summary>The clockwise turn of the world on screen, in degrees: the heading turned away when heading-up, 0 when north-up.</summary>
    public double Turn { get; }
    public double PixelsPerMetre { get; }
    /// <summary>The player's coordinates as retail writes them, or null without a player.</summary>
    public string? Coordinates { get; }
    /// <summary>The player's heading in degrees (0 north, 90 east), or 0 without a player.</summary>
    public double Heading { get; }
}

/// <summary>Where objects sit on the radar scope, which dot the pointer is over, and what a hover says. Pure: no window and no game.</summary>
public static class RadarScope
{
    // Retail's rule: a dot that is less than 5 m above or below the player is drawn in full colour, the rest at 0.65.
    private const double LevelMetres = 5, FadedAlpha = 0.65;
    // Retail's hover reach, in pixels.
    private const double HoverReach = 6;
    private const double LandblockMetres = 192;
    private static readonly string[] Points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    /// <param name="player">The player, or null outside the world.</param>
    /// <param name="objects">The objects retail has placed near the player.</param>
    /// <param name="options">The radar's options: the map, north-up and zoom.</param>
    /// <param name="radius">The scope's radius in DIPs.</param>
    /// <param name="plan">The plan of the place the player is in, or null outdoors and until it is built.</param>
    public static RadarScopeFrame Build(RadarPlayer? player, IReadOnlyList<RadarObject> objects, RadarOptions options, double radius, RadarPlan? plan = null)
    {
        var indoors = player is { Position.Outdoors: false };
        var range = indoors ? RadarZoom.ClampIndoor(options.IndoorZoom) : RadarZoom.Clamp(options.Zoom);
        var scale = radius / range;
        var turn = player == null || options.NorthUp ? 0 : -player.Heading;
        // Indoors the plan is drawn when the place has one; a building keeps the ground under it, a dungeon has none.
        var drawn = indoors && options.Map && plan is { PlayerLevel: >= 0 } ? plan : null;
        var tiles = player != null && options.Map && (!indoors || drawn is { Building: true }) ? GroundAround(player) : Array.Empty<RadarScopeTile>();
        var dots = new List<RadarScopeDot>();
        if (player != null)
        {
            foreach (var found in objects)
            {
                if (!found.Shown) continue;
                var (east, north) = player.Position.OffsetTo(found.Position);
                var distance = Math.Sqrt(east * east + north * north);
                if (distance > range) continue;
                var at = Turned(east, north, turn, scale, radius);
                var alpha = Math.Abs(found.Position.Z - player.Position.Z) < LevelMetres ? 1 : FadedAlpha;
                dots.Add(new RadarScopeDot(found.Id, found.Name, found.Colour, distance, Bearing(east, north), alpha, at));
            }
        }
        var planLevels = drawn == null ? Array.Empty<RadarPlanDraw>() : PlanDraws(drawn);
        var origin = player == null ? default : new Point(player.Position.X, player.Position.Y);
        return new RadarScopeFrame(dots, tiles, turn, scale, player?.Position.Coordinates, player?.Heading ?? 0, planLevels, origin);
    }

    /// <summary>The dot under the pointer, the nearest within the hover reach, or null.</summary>
    public static RadarScopeDot? Hit(RadarScopeFrame frame, Point point)
    {
        RadarScopeDot? best = null;
        var bestReach = HoverReach;
        foreach (var dot in frame.Dots)
        {
            var reach = Math.Sqrt(Math.Pow(dot.At.X - point.X, 2) + Math.Pow(dot.At.Y - point.Y, 2));
            if (reach <= bestReach)
            {
                best = dot;
                bestReach = reach;
            }
        }
        return best;
    }

    /// <summary>What a hover says: the name, the distance in whole metres, and the compass direction of the dot from the player.</summary>
    public static string Label(RadarScopeDot dot) =>
        string.Format(CultureInfo.InvariantCulture, "{0} · {1:0} m {2}", dot.Name, dot.Distance, Direction(dot.Bearing));

    /// <summary>The compass point (N, NE, …) of a bearing in degrees from north.</summary>
    public static string Direction(double bearing) => Points[(int)Math.Round(Normalise(bearing) / 45, MidpointRounding.AwayFromZero) % 8];

    // A point (east, north) metres from the player, on the scope: east to the right and north up, turned by the world's turn.
    private static Point Turned(double east, double north, double turn, double scale, double radius)
    {
        var radians = turn * Math.PI / 180;
        var x = east * scale;
        var y = -north * scale;
        return new Point(radius + x * Math.Cos(radians) - y * Math.Sin(radians), radius + x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    // The bearing of a point from the player, in degrees clockwise from north.
    private static double Bearing(double east, double north) => Normalise(Math.Atan2(east, north) * 180 / Math.PI);

    private static double Normalise(double degrees) => ((degrees % 360) + 360) % 360;

    // The levels below the player's, then above, then the player's own, solid last so it sits on the others.
    private static IReadOnlyList<RadarPlanDraw> PlanDraws(RadarPlan plan)
    {
        var draws = new List<RadarPlanDraw>(plan.Levels.Count);
        for (var level = plan.PlayerLevel + 1; level < plan.Levels.Count; level++) draws.Add(new RadarPlanDraw(plan.Levels[level], false));
        for (var level = 0; level < plan.PlayerLevel; level++) draws.Add(new RadarPlanDraw(plan.Levels[level], false));
        draws.Add(new RadarPlanDraw(plan.Levels[plan.PlayerLevel], true));
        return draws;
    }

    // The nine landblocks around the player's, each as a rectangle in metres from the player (south is positive y, as the screen runs).
    private static IReadOnlyList<RadarScopeTile> GroundAround(RadarPlayer player)
    {
        var cell = player.Position.Cell;
        var x = (int)(cell >> 24);
        var y = (int)((cell >> 16) & 0xFF);
        var tiles = new List<RadarScopeTile>(9);
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                var east = dx * LandblockMetres - player.Position.X;
                var north = dy * LandblockMetres - player.Position.Y;
                var landblock = (ushort)(((x + dx) << 8) | (y + dy));
                tiles.Add(new RadarScopeTile(landblock, new Rect(east, -(north + LandblockMetres), LandblockMetres, LandblockMetres)));
            }
        return tiles;
    }
}
