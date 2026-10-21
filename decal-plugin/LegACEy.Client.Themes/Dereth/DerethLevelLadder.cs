using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// The indoor level ladder beside the radar's bezel: a plaque with one pip a level, the top floor first. The player's level is a gold pip, a
/// level with someone on it has a ringed pip, and a peeked level is ringed in teal. Moving the pointer over the ladder reports the level under
/// it, and leaving it reports -1, for the radar to peek at.
/// </summary>
public sealed class DerethLevelLadder : Control
{
    private const double PipSize = 6;
    private static readonly IBrush Plaque = DerethPalette.Brush(Color.FromArgb(0xD9, 0x08, 0x10, 0x16));
    private static readonly IPen PlaqueEdge = new Pen(DerethPalette.GoldDarkBrush, 1);
    private static readonly IBrush PipFill = DerethPalette.Brush(Color.Parse("#0B1117"));
    private static readonly IPen PipEdge = new Pen(DerethPalette.GoldDarkBrush, 1);
    private static readonly IPen RingEdge = new Pen(DerethPalette.GoldBrush, 1);
    private static readonly IPen PeekEdge = new Pen(DerethPalette.TealBrush, 1);

    private int _levels;
    private int _current = -1;
    private int _peek = -1;
    private ulong _occupied;

    /// <summary>Raised as the pointer moves over the ladder with the level under it, and with -1 when it leaves.</summary>
    public event Action<int>? PeekChanged;

    /// <summary>The number of levels, one pip each.</summary>
    public int Levels { get => _levels; set { if (_levels == value) return; _levels = value; InvalidateVisual(); } }

    /// <summary>The player's level, whose pip is gold.</summary>
    public int Current { get => _current; set { if (_current == value) return; _current = value; InvalidateVisual(); } }

    /// <summary>The level being peeked at, whose pip is ringed in teal, or -1.</summary>
    public int Peek { get => _peek; set { if (_peek == value) return; _peek = value; InvalidateVisual(); } }

    /// <summary>Bit n is set when someone is on level n: its pip is ringed.</summary>
    public ulong Occupied { get => _occupied; set { if (_occupied == value) return; _occupied = value; InvalidateVisual(); } }

    public DerethLevelLadder()
    {
        // The press is the ladder's, so it does not move the radar.
        PointerPressed += (_, e) => e.Handled = true;
        PointerMoved += (_, e) => PeekChanged?.Invoke(LevelAt(e.GetPosition(this)));
        PointerExited += (_, _) => PeekChanged?.Invoke(-1);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Plaque, PlaqueEdge, new RoundedRect(new Rect(Bounds.Size).Deflate(0.5), 8));
        if (_levels == 0) return;
        // The pips share the height evenly, as the mockup's space-evenly column does.
        var slot = Bounds.Height / _levels;
        for (var level = 0; level < _levels; level++)
        {
            var centre = new Point(Bounds.Width / 2, (level + 0.5) * slot);
            var edge = level == _peek ? PeekEdge : level == _current || IsOccupied(level) ? RingEdge : PipEdge;
            context.DrawEllipse(level == _current ? DerethPalette.GoldBrush : PipFill, edge, centre, PipSize / 2, PipSize / 2);
        }
    }

    private bool IsOccupied(int level) => level < 64 && (_occupied & (1UL << level)) != 0;

    // The level whose slot the point is in, the top floor first; -1 with no levels.
    private int LevelAt(Point point)
    {
        if (_levels == 0 || Bounds.Height <= 0) return -1;
        return Math.Max(0, Math.Min(_levels - 1, (int)(point.Y / (Bounds.Height / _levels))));
    }
}
