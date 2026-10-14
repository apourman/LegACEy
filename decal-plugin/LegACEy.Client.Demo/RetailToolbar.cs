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

/// <summary>Retail's panels, by the number gmPanelUI files each under. The window buttons and the backpack open these.</summary>
public enum RetailPanel { Options = 10, Skills = 11, Social = 12, Magic = 13, WorldMap = 16, Quests = 25, Inventory = 7 }

/// <summary>
/// The retail calls the toolbar makes. The client implements it over retail's toolbar and gmPanelUI; tests fake it.
/// </summary>
public interface IRetailToolbarPort
{
    /// <summary>The player's combat stance.</summary>
    RetailStance Stance { get; }
    void SetStance(RetailStance stance);
    /// <summary>Retail's open state for a panel.</summary>
    bool IsPanelOpen(RetailPanel panel);
    void SetPanelOpen(RetailPanel panel, bool open);
    /// <summary>Raised when the stance or a panel's open state may have changed.</summary>
    event Action? Changed;
}

/// <summary>
/// The Dereth toolbar that replaces retail's: the stance, six window buttons and the LegACEy menu, then Use, a selection plaque, Examine
/// and the backpack, in one row. Use, the plaque and Examine are placeholders until the selection work. It shows the port's state and
/// asks the port for each change.
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

    private readonly IRetailToolbarPort _port;
    private readonly Action _openMenu;
    private readonly DerethMedallion _stance;
    private readonly Dictionary<RetailPanel, DerethMedallion> _windows = new();
    private readonly DerethMedallion _menu;

    /// <param name="port">Retail's toolbar, as the client reaches it.</param>
    /// <param name="openMenu">Opens or closes the LegACEy menu. The caller shows the menu's state with <see cref="SetMenuOpen"/>.</param>
    public RetailToolbar(IRetailToolbarPort port, Action openMenu)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _openMenu = openMenu ?? throw new ArgumentNullException(nameof(openMenu));

        _stance = new DerethMedallion(GlyphOf(port.Stance), 34, 21);
        ToolTip.SetTip(_stance, "Combat stance");
        _stance.Clicked += () => _port.SetStance(NextStance(_port.Stance));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Height = 32 };
        row.Children.Add(Centred(_stance));
        row.Children.Add(Centred(new DerethDivider()));
        foreach (var (panel, glyph, name) in WindowButtons)
        {
            var button = Medallion(glyph, 26, 16, name);
            button.Clicked += () => _port.SetPanelOpen(panel, !_port.IsPanelOpen(panel));
            _windows.Add(panel, button);
            row.Children.Add(Centred(button));
        }
        _menu = Medallion(DerethGlyph.LegACEy, 26, 16, "LegACEy menu");
        _menu.Clicked += _openMenu;
        row.Children.Add(Centred(_menu));
        row.Children.Add(Centred(new DerethDivider()));
        row.Children.Add(Centred(Dimmed(Medallion(DerethGlyph.Use, 20, 16, "Use selected"))));
        row.Children.Add(Centred(new Border
        {
            Width = 150,
            Height = 31,
            CornerRadius = new CornerRadius(3),
            Background = DerethPalette.GrooveBrush,
            BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1)
        }));
        row.Children.Add(Centred(Dimmed(Medallion(DerethGlyph.Examine, 20, 16, "Examine selected"))));
        row.Children.Add(Centred(new DerethDivider()));
        var backpack = Medallion(DerethGlyph.Backpack, 34, 21, "Inventory");
        _windows.Add(RetailPanel.Inventory, backpack);
        backpack.Clicked += () => _port.SetPanelOpen(RetailPanel.Inventory, !_port.IsPanelOpen(RetailPanel.Inventory));
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

    /// <summary>Shows the stance, the open windows and the backpack's ring from the port.</summary>
    private void Refresh()
    {
        _stance.Glyph = GlyphOf(_port.Stance);
        foreach (var pair in _windows) pair.Value.IsOpen = _port.IsPanelOpen(pair.Key);
    }

    private static DerethMedallion Medallion(DerethGlyph glyph, double diameter, double glyphSize, string name)
    {
        var medallion = new DerethMedallion(glyph, diameter, glyphSize);
        ToolTip.SetTip(medallion, name);
        return medallion;
    }

    private static DerethMedallion Dimmed(DerethMedallion medallion)
    {
        // Nothing is selected yet, so Use and Examine show as retail shows them dimmed.
        medallion.Opacity = 0.4;
        return medallion;
    }

    private static T Centred<T>(T control) where T : Control
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    private static DerethGlyph GlyphOf(RetailStance stance) => stance switch
    {
        RetailStance.Melee => DerethGlyph.MeleeStance,
        RetailStance.Missile => DerethGlyph.MissileStance,
        RetailStance.Magic => DerethGlyph.MagicStance,
        _ => DerethGlyph.PeaceStance
    };

    /// <summary>Peace, melee, missile, magic, then peace again.</summary>
    private static RetailStance NextStance(RetailStance stance) => stance == RetailStance.Magic ? RetailStance.Peace : (RetailStance)((int)stance * 2);
}
