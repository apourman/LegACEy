using System.Collections.Generic;

namespace LegACEy.Client.Themes;

/// <summary>The gold toolbar glyphs: vector art in code, not DAT images (an accepted exception for window icons).</summary>
public enum DerethGlyph
{
    PeaceStance, MeleeStance, MissileStance, MagicStance,
    Social, Magic, Skills, Quests, WorldMap, Options, LegACEy,
    Use, Examine, Backpack, Lock, Unlock
}

/// <summary>One drawn part of a glyph: an SVG path on a 24-unit grid, filled or stroked, at an opacity.</summary>
public readonly struct GlyphPart
{
    public GlyphPart(string data, bool filled, double strokeWidth, double opacity, double[]? dash)
    {
        Data = data;
        Filled = filled;
        StrokeWidth = strokeWidth;
        Opacity = opacity;
        Dash = dash;
    }

    public string Data { get; }
    public bool Filled { get; }
    public double StrokeWidth { get; }
    public double Opacity { get; }
    public double[]? Dash { get; }
}

/// <summary>The toolbar's glyph paths, copied from the mockup's SVG symbols on a 24-unit grid. Strokes use round caps and joins.</summary>
public static class DerethGlyphs
{
    /// <summary>The parts a glyph is drawn from, in drawing order.</summary>
    public static IReadOnlyList<GlyphPart> PartsOf(DerethGlyph glyph) => Parts[glyph];

    /// <summary>The glyph's grid size in its own units; drawn at a scale of size / <see cref="Grid"/>.</summary>
    public const double Grid = 24;

