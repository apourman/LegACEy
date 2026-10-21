using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using LegACEy.Client.GameArt;
using LegACEy.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Dereth radar's settings, kept per character: whether the map is drawn (the land outdoors, the floor plan indoors),
/// north-up or heading-up, the range rings, and the zoom in metres outdoors and indoors.
/// </summary>
public sealed record RadarOptions(bool Map = true, bool NorthUp = false, bool Rings = false, int Zoom = RadarZoom.Default,
    int IndoorZoom = RadarZoom.IndoorDefault)
{
    // The bits of the flags kept in the settings store's X, the indoor zoom above them; the zoom is its Y.
    private const int MapBit = 1, NorthUpBit = 2, RingsBit = 4, IndoorZoomShift = 8;

    /// <summary>The flags and the zoom, as two numbers the per-character settings store keeps.</summary>
    public (int Flags, int Zoom) Encode() =>
        ((Map ? MapBit : 0) | (NorthUp ? NorthUpBit : 0) | (Rings ? RingsBit : 0) | (RadarZoom.ClampIndoor(IndoorZoom) << IndoorZoomShift), Zoom);

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

/// <summary>Which level of the indoor plan a dot is on, against the player's: the same, the one above, or the one below.</summary>
public enum RadarFloor
{
    Same,
    Above,
    Below
}

/// <summary>
/// A dot on the radar: an object in retail's radar colour, where it sits on the scope, its name, range and bearing, and its floor against the
/// player's (Same unless the indoor plan is drawn).
/// </summary>
public sealed record RadarScopeDot(uint Id, string Name, RadarColor Colour, double Distance, double Bearing, double Alpha, Point At, RadarFloor Floor);

/// <summary>
/// One landblock's ground on the scope: the landblock, and its rectangle in metres from the player, x east and y south (as the screen
/// runs), before the scope turns and scales it.
/// </summary>
public sealed record RadarScopeTile(ushort Landblock, Rect Metres);

/// <summary>
/// One level of the indoor plan to draw. The player's own level is solid; a peeked level is solid too, in teal (<see cref="Peek"/>), and while
/// one is peeked the player's own is left out.
/// </summary>
public sealed record RadarPlanDraw(RadarPlanLevel Level, bool Solid, bool Peek = false);

/// <summary>
/// The indoor ladder beside the bezel: one pip a level, top first. <see cref="Current"/> is the player's level, <see cref="Peek"/> the level
/// the pointer is on (or -1), and <see cref="Occupied"/> has bit n set when someone within range is on level n (not the player's own).
/// </summary>
public sealed record RadarLadder(int Levels, int Current, int Peek, ulong Occupied);

/// <summary>A spot a player pinged on their radar: who, where, and when it arrived. It shows for <see cref="Lasts"/>.</summary>
public sealed record RadarPing(string Sender, RadarPosition Position, DateTime Placed)
{
    public static readonly TimeSpan Lasts = TimeSpan.FromSeconds(15);
}

/// <summary>
/// A ping on the scope: who placed it, where it sits in DIPs from the scope's top-left, how far through its life it is (0 to 1), and, for a
/// ping past the rim, the screen angle it lies at in degrees clockwise from up, with <see cref="At"/> on the rim.
/// </summary>
public sealed record RadarScopePing(string Sender, Point At, double Age, double? Edge);

/// <summary>What the radar shows this frame. Dots and the centre are in DIPs from the scope's top-left.</summary>
public sealed class RadarScopeFrame
{
    public RadarScopeFrame(IReadOnlyList<RadarScopeDot> dots, IReadOnlyList<RadarScopeTile> tiles, double turn, double pixelsPerMetre,
        string? coordinates, double heading, IReadOnlyList<RadarPlanDraw> planLevels, Point planOrigin,
        RadarLadder? ladder, string? plaque, IReadOnlyList<RadarScopePing>? pings = null)
    {
        Pings = pings ?? Array.Empty<RadarScopePing>();
        Dots = dots;
        Tiles = tiles;
        Turn = turn;
        PixelsPerMetre = pixelsPerMetre;
        Coordinates = coordinates;
        Heading = heading;
        PlanLevels = planLevels;
        PlanOrigin = planOrigin;
        Ladder = ladder;
        Plaque = plaque;
    }

    public IReadOnlyList<RadarScopeDot> Dots { get; }
    /// <summary>The pings still showing, on the scope or as arrows on its rim.</summary>
    public IReadOnlyList<RadarScopePing> Pings { get; }
    /// <summary>The ladder beside the bezel, or null when no plan is drawn (outdoors, the map off, or the plan not yet built).</summary>
    public RadarLadder? Ladder { get; }
    /// <summary>The ground around the player: outdoors, or under a building's plan. Empty when the map is off, or in a dungeon.</summary>
    public IReadOnlyList<RadarScopeTile> Tiles { get; }
    /// <summary>
    /// The indoor plan's levels in drawing order: the others below the player's, then the others above, then the player's own, solid. While a
    /// level is peeked, the player's own is left out and the peeked one is solid.
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
    /// <summary>The plaque's text, as <see cref="RadarScope.Plaque"/> makes it, or null without a player.</summary>
    public string? Plaque { get; }
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
    /// <param name="peek">The level the pointer is on in the ladder, or -1. Drawn in teal, as the solid level, while it is not the player's own; the player's own is then left out.</param>
    /// <param name="serverName">The server's name for a cell's place, or null; see <see cref="Plaque"/>.</param>
    /// <param name="pings">The pings received, shown while they last; see <see cref="PingsOn"/>.</param>
    /// <param name="now">The time the pings' ages are taken at.</param>
    public static RadarScopeFrame Build(RadarPlayer? player, IReadOnlyList<RadarObject> objects, RadarOptions options, double radius,
        RadarPlan? plan = null, int peek = -1, Func<uint, string?>? serverName = null, IEnumerable<RadarPing>? pings = null, DateTime now = default)
    {
        var indoors = player is { Position.Outdoors: false };
        var range = indoors ? RadarZoom.ClampIndoor(options.IndoorZoom) : RadarZoom.Clamp(options.Zoom);
        var scale = radius / range;
        var turn = player == null || options.NorthUp ? 0 : -player.Heading;
        // Indoors the plan is drawn when its place has the player's cell; a building keeps the ground under it, a dungeon has none.
        var level = indoors && options.Map && player != null && plan != null ? plan.LevelOf((ushort)(player.Position.Cell & 0xFFFF)) : -1;
        var drawn = level >= 0 ? plan : null;
        var peeking = drawn != null && peek >= 0 && peek != level && peek < drawn.Levels.Count ? peek : -1;
        var tiles = player != null && options.Map && (!indoors || drawn is { Building: true }) ? GroundAround(player) : Array.Empty<RadarScopeTile>();
        var dots = new List<RadarScopeDot>();
        var occupied = 0UL;
        if (player != null)
        {
            foreach (var found in objects)
            {
                if (!found.Shown) continue;
                var (east, north) = player.Position.OffsetTo(found.Position);
                var distance = Math.Sqrt(east * east + north * north);
                if (distance > range) continue;
                var at = Turned(east, north, turn, scale, radius);
                // A dot on another level is an arrow, in full colour; on the player's own, retail's rule for the height stands.
                var dotLevel = drawn == null ? -1 : LevelOf(drawn, player.Position.Cell, found.Position);
                var floor = dotLevel < 0 || dotLevel == level ? RadarFloor.Same : dotLevel < level ? RadarFloor.Above : RadarFloor.Below;
                if (dotLevel >= 0 && dotLevel < 64 && dotLevel != level) occupied |= 1UL << dotLevel;
                var alpha = floor != RadarFloor.Same || Math.Abs(found.Position.Z - player.Position.Z) < LevelMetres ? 1 : FadedAlpha;
                dots.Add(new RadarScopeDot(found.Id, found.Name, found.Colour, distance, Bearing(east, north), alpha, at, floor));
            }
        }
        var planLevels = drawn?.DrawOrder(level, peeking) ?? Array.Empty<RadarPlanDraw>();
        var ladder = drawn == null ? null : new RadarLadder(drawn.Levels.Count, level, peeking, occupied);
        var origin = player == null ? default : new Point(player.Position.X, player.Position.Y);
        return new RadarScopeFrame(dots, tiles, turn, scale, player?.Position.Coordinates, player?.Heading ?? 0, planLevels, origin, ladder,
            Plaque(player, drawn, level, serverName), player == null || pings == null ? null : PingsOn(player, pings, now, range, turn, scale, radius));
    }

    /// <summary>
    /// The pings that show: not yet <see cref="RadarPing.Lasts"/> old, outdoors while the player is, and indoors only in the player's
    /// landblock. One past the range sits on the rim, pointing its way.
    /// </summary>
    private static List<RadarScopePing> PingsOn(RadarPlayer player, IEnumerable<RadarPing> pings, DateTime now, double range, double turn,
        double scale, double radius)
    {
        var shown = new List<RadarScopePing>();
        foreach (var ping in pings)
        {
            var age = (now - ping.Placed).TotalSeconds / RadarPing.Lasts.TotalSeconds;
            if (age < 0 || age >= 1 || ping.Position.Outdoors != player.Position.Outdoors) continue;
            if (!player.Position.Outdoors && ping.Position.Cell >> 16 != player.Position.Cell >> 16) continue;
            var (east, north) = player.Position.OffsetTo(ping.Position);
            var distance = Math.Sqrt(east * east + north * north);
            if (distance <= range)
            {
                shown.Add(new RadarScopePing(ping.Sender, Turned(east, north, turn, scale, radius), age, null));
                continue;
            }
            var rim = range / distance;
            shown.Add(new RadarScopePing(ping.Sender, Turned(east * rim, north * rim, turn, scale, radius), age, Normalise(Bearing(east, north) + turn)));
        }
        return shown;
    }

    /// <summary>
    /// The spot under a point on the scope, at the player's height: the inverse of where a dot is drawn. Outdoors its cell is the landblock
    /// and outdoor cell it falls in; indoors it keeps the player's cell.
    /// </summary>
    public static RadarPosition PositionAt(RadarPlayer player, RadarOptions options, double radius, Point point)
    {
        var indoors = !player.Position.Outdoors;
        var scale = radius / (indoors ? RadarZoom.ClampIndoor(options.IndoorZoom) : RadarZoom.Clamp(options.Zoom));
        var radians = (options.NorthUp ? 0 : -player.Heading) * Math.PI / 180;
        var x = point.X - radius;
        var y = point.Y - radius;
        var east = (x * Math.Cos(radians) + y * Math.Sin(radians)) / scale;
        var north = -(-x * Math.Sin(radians) + y * Math.Cos(radians)) / scale;
        var at = player.Position;
        if (indoors) return at with { X = (float)(at.X + east), Y = (float)(at.Y + north) };
        var metresX = at.X + east;
        var metresY = at.Y + north;
        var landblockX = (int)(at.Cell >> 24) + (int)Math.Floor(metresX / LandblockMetres);
        var landblockY = (int)((at.Cell >> 16) & 0xFF) + (int)Math.Floor(metresY / LandblockMetres);
        metresX -= Math.Floor(metresX / LandblockMetres) * LandblockMetres;
        metresY -= Math.Floor(metresY / LandblockMetres) * LandblockMetres;
        // Outdoor cells are 24 m squares, 8 a side, numbered from 1 up each column of x.
        var cell = 1 + (uint)(metresX / 24) * 8 + (uint)(metresY / 24);
        return new RadarPosition(((uint)landblockX << 24) | ((uint)landblockY << 16) | cell, (float)metresX, (float)metresY, at.Z);
    }

    /// <summary>
    /// The plaque's text: the coordinates when no plan is drawn. In a building, the coordinates and the floor
    /// ("33.6N, 56.7E · Ground"). In a dungeon, the place's name and the level from the top ("Arwic Mines · Level 2 of 6"), or the level
    /// alone without a name. The window cuts a name too long for the plaque. Null without a player.
    /// </summary>
    /// <param name="drawn">The plan Build draws, or null when none is drawn.</param>
    /// <param name="level">The index in <paramref name="drawn"/> of the player's level, or -1.</param>
    /// <param name="serverName">The server's name for the cell's place, which wins; null or empty leaves the shared list to name it.</param>
    public static string? Plaque(RadarPlayer? player, RadarPlan? drawn, int level, Func<uint, string?>? serverName = null)
    {
        if (player == null) return null;
        var coordinates = player.Position.Coordinates;
        if (drawn == null || level < 0) return coordinates;
        if (drawn.Building) return $"{coordinates} · {drawn.FloorName(level)}";
        var where = $"Level {level + 1} of {drawn.Levels.Count}";
        var name = PlaceName(player.Position.Cell, serverName);
        return name == null ? where : $"{name} · {where}";
    }

    // The place's name: the server's when it gives one, else the shared list's when the landblock has exactly one name. Several names
    // are no name, since a wrong one is worse than none.
    private static string? PlaceName(uint cell, Func<uint, string?>? serverName)
    {
        if (serverName?.Invoke(cell) is { Length: > 0 } named) return named;
        var names = LandblockNames.Get((ushort)(cell >> 16));
        return names.Count == 1 ? names[0] : null;
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

    /// <summary>
    /// What a hover says: the name, the distance in whole metres, the compass direction of the dot from the player, and for a dot on another
    /// level, above or below.
    /// </summary>
    public static string Label(RadarScopeDot dot)
    {
        var text = string.Format(CultureInfo.InvariantCulture, "{0} · {1:0} m {2}", dot.Name, dot.Distance, Direction(dot.Bearing));
        return dot.Floor switch
        {
            RadarFloor.Above => text + " · above",
            RadarFloor.Below => text + " · below",
            _ => text
        };
    }

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

    // An object's level in the drawn plan: its cell's, as the player's is, when the cell is in the place; else the level nearest its height
    // (an object outdoors by a building, or one whose cell retail has not given).
    private static int LevelOf(RadarPlan drawn, uint playerCell, RadarPosition at)
    {
        var level = at.Cell >> 16 == playerCell >> 16 ? drawn.LevelOf((ushort)at.Cell) : -1;
        return level >= 0 ? level : drawn.LevelOfZ(at.Z);
    }

    // The bearing of a point from the player, in degrees clockwise from north.
    private static double Bearing(double east, double north) => Normalise(Math.Atan2(east, north) * 180 / Math.PI);

    private static double Normalise(double degrees) => ((degrees % 360) + 360) % 360;

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
