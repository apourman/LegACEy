using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// LegACEy's vitals window in one of two layouts, both in the gilded frame. Stacked (A): retail's three bars in retail's order and colours on
/// an ornate gilded plate, each with its glyph in an engraved gold bezel at the left and current / max on a plaque at the right. Side by side
/// (D): the same parts in one row on a slim gilded plate, sharing the width equally, with a gold lozenge between vitals. Either way a pale trail stays behind a drop, health pulses at its edge under a quarter, and a gear
/// shown on hover switches the layout. With retail-style bars, either layout drops the glyphs and plaques, as retail's bars do, and shows each
/// vital's numbers in the middle of its taller bar. Its values come from the game's state each frame.
/// </summary>
public sealed class DerethVitalsWindow : UserControl
{
    /// <summary>The stacked window's design size in DIPs.</summary>
    public const int DesignWidth = 252, DesignHeight = 84;
    /// <summary>
    /// How the stacked window resizes, in DIPs. Its height sets how large it draws, from three quarters of its design size to twice it; its
    /// width stretches the bars, from 180 to 480 DIPs at that scale.
    /// </summary>
    public const int MinimumHeight = 63, MaximumHeight = 168, LayoutMinimumWidth = 180, LayoutMaximumWidth = 480;
    /// <summary>The side-by-side strip's design size in DIPs.</summary>
    public const int SideDesignWidth = 440, SideDesignHeight = 26;
    /// <summary>How the strip resizes, as the stacked window does: its height sets its scale, and its width stretches it from 260 to 880 DIPs at that scale.</summary>
    public const int SideMinimumHeight = 20, SideMaximumHeight = 52, SideLayoutMinimumWidth = 260, SideLayoutMaximumWidth = 880;
    /// <summary>The stacked window's id, and the strip's: each layout keeps its own size under its own id.</summary>
    public const string StackedId = "dereth-vitals", SideBySideId = "dereth-vitals-side";

    // The bezels' diameters, stacked and in the strip, and the stacked window's value plaque, in DIPs.
    private const double StackedBezel = 17.6, SideBezel = 14.4, PlaqueWidth = 44, PlaqueHeight = 15;
    // A retail-style stacked bar's height in DIPs, tall enough for its numbers inside.
    private const double RetailBarHeight = 15;
    private const double GearDiameter = 11, GearGlyphSize = 7;

    private readonly IGameStatePort _game;
    private readonly Func<bool> _uiLocked;
    private readonly Action<VitalsLayout>? _chose;
    private readonly VitalsModel _model = new();
    private readonly Dictionary<Vital, Row> _rows = new();
    private readonly DerethStretchGrip _grip = new();
    private readonly DerethMedallion _gear;

    private sealed class Row
    {
        public Grid Line = null!;
        public DerethVitalBar Bar = null!;
        public TextBlock Label = null!;
        public Run CurrentText = null!, MaxText = null!;
        // The values the label and tooltip show, so they are set only when a value changes.
        public int Current = -1, Max = -1;
    }