    private static readonly Dictionary<DerethGlyph, GlyphPart[]> Parts = new()
    {
        [DerethGlyph.Social] = new GlyphPart[]
        {
            new("M5.8 8A3.2 3.2 0 1 0 12.2 8A3.2 3.2 0 1 0 5.8 8Z", false, 1.7, 1, null),
            new("M3 20c0-4 2.7-6.3 6-6.3s6 2.3 6 6.3", false, 1.7, 1, null),
            new("M13.9 7A2.6 2.6 0 1 0 19.1 7A2.6 2.6 0 1 0 13.9 7Z", false, 1.7, 1, null),
            new("M15.5 12.8c3 0 5.5 2 5.5 5.6", false, 1.7, 1, null),
        },
        [DerethGlyph.Magic] = new GlyphPart[]
        {
            new("M12 1.5l1.6 7.2 6.6-3.4-4.3 6 7.6.7-7.6.7 4.3 6-6.6-3.4L12 22.5l-1.6-7.2-6.6 3.4 4.3-6L.5 12l7.6-.7-4.3-6 6.6 3.4z", true, 0, 0.95, null),
        },
        [DerethGlyph.Skills] = new GlyphPart[]
        {
            new("M12 3v17M7.5 20.5h9M4.5 7h15", false, 1.6, 1, null),
            new("M10.8 4A1.2 1.2 0 1 0 13.2 4A1.2 1.2 0 1 0 10.8 4Z", true, 0, 1, null),
            new("M10.8 4A1.2 1.2 0 1 0 13.2 4A1.2 1.2 0 1 0 10.8 4Z", false, 1.6, 1, null),
            new("M4.5 7l-2.8 6h5.6zM19.5 7l-2.8 6h5.6zM1.7 13a2.8 1.8 0 0 0 5.6 0M16.7 13a2.8 1.8 0 0 0 5.6 0", false, 1.6, 1, null),
        },
        [DerethGlyph.Quests] = new GlyphPart[]
        {
            new("M20.5 2.5C13 3.5 8 8.5 6 16.5L4 21", false, 1.6, 1, null),
            new("M20.5 2.5c-.6 6.5-5 10.6-11.8 12.6", false, 1.6, 1, null),
            new("M14 7.5l-4.5 4.5M3 21h8", false, 1.6, 1, null),
        },
        [DerethGlyph.WorldMap] = new GlyphPart[]
        {
            new("M2.8 12A9.2 9.2 0 1 0 21.2 12A9.2 9.2 0 1 0 2.8 12Z", false, 1.6, 1, null),
            new("M12 4.5l2.2 7.5h-4.4z", true, 0, 1, null),
            new("M12 19.5l2.2-7.5h-4.4z", false, 1.6, 1, null),
            new("M12 1.5v2M12 20.5v2M1.5 12h2M20.5 12h2", false, 1.6, 1, null),
        },
        [DerethGlyph.Options] = new GlyphPart[]
        {
            new("M4.4 12A7.6 7.6 0 1 0 19.6 12A7.6 7.6 0 1 0 4.4 12Z", false, 3.4, 1, [3, 2.97]),
            new("M6.4 12A5.6 5.6 0 1 0 17.6 12A5.6 5.6 0 1 0 6.4 12Z", false, 1.6, 1, null),
            new("M9.8 12A2.2 2.2 0 1 0 14.2 12A2.2 2.2 0 1 0 9.8 12Z", false, 1.6, 1, null),
        },
        [DerethGlyph.Backpack] = new GlyphPart[]
        {
            new("M6 9.5a6 6 0 0 1 12 0V19a2.2 2.2 0 0 1-2.2 2.2H8.2A2.2 2.2 0 0 1 6 19z", false, 1.6, 1, null),
            new("M9 6.3V4.8a3 3 0 0 1 6 0v1.5", false, 1.6, 1, null),
            new("M8.5 13.5h7v4.5h-7zM8.5 13.5l.5-2h6l.5 2", false, 1.6, 1, null),
        },
        [DerethGlyph.Use] = new GlyphPart[]
        {
            new("M8 13.5V6a1.5 1.5 0 0 1 3 0v5M11 10.5V4.5a1.5 1.5 0 0 1 3 0v6M14 10.5V5.5a1.5 1.5 0 0 1 3 0v6M17 10a1.5 1.5 0 0 1 3 0v3.5c0 4.8-3 7.8-7 7.8-3 0-5-1.4-6.8-4.3L4 14.2a1.5 1.5 0 0 1 2.4-1.7L8 14", false, 1.5, 1, null),
        },
        [DerethGlyph.Examine] = new GlyphPart[]
        {
            new("M3.7 10A6.3 6.3 0 1 0 16.3 10A6.3 6.3 0 1 0 3.7 10Z", false, 1.9, 1, null),
            new("M14.8 14.8L20.5 20.5", false, 1.9, 1, null),
        },
        [DerethGlyph.PeaceStance] = new GlyphPart[]
        {
            new("M2.5 13.5c2.8.4 5.3-.7 6.6-3.6.6 2.6 3 3.8 5.8 3.3L21.5 7l-1.6 5.2c-1.7 5-6.4 7.5-11.6 6.4l-3 3.1.3-4.1C3.8 17 2.9 15.6 2.5 13.5z", true, 0, 1, null),
            new("M9.1 9.9C8.6 6.8 10.4 4 13.5 3c-1 2.2-.9 4.5.5 6.7", true, 0, 0.7, null),
        },
        [DerethGlyph.MeleeStance] = new GlyphPart[]
        {
            new("M20.5 3.5l-.6 4L10 17.4 6.6 14 16.5 4.1z", false, 1.7, 1, null),
            new("M5 12.5l6.5 6.5M7.2 16.8L3.5 20.5", false, 1.7, 1, null),
        },
        [DerethGlyph.MissileStance] = new GlyphPart[]
        {
            new("M5 3.5c8.5 1.8 13.7 7 15.5 15.5", false, 1.6, 1, null),
            new("M5 3.5L20.5 19", false, 1, 1, null),
            new("M3 21L15 9M15 9h-3.4M15 9v3.4", false, 1.6, 1, null),
        },
        [DerethGlyph.MagicStance] = new GlyphPart[]
        {
            new("M7.5 12A4.5 4.5 0 1 0 16.5 12A4.5 4.5 0 1 0 7.5 12Z", true, 0, 0.35, null),
            new("M7.5 12A4.5 4.5 0 1 0 16.5 12A4.5 4.5 0 1 0 7.5 12Z", false, 1.6, 1, null),
            new("M12 2.5v3M12 18.5v3M2.5 12h3M18.5 12h3M5.3 5.3l2.1 2.1M16.6 16.6l2.1 2.1M5.3 18.7l2.1-2.1M16.6 7.4l2.1-2.1", false, 1.6, 1, null),
        },
        [DerethGlyph.Lock] = new GlyphPart[]
        {
            new("M7 10.5H17A2 2 0 0 1 19 12.5V18.5A2 2 0 0 1 17 20.5H7A2 2 0 0 1 5 18.5V12.5A2 2 0 0 1 7 10.5Z", true, 0, 0.3, null),
            new("M7 10.5H17A2 2 0 0 1 19 12.5V18.5A2 2 0 0 1 17 20.5H7A2 2 0 0 1 5 18.5V12.5A2 2 0 0 1 7 10.5Z", false, 2.4, 1, null),
            new("M8 10.5V7.5a4 4 0 0 1 8 0v3", false, 2.4, 1, null),
        },
        [DerethGlyph.Unlock] = new GlyphPart[]
        {
            new("M7 10.5H17A2 2 0 0 1 19 12.5V18.5A2 2 0 0 1 17 20.5H7A2 2 0 0 1 5 18.5V12.5A2 2 0 0 1 7 10.5Z", true, 0, 0.3, null),
            new("M7 10.5H17A2 2 0 0 1 19 12.5V18.5A2 2 0 0 1 17 20.5H7A2 2 0 0 1 5 18.5V12.5A2 2 0 0 1 7 10.5Z", false, 2.4, 1, null),
            new("M8 10.5V7.5a4 4 0 0 1 7.6-1.7", false, 2.4, 1, null),
        },
        [DerethGlyph.LegACEy] = new GlyphPart[]
        {
            new("M12 1.8l6.4 7.4L12 22.2 5.6 9.2z", true, 0, 0.18, null),
            new("M12 1.8l6.4 7.4L12 22.2 5.6 9.2z", false, 1.5, 1, null),
            new("M5.6 9.2h12.8M12 1.8L9.4 9.2 12 22.2l2.6-13z", false, 1.5, 1, null),
            new("M3 4.5l1.2 1.2M21 4.5l-1.2 1.2M2.5 13h1.6M21.5 13h-1.6", false, 1.2, 1, null),
        },
    };
}
