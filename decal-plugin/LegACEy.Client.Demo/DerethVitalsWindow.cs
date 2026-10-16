using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// LegACEy's vitals window, layout A: retail's three bars in retail's order and colours, in a Dereth frame. Each bar has its glyph in a
/// bezel at the left and current / max at the right, a pale trail behind a drop, and a pulse at its edge while health is low. The window
/// is 64 DIPs high and stretches wider by its grip, from 150 to 460 DIPs. Its values come from the game's state each frame.
/// </summary>
public sealed class DerethVitalsWindow : UserControl
{
    /// <summary>The window's design size in DIPs.</summary>
    public const int DesignWidth = 220, DesignHeight = 64;
    /// <summary>How wide the window stretches, in DIPs; its height does not change.</summary>
    public const int MinimumWidth = 150, MaximumWidth = 460;

    private readonly IGameStatePort _game;
    private readonly Func<bool> _uiLocked;
    private readonly VitalsModel _model = new();
    private readonly Dictionary<Vital, Row> _rows = new();
    private readonly DerethStretchGrip _grip = new();

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
    public DerethVitalsWindow(IGameStatePort game, Func<bool> uiLocked, Action startMove)
    {
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _uiLocked = uiLocked ?? throw new ArgumentNullException(nameof(uiLocked));
        if (startMove == null) throw new ArgumentNullException(nameof(startMove));

        var lines = new Grid { RowDefinitions = new RowDefinitions("*,*,*") };
        foreach (var vital in new[] { Vital.Health, Vital.Stamina, Vital.Mana })
        {
            var row = Line(vital);
            _rows[vital] = row;
            Grid.SetRow(row.Line, (int)vital);
            lines.Children.Add(row.Line);
        }

        _grip.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        _grip.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        _grip.Margin = new Thickness(0, 0, 2, 2);
        // The grip's own presses are the window's resize, which the window manager has already taken.
        _grip.PointerPressed += (_, e) => e.Handled = true;

        // A transparent square takes the pointer over the whole window, so a press anywhere on it can drag it.
        var root = new Grid
        {
            Background = Brushes.Transparent,
            Children = { new DerethFrame(DerethFrameArt.Window) { Child = new Border { Margin = new Thickness(2, 0), Child = lines } }, _grip }
        };
        root.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(root).Properties.IsLeftButtonPressed && !_uiLocked()) startMove();
        };
        root.PointerEntered += (_, _) => _grip.Opacity = 0.9;
        root.PointerExited += (_, _) => _grip.Opacity = 0;
        Content = root;
        Refresh(TimeSpan.Zero);
    }

    /// <summary>The window's definition: 220 by 64 DIPs, stretching wider from 150 to 460 DIPs, in the Dereth theme.</summary>
    public static WindowDefinition Definition(string id, Func<bool> uiLocked) => new(id, "Vitals", DesignWidth, DesignHeight,
        theme: new DerethClientTheme(),
        resizing: new WindowResizing(new System.Drawing.Size(MinimumWidth, DesignHeight), new System.Drawing.Size(MaximumWidth, DesignHeight), uiLocked),
        dragsFromContent: true);

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

    private static Row Line(Vital vital)
    {
        var (glyph, light, mid, dark) = vital switch
        {
            Vital.Health => (DerethGlyph.Heart, DerethVitalsPalette.HealthLight, DerethVitalsPalette.HealthMid, DerethVitalsPalette.HealthDark),
            Vital.Stamina => (DerethGlyph.Sword, DerethVitalsPalette.StaminaLight, DerethVitalsPalette.StaminaMid, DerethVitalsPalette.StaminaDark),
            _ => (DerethGlyph.ManaSerpent, DerethVitalsPalette.ManaLight, DerethVitalsPalette.ManaMid, DerethVitalsPalette.ManaDark)
        };
        var row = new Row
        {
            Bar = new DerethVitalBar(light, mid, dark) { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            Label = new TextBlock
            {
                FontFamily = DerethPalette.Title, FontSize = 9, Foreground = DerethPalette.CreamBrush,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            }
        };
        var glyphBezel = new DerethVitalGlyph(glyph, light) { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        DockPanel.SetDock(glyphBezel, Dock.Left);
        // The bar fills the line; the value sits over its right end.
        var bar = new Grid { Children = { row.Bar, row.Label } };
        row.Line = new DockPanel { LastChildFill = true, Background = Brushes.Transparent, Children = { glyphBezel, bar } };
        return row;
    }
}
