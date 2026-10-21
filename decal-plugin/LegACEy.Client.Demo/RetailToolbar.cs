using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

/// <summary>A shortcut in retail's shortcut list: its slot index (row 1 is 0 to 8, row 2 is 9 to 17) and the object it uses.</summary>
public readonly record struct RetailShortcut(int Index, uint ObjectId);

/// <summary>What the selection plaque shows: a creature gets a health meter, an item with mana a mana meter, a stack a name only.</summary>
public enum SelectionKind { Other, Creature, ItemWithMana, Stack }

/// <summary>
/// The selected object: its id, name, kind and stack count. <see cref="Meter"/> is the health fraction of a creature or the mana fraction of
/// an item with mana, and null while the server's answer has not come.
/// </summary>
public sealed record RetailSelection(uint Id, string Name, SelectionKind Kind, float? Meter = null, int StackCount = 1);

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
    /// <summary>The shortcuts retail holds, in slot order. Empty slots are left out.</summary>
    IReadOnlyList<RetailShortcut> Shortcuts { get; }
    /// <summary>Uses the shortcut in a slot, as the number key does.</summary>
    void UseShortcut(int index);
    /// <summary>Puts an object into a slot as a shortcut, as retail's add does, so the server keeps it.</summary>
    void AddShortcut(int index, uint objectId);
    /// <summary>Takes the shortcut out of a slot, as retail's remove does, so the server drops it.</summary>
    void RemoveShortcut(int index);
    /// <summary>The selected object, or null when nothing is selected.</summary>
    RetailSelection? Selection { get; }
    /// <summary>The amount retail splits off the selected stack when it is dragged. Set it clamped to 1 and the stack's count.</summary>
    int SplitAmount { get; set; }
    /// <summary>Uses the selected object, as the hand button does.</summary>
    void UseSelected();
    /// <summary>Examines the selected object in the Dereth appraisal window.</summary>
    void ExamineSelected();
    /// <summary>Raised when the stance, the selection, the shortcut list or a panel's open state may have changed.</summary>
    event Action? Changed;
}