    /// <param name="game">The game's state, read on each <see cref="Refresh"/>.</param>
    /// <param name="uiLocked">Whether retail's UI is locked: the grip hides, and a press on the content doesn't start a move.</param>
    /// <param name="startMove">Starts the window's move from the pointer's place, on a press on the window that should drag it.</param>
    /// <param name="layout">The layout the window draws: stacked or side by side.</param>
    /// <param name="chose">Told the layout the gear asks for, which is the other one. The owner keeps it and opens that layout's window.</param>
    /// <param name="retailBars">Whether the bars are retail-style: no glyphs or plaques, and the numbers in the middle of each bar.</param>
    public DerethVitalsWindow(IGameStatePort game, Func<bool> uiLocked, Action startMove, VitalsLayout layout = VitalsLayout.Stacked, Action<VitalsLayout>? chose = null,
        bool retailBars = false)
    {
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _uiLocked = uiLocked ?? throw new ArgumentNullException(nameof(uiLocked));
        if (startMove == null) throw new ArgumentNullException(nameof(startMove));
        _chose = chose;
        var side = layout == VitalsLayout.SideBySide;

        // Stacked: three rows 21 DIPs apart inside the ornate plate. Side by side: three equal parts with a lozenge between each, inset on both
        // sides past the gear in the top-right corner, so the gear never covers the last value.
        var lines = side
            ? new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*,14,*"), Margin = new Thickness(13, 0) }
            : new Grid { RowDefinitions = new RowDefinitions("*,*,*"), Margin = new Thickness(13.2, 10.5, 9, 10.5) };
        foreach (var vital in new[] { Vital.Health, Vital.Stamina, Vital.Mana })
        {
            var row = Line(vital, side, retailBars);
            _rows[vital] = row;
            if (side) Grid.SetColumn(row.Line, 2 * (int)vital);
            else Grid.SetRow(row.Line, (int)vital);
            lines.Children.Add(row.Line);
        }
        if (side)
            foreach (var column in new[] { 1, 3 })
            {
                var lozenge = new DerethLozenge { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(lozenge, column);
                lines.Children.Add(lozenge);
            }

        var frame = new DerethGildedPlate(side ? 4 : 6, ornate: !side) { Child = lines };

        // The gear switches to the other layout. It shows while the pointer is over the window, as the grip does, and a click on it is the
        // gear's own, not a move.
        _gear = new DerethMedallion(DerethGlyph.Options, GearDiameter, GearGlyphSize)
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = side ? new Thickness(0, 1, 1, 0) : new Thickness(0, 2, 16, 0), Opacity = 0
        };
        _gear.Clicked += () => _chose?.Invoke(side ? VitalsLayout.Stacked : VitalsLayout.SideBySide);
        ToolTip.SetTip(_gear, side ? "Switch to stacked" : "Switch to side by side");

        _grip.HorizontalAlignment = HorizontalAlignment.Right;
        _grip.VerticalAlignment = VerticalAlignment.Bottom;
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
        // Drawn at the window's height over its layout height, so stretching it taller draws it larger.
        Content = new HeightScaled(side ? SideDesignHeight : DesignHeight) { Child = root };
        Refresh(TimeSpan.Zero);
    }

    /// <summary>The stacked window's definition: 252 by 84 DIPs, drawn larger as it is stretched taller and wider as it is stretched wider, in the Dereth theme.</summary>
    private static WindowDefinition StackedDefinition(Func<bool> uiLocked) => new(StackedId, "Vitals", DesignWidth, DesignHeight,
        theme: new DerethClientTheme(),
        resizing: ScaledResizing(DesignHeight, MinimumHeight, MaximumHeight, LayoutMinimumWidth, LayoutMaximumWidth, uiLocked, noCorners: false),
        dragsFromContent: true);

    /// <summary>
    /// Resizing for a window drawn at its height over <paramref name="layoutHeight"/>: between the two heights, and as narrow and as wide as
    /// the layout widths at that scale.
    /// </summary>
    private static WindowResizing ScaledResizing(int layoutHeight, int minimumHeight, int maximumHeight, int layoutMinimumWidth, int layoutMaximumWidth,
        Func<bool> uiLocked, bool noCorners)
    {
        double narrowest = (double)layoutMinimumWidth / layoutHeight, widest = (double)layoutMaximumWidth / layoutHeight;
        return new WindowResizing(
            new System.Drawing.Size((int)Math.Ceiling(minimumHeight * narrowest), minimumHeight),
            new System.Drawing.Size((int)Math.Floor(maximumHeight * widest), maximumHeight),
            uiLocked, noCorners, (narrowest, widest));
    }

    /// <summary>
    /// The window's definition in <paramref name="layout"/>. The strip opens where the stacked window was left, and where it is left is where the
    /// stacked window opens; each keeps its own size.
    /// </summary>
    public static WindowDefinition DefinitionOf(VitalsLayout layout, Func<bool> uiLocked) =>
        layout == VitalsLayout.SideBySide ? SideBySideDefinition(uiLocked) : StackedDefinition(uiLocked);

    private static WindowDefinition SideBySideDefinition(Func<bool> uiLocked) => new(SideBySideId, "Vitals", SideDesignWidth, SideDesignHeight,
        // No title bar: the strip drags from its content, so its title bar is one pixel tall.
        titleBarHeight: 1, theme: new DerethClientTheme(),
        resizing: ScaledResizing(SideDesignHeight, SideMinimumHeight, SideMaximumHeight, SideLayoutMinimumWidth, SideLayoutMaximumWidth, uiLocked, noCorners: true),
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
        // An index loop: a foreach over the interface allocates an enumerator on every frame.
        var bars = _model.Bars;
        for (var i = 0; i < bars.Count; i++)
        {
            var reading = bars[i];
            var row = _rows[reading.Vital];
            row.Bar.Show(reading.Fraction, reading.TrailFraction, reading.Pulsing, pulse, now);
            if (reading.Current == row.Current && reading.Max == row.Max) continue;
            row.Current = reading.Current;
            row.Max = reading.Max;
            row.CurrentText.Text = reading.Current.ToString(System.Globalization.CultureInfo.InvariantCulture);
            row.MaxText.Text = "/" + reading.Max.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ToolTip.SetTip(row.Line, reading.Tooltip);
        }
        _grip.IsVisible = !_uiLocked();
    }

