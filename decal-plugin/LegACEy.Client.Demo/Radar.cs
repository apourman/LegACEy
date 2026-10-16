using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;

namespace LegACEy.Client.Demo;

/// <summary>
/// The colour retail draws a radar dot in. The numbers are retail's own (gmRadarUI::GetBlipColor switches on them, 1 to 10): ACE's
/// <c>RadarColor</c> agrees for 1 to 9, but its BrightGreen is 0x10, which retail's switch does not take, so retail draws such an object white.
/// </summary>
public enum RadarColor
{
    Blue = 1,
    Gold = 2,
    White = 3,
    Purple = 4,
    Red = 5,
    Pink = 6,
    Green = 7,
    Yellow = 8,
    Cyan = 9,
    BrightGreen = 10
}

/// <summary>Whether retail shows an object on its radar (ACE <c>RadarBehavior</c>). Retail shows 2, 3 and 4 alike.</summary>
public enum RadarBehavior
{
    Undefined = 0,
    ShowNever = 1,
    ShowMovement = 2,
    ShowAttacking = 3,
    ShowAlways = 4
}

/// <summary>Retail's radar colours.</summary>
public static class RadarColors
{
    /// <summary>
    /// The red, green and blue retail draws each colour with (alpha 1), as floats: the static RGBA table at 0x0081C69C..0x0081C73B of the
    /// installed end-of-retail acclient.exe, which gmRadarUI::GetBlipColor (0x004D8350) copies from. Checked against the running client by
    /// the HUD catalogue.
    /// </summary>
    public static readonly IReadOnlyDictionary<RadarColor, (float R, float G, float B)> Retail = new Dictionary<RadarColor, (float, float, float)>
    {
        [RadarColor.Blue] = (0.25f, 0.66f, 1f),
        [RadarColor.Gold] = (1f, 0.67f, 0f),
        [RadarColor.White] = (1f, 1f, 1f),
        [RadarColor.Purple] = (0.75f, 0.39f, 1f),
        [RadarColor.Red] = (1f, 0.25f, 0.39f),
        [RadarColor.Pink] = (1f, 0.66f, 0.75f),
        [RadarColor.Green] = (0f, 0.5f, 0.25f),
        [RadarColor.Yellow] = (1f, 1f, 0.5f),
        [RadarColor.Cyan] = (0f, 1f, 1f),
        [RadarColor.BrightGreen] = (0f, 1f, 0f)
    };

    public static Color Rgb(RadarColor colour)
    {
        var (r, g, b) = Retail[colour];
        return Color.FromRgb(Byte(r), Byte(g), Byte(b));
    }

    private static byte Byte(float value) => (byte)Math.Round(value * 255);
}

/// <summary>
/// A place as retail keeps it: the cell id (the landblock 0xXXYY in its high 16 bits) and metres inside the landblock, x east, y north,
/// z up. Outdoors a landblock is 192 m across.
/// </summary>
public readonly record struct RadarPosition(uint Cell, float X, float Y, float Z)
{
    /// <summary>Outdoors when the cell (the low 16 bits) is below 0x100, as retail's SmartBox::is_player_outside tests; indoor and dungeon
    /// cells start at 0x100.</summary>
    public bool Outdoors => (Cell & 0xFFFF) < 0x100;

    /// <summary>Metres east and north from here to <paramref name="other"/>, across landblocks.</summary>
    public (float East, float North) OffsetTo(RadarPosition other) => (
        ((int)(other.Cell >> 24) - (int)(Cell >> 24)) * 192f + other.X - X,
        ((int)((other.Cell >> 16) & 0xFF) - (int)((Cell >> 16) & 0xFF)) * 192f + other.Y - Y);

    /// <summary>
    /// Retail's map coordinates as its radar writes them, such as "33.6N, 56.7E". CPlayerSystem::InqPlayerCoords counts 24 m cells across
    /// the world: north = (landblock y × 8 + ⌊y / 24⌋ − 1024) × 0.1 + 0.5, east the same with x; negative is south and west.
    /// </summary>
    public string Coordinates
    {
        get
        {
            var north = Coordinate((Cell >> 16) & 0xFF, Y);
            var east = Coordinate(Cell >> 24, X);
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}{1}, {2:0.0}{3}", Math.Abs(north), north < 0 ? 'S' : 'N', Math.Abs(east), east < 0 ? 'W' : 'E');
        }
    }

    private static double Coordinate(uint landblock, float metres) => (landblock * 8 + Math.Floor(metres / 24) - 1024) * 0.1 + 0.5;
}

/// <summary>The player: where, and the compass heading in degrees (0 north, 90 east).</summary>
public sealed record RadarPlayer(RadarPosition Position, float Heading);

/// <summary>An object near the player, as retail's radar sees it.</summary>
public sealed record RadarObject(uint Id, string Name, RadarColor Colour, RadarBehavior Behavior, RadarPosition Position)
{
    /// <summary>Whether retail would draw it (ACCWeenieObject::InqShowableOnRadar).</summary>
    public bool Shown => Behavior is RadarBehavior.ShowMovement or RadarBehavior.ShowAttacking or RadarBehavior.ShowAlways;
}

/// <summary>
/// What the radar reads from the game and does in it. The client implements it over retail and Decal; tests fake it. Read it on the game
/// thread, once a frame: positions move every frame.
/// </summary>
public interface IRadarPort
{
    /// <summary>The player, or null outside the world.</summary>
    RadarPlayer? Player { get; }
    /// <summary>
    /// Every object retail has placed in the world within 150 m of the player (level distance, the radar's widest zoom), but the player, with
    /// its position this frame. Empty while the player is not in the world.
    /// </summary>
    IReadOnlyList<RadarObject> Objects { get; }
    /// <summary>Selects the object, as clicking it in the world does.</summary>
    void Select(uint id);
    /// <summary>Retail's UI lock, as its radar's lock button reads and sets it.</summary>
    bool UiLocked { get; set; }
    /// <summary>Whether retail's UI lock can be read and set: false when the running client does not match its lock natives.</summary>
    bool UiLockAvailable { get; }
}
