using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// LegACEy's vitals window in one of two layouts. Stacked (A): retail's three bars in retail's order and colours, in a Dereth frame, each
/// with its glyph in a bezel at the left and current / max at the right. Side by side (D): the same three bars in one row on a slim
/// plaque, sharing the width equally. Either way a pale trail stays behind a drop, health pulses at its edge under a quarter, and a gear
/// shown on hover switches the layout. Its values come from the game's state each frame.
/// </summary>
public sealed class DerethVitalsWindow : UserControl
{
    /// <summary>The stacked window's design size in DIPs.</summary>
    public const int DesignWidth = 220, DesignHeight = 64;
    /// <summary>How wide the stacked window stretches, in DIPs; its height does not change.</summary>
    public const int MinimumWidth = 150, MaximumWidth = 460;
    /// <summary>The side-by-side strip's design size in DIPs.</summary>
    public const int SideDesignWidth = 440, SideDesignHeight = 22;
    /// <summary>How far the strip stretches in DIPs: wider, and taller so its bars grow thicker.</summary>
    public const int SideMinimumWidth = 260, SideMaximumWidth = 880, SideMinimumHeight = 16, SideMaximumHeight = 40;
    /// <summary>The stacked window's id, and the strip's: each layout keeps its own size under its own id.</summary>
    public const string StackedId = "dereth-vitals", SideBySideId = "dereth-vitals-side";

    // The strip's gap between bars, and its plaque: the mockup's slim plaque, a little more opaque than the radar's.
    private const double SideGap = 10, SideCornerRadius = 4;
    private const double GearDiameter = 11, GearGlyphSize = 7;
    private static readonly IBrush Plaque = DerethPalette.Brush(Color.FromArgb(0xC7, 8, 16, 22));
    private static readonly IBrush GoldDark = DerethPalette.Brush(Color.Parse("#7A5E30"));

    private readonly IGameStatePort _game;
    private readonly Func<bool> _uiLocked;
    private readonly Action<VitalsLayout>? _chose;
    private readonly VitalsModel _model = new();
    private readonly Dictionary<Vital, Row> _rows = new();
    private readonly DerethStretchGrip _grip = new();
    private readonly DerethMedallion _gear;

    private sealed class Row
    {
        public DockPanel Line = null!;
        public DerethVitalBar Bar = null!;
        public TextBlock Label = null!;
        // The values the label and tooltip show, so they are set only when a value changes.
        public int Current = -1, Max = -1;
    }