/// <summary>
/// The Dereth toolbar that replaces retail's: the stance, six window buttons and the LegACEy menu, then Use, a selection plaque, Examine
/// and the backpack, in one row. The shortcut bars are not in it yet. It shows the port's state and asks the port for each change.
/// </summary>
public sealed class RetailToolbar : Decorator, IRetailItemDropTarget, IItemSetDropTarget
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

    private static readonly IBrush TrackBrush = DerethPalette.Brush(Color.Parse("#0A0906"));
    private static readonly IBrush HealthBrush = Gradient(Color.Parse("#7E211B"), Color.Parse("#D9584A"));
    private static readonly IBrush ManaBrush = Gradient(Color.Parse("#24507F"), Color.Parse("#5AA0E0"));
    // Use and Examine show dimmed while nothing is selected, as retail shows them.
    private const double DimmedOpacity = 0.4;

    private readonly IRetailToolbarPort _port;
    private readonly Action _openMenu;
    private readonly Func<IReadOnlyList<uint>, bool> _moveToMainPack;
    private readonly Action? _startMove;
    private readonly DerethMedallion _stance;
    private readonly Dictionary<RetailPanel, DerethMedallion> _windows = new();
    private readonly DerethMedallion _menu;
    private readonly DerethMedallion _use;
    private readonly DerethMedallion _examine;
    private readonly Border _plaque;
    // The plaque's parts are fixed; a refresh only changes what they show, so a typed figure and the focus survive it.
    private readonly TextBlock _name = new()
    {
        FontFamily = DerethPalette.Title,
        FontSize = 11,
        // The mockup's name line is 11 DIP tall, so the meter fits under it in the plaque's 31.
        LineHeight = 11,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly Border _meterSlot = new() { IsVisible = false };
    private readonly StackPanel _plaqueContent;
    /// <param name="port">Retail's toolbar, as the client reaches it.</param>
    /// <param name="openMenu">Opens or closes the LegACEy menu. The caller shows the menu's state with <see cref="SetMenuOpen"/>.</param>
    /// <param name="moveToMainPack">Moves the items to the player's main pack, in order, as a drop on the backpack does. True when one moved.</param>
    /// <param name="startMove">Run on a press on the frame that no button or control takes, which starts the toolbar's move; null for none.</param>
    public RetailToolbar(IRetailToolbarPort port, Action openMenu, Func<IReadOnlyList<uint>, bool> moveToMainPack, Action? startMove = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _openMenu = openMenu ?? throw new ArgumentNullException(nameof(openMenu));
        _moveToMainPack = moveToMainPack ?? throw new ArgumentNullException(nameof(moveToMainPack));
        _startMove = startMove;

        _stance = new DerethMedallion(GlyphOf(port.Stance), 34, 21);
        ToolTip.SetTip(_stance, "Combat stance");
        _stance.Clicked += () => _port.ToggleStance();

        var row = new DerethItemRow { Spacing = 5 };
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
        _use.Clicked += _port.UseSelected;
        row.Children.Add(Centred(_use));
        _plaqueContent = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { _name, _meterSlot } };
        _plaque = new Border
        {
            // Wide enough for a long NPC's name, such as "Ulgrim the Unpleasant, Bartender"; a longer one ends in an ellipsis, named in full on hover.
            Width = 220,
            Height = 31,
            CornerRadius = new CornerRadius(3),
            Background = DerethPalette.GrooveBrush,
            BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 0),
            Child = _plaqueContent
        };
        row.Children.Add(Centred(_plaque));
        _examine = Medallion(DerethGlyph.Examine, 20, 16, "Examine selected");
        _examine.Clicked += _port.ExamineSelected;
        row.Children.Add(Centred(_examine));
        row.Children.Add(Centred(new DerethDivider()));
        var backpack = Medallion(DerethGlyph.Backpack, 34, 21, "Inventory");
        _windows.Add(RetailPanel.Inventory, backpack);
        backpack.Clicked += () => TogglePanel(RetailPanel.Inventory);
        row.Children.Add(Centred(backpack));

        // A transparent frame takes the press wherever no button or control does (a medallion marks its presses handled), so a press there
        // starts the move.
        var frame = new Border { Padding = new Thickness(4, 0), Background = Brushes.Transparent, Child = row };
        frame.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(frame).Properties.IsLeftButtonPressed) _startMove?.Invoke();
        };
        Child = new DerethFrame(DerethFrameArt.Window) { Child = frame };
        Refresh();
        port.Changed += Refresh;
    }

    /// <summary>Shows the green ring on the LegACEy menu button while the menu is open.</summary>
    public void SetMenuOpen(bool open) => _menu.IsOpen = open;

    /// <summary>A drop on the backpack goes to the main pack, as retail's backpack takes it. Elsewhere on the toolbar it is not taken.</summary>
    public bool RetailDrop(uint itemId, string itemName, Point position) => itemId != 0 && OnBackpack(position) && _moveToMainPack([itemId]);

    /// <summary>A set of items dropped on the backpack goes to the main pack together. Elsewhere on the toolbar it is not taken.</summary>
    public bool DropMany(IReadOnlyList<uint> itemIds, Point position) => OnBackpack(position) && _moveToMainPack(itemIds);

    private bool OnBackpack(Point position)
    {
        var backpack = _windows[RetailPanel.Inventory];
        return backpack.TranslatePoint(new Point(0, 0), this) is { } origin && new Rect(origin, backpack.Bounds.Size).Contains(position);
    }

    /// <summary>The toolbar draws no drop indicator; a drop is judged on release.</summary>
    public void RetailDragOver(uint itemId, string itemName, Point? position) { }

    /// <summary>The button that opens a panel: a window button, or the backpack for the inventory.</summary>
    public DerethMedallion ButtonFor(RetailPanel panel) => _windows[panel];

    public DerethMedallion StanceButton => _stance;

    public DerethMedallion MenuButton => _menu;

    public DerethMedallion UseButton => _use;

    public DerethMedallion ExamineButton => _examine;

    /// <summary>The selection plaque; the client centres the split tab over it.</summary>
    public Control Plaque => _plaque;

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
        _name.Text = selection?.Name ?? "Nothing selected";
        ToolTip.SetTip(_plaque, selection?.Name);
        _name.FontStyle = selection is null ? FontStyle.Italic : FontStyle.Normal;
        _name.Foreground = selection is null ? DerethPalette.MutedBrush : DerethPalette.CreamBrush;
        // No bar while the server's answer is pending, or for an item with no mana.
        Control? meter = selection is { Kind: SelectionKind.Creature or SelectionKind.ItemWithMana, Meter: { } fraction } meterSelection
            ? Meter(meterSelection.Kind == SelectionKind.Creature ? HealthBrush : ManaBrush, fraction)
            : null;
        _meterSlot.IsVisible = meter is not null;
        _meterSlot.Child = meter;
        // Dimmed and inactive while nothing is selected: they take no pointer, so they show no hover or press and do nothing.
        _use.Opacity = _examine.Opacity = selection is null ? DimmedOpacity : 1;
        _use.IsHitTestVisible = _examine.IsHitTestVisible = selection is not null;
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