    private static Row Line(Vital vital, bool side, bool retailBars)
    {
        var (glyph, light, mid, dark) = vital switch
        {
            Vital.Health => (DerethGlyph.Heart, DerethVitalsPalette.HealthLight, DerethVitalsPalette.HealthMid, DerethVitalsPalette.HealthDark),
            Vital.Stamina => (DerethGlyph.Sword, DerethVitalsPalette.StaminaLight, DerethVitalsPalette.StaminaMid, DerethVitalsPalette.StaminaDark),
            _ => (DerethGlyph.ManaSerpent, DerethVitalsPalette.ManaLight, DerethVitalsPalette.ManaMid, DerethVitalsPalette.ManaDark)
        };
        var row = new Row
        {
            Bar = new DerethVitalBar(light, mid, dark, ticks: !side && !retailBars),
            CurrentText = new Run(),
            // On the bar's colour the max is cream too, and only smaller.
            MaxText = new Run { FontSize = side ? 7 : 7.6, Foreground = retailBars ? DerethPalette.CreamBrush : DerethPalette.Brush(Color.Parse("#B49A6A")) }
        };
        // Centred on its digits, not on Palatino's tall line box, so it sits in the middle of its plaque or its bar.
        row.Label = DerethPalette.CentreInk(new TextBlock
        {
            FontFamily = DerethPalette.Title, FontSize = side ? (retailBars ? 8.5 : 9.5) : (retailBars ? 9.5 : 10.5), Foreground = DerethPalette.CreamBrush,
            HorizontalAlignment = retailBars ? HorizontalAlignment.Center : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Inlines = { row.CurrentText, row.MaxText }
        });
        if (retailBars)
        {
            // A dark halo keeps the numbers readable over the fill's light band.
            row.Label.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 2.5, Color = Colors.Black, Opacity = 1 };
            row.Label.IsHitTestVisible = false;
            Control retailBar;
            if (side)
            {
                row.Bar.Height = double.NaN;
                retailBar = new Grid { RowDefinitions = new RowDefinitions("6*,14*,6*"), Margin = new Thickness(6, 0), Children = { row.Bar, row.Label } };
                Grid.SetRow(row.Bar, 1);
                Grid.SetRow(row.Label, 1);
            }
            else
            {
                row.Bar.Height = RetailBarHeight;
                row.Bar.VerticalAlignment = VerticalAlignment.Center;
                retailBar = new Grid { Margin = new Thickness(0, 0, 4, 0), Children = { row.Bar, row.Label } };
            }
            row.Line = new Grid { Background = Brushes.Transparent, Children = { retailBar } };
            return row;
        }
        var bezel = new DerethVitalGlyph(glyph, light, side ? SideBezel : StackedBezel) { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bezel, 0);

        Control bar;
        Control value;
        if (side)
        {
            // The strip's bars fill a third of its height.
            row.Bar.Height = double.NaN;
            bar = new Grid { RowDefinitions = new RowDefinitions("17*,18*,17*"), Margin = new Thickness(13, 0, 10, 0), Children = { row.Bar } };
            Grid.SetRow(row.Bar, 1);
            value = row.Label;
        }
        else
        {
            row.Bar.VerticalAlignment = VerticalAlignment.Center;
            bar = new Border { Margin = new Thickness(11.2, 0, 11, 0), Child = row.Bar };
            row.Label.Margin = new Thickness(0, 0, 4, 0);
            value = new DerethValuePlaque { Width = PlaqueWidth, Height = PlaqueHeight, VerticalAlignment = VerticalAlignment.Center, Child = row.Label };
        }
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(value, 2);
        row.Line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Background = Brushes.Transparent, Children = { bezel, bar, value } };
        return row;
    }
}