    /// <param name="game">The game's state, read on each <see cref="Refresh"/>.</param>
    /// <param name="uiLocked">Whether retail's UI is locked: the grip hides, and a press on the content doesn't start a move.</param>
    /// <param name="startMove">Starts the window's move from the pointer's place, on a press on the window that should drag it.</param>
    /// <param name="layout">The layout the window draws: stacked or side by side.</param>
    /// <param name="chose">Told the layout the gear asks for, which is the other one. The owner keeps it and opens that layout's window.</param>
    public DerethVitalsWindow(IGameStatePort game, Func<bool> uiLocked, Action startMove, VitalsLayout layout = VitalsLayout.Stacked, Action<VitalsLayout>? chose = null)
    {
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _uiLocked = uiLocked ?? throw new ArgumentNullException(nameof(uiLocked));
        if (startMove == null) throw new ArgumentNullException(nameof(startMove));
        _chose = chose;
        var side = layout == VitalsLayout.SideBySide;

        var lines = side ? new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") } : new Grid { RowDefinitions = new RowDefinitions("*,*,*") };
        foreach (var vital in new[] { Vital.Health, Vital.Stamina, Vital.Mana })
        {
            var row = Line(vital, side);
            _rows[vital] = row;
            if (side)
            {
                Grid.SetColumn(row.Line, (int)vital);
                row.Line.Margin = new Thickness(vital == Vital.Health ? 0 : SideGap / 2, 0, vital == Vital.Mana ? 0 : SideGap / 2, 0);
            }
            else Grid.SetRow(row.Line, (int)vital);
            lines.Children.Add(row.Line);
        }

        // Stacked is the Dereth frame; the strip is a slim plaque, as the mockup's.
        Control frame = side
            ? new Border { CornerRadius = new CornerRadius(4), Background = Plaque, BorderBrush = GoldDark, BorderThickness = new Thickness(1), Padding = new Thickness(6, 3, 8, 3), Child = lines }
            : new DerethFrame(DerethFrameArt.Window) { Child = new Border { Margin = new Thickness(2, 0), Child = lines } };

        // The gear switches to the other layout. It shows while the pointer is over the window, as the grip does, and a click on it is the
        // gear's own, not a move.
        _gear = new DerethMedallion(DerethGlyph.Options, GearDiameter, GearGlyphSize)
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 1, 0), Opacity = 0
        };
        _gear.Clicked += () => _chose?.Invoke(side ? VitalsLayout.Stacked : VitalsLayout.SideBySide);
        ToolTip.SetTip(_gear, side ? "Switch to stacked" : "Switch to side by side");

        _grip.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        _grip.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        _grip.Margin = new Thickness(0, 0, 2, 2);
        // Hidden until the pointer is over the window, as the mockup's grip is.
        _grip.Opacity = 0;
        // The grip's own presses are the window's resize, which the window manager has already taken.
        _grip.PointerPressed += (_, e) => e.Handled = true;

        // A transparent square takes the pointer over the whole window, so a press anywhere on it can drag it.
        var root = new Grid
        {
            Background = Brushes.Transparent,
            Children = { frame, _gear, _grip }
        };
        root.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(root).Properties.IsLeftButtonPressed && !_uiLocked()) startMove();
        };
        root.PointerEntered += (_, _) => _grip.Opacity = _gear.Opacity = 0.9;
        root.PointerExited += (_, _) => _grip.Opacity = _gear.Opacity = 0;
        Content = root;
        Refresh(TimeSpan.Zero);
    }

    /// <summary>The stacked window's definition: 220 by 64 DIPs, stretching wider from 150 to 460 DIPs, in the Dereth theme.</summary>
    public static WindowDefinition Definition(string id, Func<bool> uiLocked) => new(id, "Vitals", DesignWidth, DesignHeight,
        theme: new DerethClientTheme(),
        resizing: new WindowResizing(new System.Drawing.Size(MinimumWidth, DesignHeight), new System.Drawing.Size(MaximumWidth, DesignHeight), uiLocked),
        dragsFromContent: true);

    /// <summary>
    /// The window's definition in <paramref name="layout"/>. The strip opens where the stacked window was left, and where it is left is where the
    /// stacked window opens; each keeps its own size.
    /// </summary>
    public static WindowDefinition DefinitionOf(VitalsLayout layout, Func<bool> uiLocked) =>
        layout == VitalsLayout.SideBySide ? SideBySideDefinition(uiLocked) : Definition(StackedId, uiLocked);

    private static WindowDefinition SideBySideDefinition(Func<bool> uiLocked) => new(SideBySideId, "Vitals", SideDesignWidth, SideDesignHeight,
        // No title bar: the strip drags from its content, so its title bar is one pixel tall.
        titleBarHeight: 1, theme: new DerethClientTheme(),
        resizing: new WindowResizing(new System.Drawing.Size(SideMinimumWidth, SideMinimumHeight), new System.Drawing.Size(SideMaximumWidth, SideMaximumHeight), uiLocked),
        sharesLocationWith: StackedId, dragsFromContent: true);

    /// <summary>Whether the stretch grip shows: it is hidden while retail's UI is locked.</summary>
    public bool GripVisible => _grip.IsVisible;

    /// <summary>The hover text of a vital's bar: its name and value.</summary>
    public string? TooltipOf(Vital vital) => ToolTip.GetTip(_rows[vital].Line) as string;

    /// <summary>Shows the game's values as they stand at <paramref name="now"/>, the player's clock.</summary>
    public void Refresh(TimeSpan now)
    {
        _model.Update(_game.Current, now);
        var pulse = _model.PulseAt(now);
        foreach (var reading in _model.Bars)
        {
            var row = _rows[reading.Vital];
            row.Bar.Show(reading.Fraction, reading.TrailFraction, reading.Pulsing, pulse);
            if (reading.Current == row.Current && reading.Max == row.Max) continue;
            row.Current = reading.Current;
            row.Max = reading.Max;
            row.Label.Text = reading.Label;
            ToolTip.SetTip(row.Line, reading.Tooltip);
        }
        _grip.IsVisible = !_uiLocked();
    }

    private static Row Line(Vital vital, bool side)
    {
        var (glyph, light, mid, dark) = vital switch
        {
            Vital.Health => (DerethGlyph.Heart, DerethVitalsPalette.HealthLight, DerethVitalsPalette.HealthMid, DerethVitalsPalette.HealthDark),
            Vital.Stamina => (DerethGlyph.Sword, DerethVitalsPalette.StaminaLight, DerethVitalsPalette.StaminaMid, DerethVitalsPalette.StaminaDark),
            _ => (DerethGlyph.ManaSerpent, DerethVitalsPalette.ManaLight, DerethVitalsPalette.ManaMid, DerethVitalsPalette.ManaDark)
        };
        var row = new Row
        {
            Bar = new DerethVitalBar(light, mid, dark, side ? SideCornerRadius : null) { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            Label = new TextBlock
            {
                FontFamily = DerethPalette.Title, FontSize = 9, Foreground = DerethPalette.CreamBrush,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            }
        };
        if (side)
        {
            // The strip's bars fill its height, so they grow thicker with it.
            row.Bar.Height = double.NaN;
            row.Bar.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        }
        var glyphBezel = new DerethVitalGlyph(glyph, light) { Margin = new Thickness(0, 0, side ? 4 : 6, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        DockPanel.SetDock(glyphBezel, Dock.Left);
        // The bar fills the line; the value sits over its right end.
        var bar = new Grid { Children = { row.Bar, row.Label } };
        row.Line = new DockPanel { LastChildFill = true, Background = Brushes.Transparent, Children = { glyphBezel, bar } };
        return row;
    }
}
