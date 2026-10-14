using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>The combat stance, with Decal's combat mode values: 1 peace, 2 melee, 4 missile, 8 magic.</summary>
public enum RetailStance { Peace = 1, Melee = 2, Missile = 4, Magic = 8 }

/// <summary>
/// Retail's panels, by the number gmPanelUI files each under: the element property 0x10000029 on each retail button in layout 0x21000016
/// (the spec's panel numbers). The window buttons and the backpack open these.
/// </summary>
public enum RetailPanel { Options = 10, Skills = 11, Social = 12, Magic = 13, WorldMap = 16, Quests = 25, Inventory = 7 }

/// <summary>What the selection plaque shows: a creature gets a health meter, an item with mana a mana meter, a stack a name only.</summary>
public enum SelectionKind { Other, Creature, ItemWithMana, Stack }

/// <summary>
/// The selected object: its id, name and kind. <see cref="Meter"/> is the health fraction of a creature or the mana fraction of an item with
/// mana, and null while the server's answer has not come.
/// </summary>
public sealed record RetailSelection(uint Id, string Name, SelectionKind Kind, float? Meter = null);

/// <summary>
/// The retail calls the toolbar makes. The client implements it over retail's toolbar and gmPanelUI; tests fake it.
/// </summary>
public interface IRetailToolbarPort
{
    /// <summary>The player's combat stance.</summary>
    RetailStance Stance { get; }
    /// <summary>The stance button's click: retail's toggle, peace to the default stance and anything else to peace.</summary>
    void ToggleStance();
    /// <summary>Retail's open state for a panel.</summary>
    bool IsPanelOpen(RetailPanel panel);
    void SetPanelOpen(RetailPanel panel, bool open);
    /// <summary>The selected object, or null when nothing is selected.</summary>
    RetailSelection? Selection { get; }
    /// <summary>Uses the selected object, as the hand button does.</summary>
    void UseSelected();
    /// <summary>Examines the selected object in the Dereth appraisal window.</summary>
    void ExamineSelected();
    /// <summary>Raised when the stance, the selection or a panel's open state may have changed.</summary>
    event Action? Changed;
}

/// <summary>
/// The Dereth toolbar that replaces retail's: the stance, six window buttons and the LegACEy menu, then Use, a selection plaque, Examine
/// and the backpack, in one row. The shortcut bars are not in it yet. It shows the port's state and asks the port for each change.
/// </summary>
public sealed class RetailToolbar : Decorator
{
    /// <summary>The window buttons, in the order the toolbar shows them, with the names their hover text shows.</summary>
    private static readonly (RetailPanel Panel, DerethGlyph Glyph, string Name)[] WindowButtons =
    {
        (RetailPanel.Social, DerethGlyph.Social, "Social"),
        (RetailPanel.Magic, DerethGlyph.Magic, "Magic"),
        (RetailPanel.Skills, DerethGlyph.Skills, "Skills"),
        (RetailPanel.Quests, DerethGlyph.Quests, "Quests"),
        (RetailPanel.WorldMap, DerethGlyph.WorldMap, "World map"),
        (RetailPanel.Options, DerethGlyph.Options, "Options"),
    };

    private static readonly IBrush TrackBrush = DerethPalette.Brush(Color.Parse("#0B1117"));
    private static readonly IBrush HealthBrush = Gradient(Color.Parse("#7E211B"), Color.Parse("#D9584A"));
    private static readonly IBrush ManaBrush = Gradient(Color.Parse("#24507F"), Color.Parse("#5AA0E0"));
    // Use and Examine show dimmed while nothing is selected, as retail shows them.
    private const double DimmedOpacity = 0.4;

    private readonly IRetailToolbarPort _port;
    private readonly Action _openMenu;
    private readonly DerethMedallion _stance;
    private readonly Dictionary<RetailPanel, DerethMedallion> _windows = new();
    private readonly DerethMedallion _menu;
    private readonly DerethMedallion _use;
    private readonly DerethMedallion _examine;
    private readonly Border _plaque;

    /// <param name="port">Retail's toolbar, as the client reaches it.</param>
    /// <param name="openMenu">Opens or closes the LegACEy menu. The caller shows the menu's state with <see cref="SetMenuOpen"/>.</param>
    public RetailToolbar(IRetailToolbarPort port, Action openMenu)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _openMenu = openMenu ?? throw new ArgumentNullException(nameof(openMenu));

