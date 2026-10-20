using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>A Dereth glyph drawn in a colour, as large as the control's smaller side.</summary>
public sealed class DerethGlyphIcon : Control
{
    private readonly DerethGlyph _glyph;
    private readonly Color _colour;

    public DerethGlyphIcon(DerethGlyph glyph, Color colour)
    {
        _glyph = glyph;
        _colour = colour;
    }

    public override void Render(DrawingContext context) =>
        DerethGlyphs.Draw(context, _glyph, Math.Min(Bounds.Width, Bounds.Height), 0, 0, _colour);
}