        _stance = new DerethMedallion(GlyphOf(port.Stance), 34, 21);
        ToolTip.SetTip(_stance, "Combat stance");
        _stance.Clicked += () => _port.ToggleStance();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Height = 32 };
        row.Children.Add(Centred(_stance));
        row.Children.Add(Centred(new DerethDivider()));
        foreach (var (panel, glyph, name) in WindowButtons)
        {
            var button = Medallion(glyph, 26, 16, name);
            button.Clicked += () => TogglePanel(panel);
            _windows.Add(panel, button);
            row.Children.Add(Centred(button));
        }
        _menu = Medallion(DerethGlyph.LegACEy, 26, 16, "LegACEy menu");
        _menu.Clicked += _openMenu;
        row.Children.Add(Centred(_menu));
        row.Children.Add(Centred(new DerethDivider()));
        _use = Medallion(DerethGlyph.Use, 20, 16, "Use selected");
        _use.Clicked += () => { if (_port.Selection is not null) _port.UseSelected(); };
        row.Children.Add(Centred(_use));
        _plaque = new Border
        {
            Width = 150,
            Height = 31,
            CornerRadius = new CornerRadius(3),
            Background = DerethPalette.GrooveBrush,
            BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 0)
        };
        row.Children.Add(Centred(_plaque));
        _examine = Medallion(DerethGlyph.Examine, 20, 16, "Examine selected");
        _examine.Clicked += () => { if (_port.Selection is not null) _port.ExamineSelected(); };
        row.Children.Add(Centred(_examine));
        row.Children.Add(Centred(new DerethDivider()));
        var backpack = Medallion(DerethGlyph.Backpack, 34, 21, "Inventory");
        _windows.Add(RetailPanel.Inventory, backpack);
        backpack.Clicked += () => TogglePanel(RetailPanel.Inventory);
        row.Children.Add(Centred(backpack));

        Child = new DerethFrame(DerethFrameArt.Window) { Child = new Border { Padding = new Thickness(4, 0), Child = row } };
        Refresh();
        port.Changed += Refresh;
    }

    /// <summary>Shows the teal ring on the LegACEy menu button while the menu is open.</summary>
    public void SetMenuOpen(bool open) => _menu.IsOpen = open;

    /// <summary>The button that opens a panel: a window button, or the backpack for the inventory.</summary>
    public DerethMedallion ButtonFor(RetailPanel panel) => _windows[panel];

    public DerethMedallion StanceButton => _stance;

    public DerethMedallion MenuButton => _menu;

    public DerethMedallion UseButton => _use;

    public DerethMedallion ExamineButton => _examine;

    /// <summary>Opens the panel if it is shut, and shuts it if it is open.</summary>
    private void TogglePanel(RetailPanel panel) => _port.SetPanelOpen(panel, !_port.IsPanelOpen(panel));

    /// <summary>Shows the stance, the open windows, the backpack's ring and the selection from the port.</summary>
    private void Refresh()
    {
        _stance.Glyph = GlyphOf(_port.Stance);
        foreach (var pair in _windows) pair.Value.IsOpen = _port.IsPanelOpen(pair.Key);
        ShowSelection(_port.Selection);
    }

    private void ShowSelection(RetailSelection? selection)
    {
        _plaque.Child = PlaqueContent(selection);
        _use.Opacity = _examine.Opacity = selection is null ? DimmedOpacity : 1;
    }

    private static Control PlaqueContent(RetailSelection? selection)
    {
        var name = new TextBlock
        {
            Text = selection?.Name ?? "Nothing selected",
            FontFamily = DerethPalette.Title,
            FontSize = 11,
            FontStyle = selection is null ? FontStyle.Italic : FontStyle.Normal,
            Foreground = selection is null ? DerethPalette.MutedBrush : DerethPalette.CreamBrush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var content = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (selection is { Kind: SelectionKind.Creature or SelectionKind.ItemWithMana } meter)
            content.Children.Add(Meter(meter.Kind == SelectionKind.Creature ? HealthBrush : ManaBrush, meter.Meter ?? 0));
        return content;
    }

    /// <summary>A 5 DIP bar filled to <paramref name="fraction"/> of its width.</summary>
    private static Control Meter(IBrush fill, float fraction)
    {
        var share = Math.Min(Math.Max(fraction, 0f), 1f);
        var fillBar = new Border { Background = fill, CornerRadius = new CornerRadius(3) };
        var bar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(share, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(1 - share, GridUnitType.Star))
            },
            Children = { fillBar }
        };
        Grid.SetColumn(fillBar, 0);
        return new Border { Height = 5, CornerRadius = new CornerRadius(3), Background = TrackBrush, Child = bar };
    }

    private static DerethMedallion Medallion(DerethGlyph glyph, double diameter, double glyphSize, string name)
    {
        var medallion = new DerethMedallion(glyph, diameter, glyphSize);
        ToolTip.SetTip(medallion, name);
        return medallion;
    }

    private static T Centred<T>(T control) where T : Control
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    private static IBrush Gradient(Color from, Color to) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) }
    };

    private static DerethGlyph GlyphOf(RetailStance stance) => stance switch
    {
        RetailStance.Melee => DerethGlyph.MeleeStance,
        RetailStance.Missile => DerethGlyph.MissileStance,
        RetailStance.Magic => DerethGlyph.MagicStance,
        _ => DerethGlyph.PeaceStance
    };
}
