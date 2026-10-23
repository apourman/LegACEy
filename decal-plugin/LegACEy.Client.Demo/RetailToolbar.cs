using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
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
    /// <summary>Selects an object, as a click on it in a retail item list does, so it can be examined.</summary>
    void Select(uint objectId);
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
/// The Dereth toolbar that replaces retail's, laid out as retail's is: the stance on the left and the backpack on the right; between them
/// six window buttons and the LegACEy menu, over Use, a selection plaque and Examine; and the shortcut rows below, inside the same frame and
/// as wide as the part above. The rows set the width, so the toolbar is retail's: nine touching 32 DIP slots. Given game art, the stance and
/// the backpack are retail's own buttons. It shows the port's state and asks the port for each change.
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

    // Retail's stance buttons in layout 0x21000016, normal then highlighted, and its backpack shut then open (states 1 and 6). Each is drawn
    // cropped to its painting: retail's art carries the gold strips and corner blocks of retail's toolbar frame, which the Dereth frame replaces.
    private static readonly Dictionary<RetailStance, (uint Normal, uint Hover)> StanceArtIds = new()
    {
        [RetailStance.Peace] = (0x06004CEC, 0x06004CED),
        [RetailStance.Melee] = (0x06004CEE, 0x06004CEF),
        [RetailStance.Missile] = (0x06004CF0, 0x06004CF1),
        [RetailStance.Magic] = (0x06004CF2, 0x06004CF3),
    };
    private const uint BackpackShutArt = 0x06004CF7, BackpackOpenArt = 0x06004CF8;
    private static readonly PixelRect StancePainting = new(4, 4, 51, 50), BackpackPainting = new(2, 4, 56, 50);

    private static readonly IBrush TrackBrush = DerethPalette.Brush(Color.Parse("#0A0906"));
    private static readonly IBrush HealthBrush = Gradient(Color.Parse("#7E211B"), Color.Parse("#D9584A"));
    private static readonly IBrush ManaBrush = Gradient(Color.Parse("#24507F"), Color.Parse("#5AA0E0"));
    // Use and Examine show dimmed while nothing is selected, as retail shows them.
    private const double DimmedOpacity = 0.4;

    private readonly IRetailToolbarPort _port;
    private readonly Action _openMenu;
    private readonly Func<IReadOnlyList<uint>, bool> _moveToMainPack;
    private readonly Action? _startMove;
    // Retail's stance art by stance; empty without game art, when the stance is a medallion.
    private readonly Dictionary<RetailStance, (IImage Normal, IImage? Hover)> _stanceArt = new();
    private readonly DerethMedallion _stance;
    private readonly Dictionary<RetailPanel, DerethMedallion> _windows = new();
    private readonly DerethMedallion _menu;
    private readonly DerethMedallion _use;
    private readonly DerethMedallion _examine;
    private readonly Border _plaque;
    // The plaque's parts are fixed; a refresh only changes what they show, so a typed figure and the focus survive it.
    private readonly FittedName _name = new()
    {
        FontFamily = DerethPalette.Title,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly Border _meterSlot = new() { IsVisible = false };
    private readonly StackPanel _plaqueContent;
    // The part above the rows, then the shortcut rows in order, touching as retail's do.
    private readonly StackPanel _sections = new();
    /// <param name="port">Retail's toolbar, as the client reaches it.</param>
    /// <param name="openMenu">Opens or closes the LegACEy menu. The caller shows the menu's state with <see cref="SetMenuOpen"/>.</param>
    /// <param name="moveToMainPack">Moves the items to the player's main pack, in order, as a drop on the backpack does. True when one moved.</param>
    /// <param name="startMove">Run on a press on the frame that no button or control takes, which starts the toolbar's move; null for none.</param>
    /// <param name="art">Game art for retail's stance and backpack buttons; null draws them as medallions.</param>
    public RetailToolbar(IRetailToolbarPort port, Action openMenu, Func<IReadOnlyList<uint>, bool> moveToMainPack, Action? startMove = null,
        IGameArtSource? art = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _openMenu = openMenu ?? throw new ArgumentNullException(nameof(openMenu));
        _moveToMainPack = moveToMainPack ?? throw new ArgumentNullException(nameof(moveToMainPack));
        _startMove = startMove;

        _stance = new DerethMedallion(GlyphOf(port.Stance), 40, 25);
        ToolTip.SetTip(_stance, "Combat stance");
        _stance.Clicked += () => _port.ToggleStance();

        foreach (var pair in StanceArtIds)
            if (Painting(art, pair.Value.Normal, StancePainting) is { } normal)
                _stanceArt[pair.Key] = (normal, Painting(art, pair.Value.Hover, StancePainting));

        // Spaced so the open ring, 2.75 DIPs outside a button, stays clear of the next one.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (panel, glyph, name) in WindowButtons)
        {
            var button = Medallion(glyph, 20, 13, name);
            button.Clicked += () => TogglePanel(panel);
            _windows.Add(panel, button);
            buttons.Children.Add(button);
        }
        _menu = Medallion(DerethGlyph.LegACEy, 20, 13, "LegACEy menu");
        _menu.Clicked += _openMenu;
        buttons.Children.Add(_menu);
        _use = Medallion(DerethGlyph.Use, 18, 14, "Use selected");
        _use.Clicked += _port.UseSelected;
        _plaqueContent = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { _name, _meterSlot } };
        // The plaque takes the width the buttons around it leave; a long name wraps to two smaller lines, as retail's does.
        _plaque = new Border
        {
            // Even, as the buttons beside it are, so they centre on whole DIPs. The padding's odd top leaves an odd height inside, so the name
            // and the meter centre on whole DIPs too: a half-DIP name redrew the buttons' edges on a refresh.
            Height = 34,
            Margin = new Thickness(3, 0),
            CornerRadius = new CornerRadius(3),
            Background = DerethPalette.GrooveBrush,
            BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 1, 5, 0),
            Child = _plaqueContent
        };
        _examine = Medallion(DerethGlyph.Examine, 18, 14, "Examine selected");
        _examine.Clicked += _port.ExamineSelected;
        var selection = Columns(Centred(_use), new NoWidth { Child = _plaque }, Centred(_examine));
        var middle = new StackPanel { Spacing = 4, Margin = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center, Children = { buttons, selection } };
        var backpack = Medallion(DerethGlyph.Backpack, 40, 25, "Inventory");
        if (Painting(art, BackpackShutArt, BackpackPainting) is { } shut)
            backpack.SetArt(shut, open: Painting(art, BackpackOpenArt, BackpackPainting));
        _windows.Add(RetailPanel.Inventory, backpack);
        backpack.Clicked += () => TogglePanel(RetailPanel.Inventory);
        _sections.Children.Add(Columns(Centred(_stance), middle, Centred(backpack)));

        // A transparent backing takes the press wherever no button or control does (a medallion marks its presses handled), the frame's edge
        // included, so a press there starts the move. The frame holds the sections with no padding, so the toolbar is retail's width.
        var frame = new Border { Background = Brushes.Transparent, Child = new DerethFrame(DerethFrameArt.Window) { Child = _sections } };
        frame.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(frame).Properties.IsLeftButtonPressed) _startMove?.Invoke();
        };
        Child = frame;
        Refresh();
        port.Changed += Refresh;
    }

    /// <summary>Shows the green ring on the LegACEy menu button while the menu is open.</summary>
    public void SetMenuOpen(bool open) => _menu.IsOpen = open;

    /// <summary>
    /// Shows these shortcut rows under the buttons, in order, in place of the ones shown before. The toolbar does not own them: the caller
    /// disposes a row it takes away.
    /// </summary>
    public void SetShortcutRows(IEnumerable<ShortcutBar> rows)
    {
        _sections.Children.RemoveAll(Rows.ToList());
        _sections.Children.AddRange(rows);
    }

    private IEnumerable<ShortcutBar> Rows => _sections.Children.OfType<ShortcutBar>();

    /// <summary>
    /// A drop on a shortcut row goes to that row. A drop on the backpack goes to the main pack, as retail's backpack takes it, unless it is a
    /// shortcut dragged off a row. Elsewhere on the toolbar it is not taken.
    /// </summary>
    public bool RetailDrop(uint itemId, string itemName, Point position)
    {
        foreach (var row in Rows)
            if (Within(row, position) is { } point) return row.RetailDrop(itemId, itemName, point);
        return itemId != 0 && !ShortcutInHand && OnBackpack(position) && _moveToMainPack([itemId]);
    }

    /// <summary>A set of items dropped on the backpack goes to the main pack together. Elsewhere on the toolbar it is not taken.</summary>
    public bool DropMany(IReadOnlyList<uint> itemIds, Point position) => OnBackpack(position) && _moveToMainPack(itemIds);

    // The rows share one drag source, so any of them knows.
    private bool ShortcutInHand => Rows.FirstOrDefault()?.ShortcutInHand == true;

    /// <summary>The position in the row's own coordinates when it is on the row; null otherwise.</summary>
    private Point? Within(Control row, Point position) =>
        this.TranslatePoint(position, row) is { } point && new Rect(row.Bounds.Size).Contains(point) ? point : null;

    private bool OnBackpack(Point position)
    {
        var backpack = _windows[RetailPanel.Inventory];
        return backpack.TranslatePoint(new Point(0, 0), this) is { } origin && new Rect(origin, backpack.Bounds.Size).Contains(position);
    }

    /// <summary>A drag over a shortcut row lights the slot it would land on; elsewhere the toolbar draws no drop indicator.</summary>
    public void RetailDragOver(uint itemId, string itemName, Point? position)
    {
        foreach (var row in Rows) row.RetailDragOver(itemId, itemName, position is { } point ? Within(row, point) : null);
    }

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
        if (_stanceArt.TryGetValue(_port.Stance, out var stanceArt)) _stance.SetArt(stanceArt.Normal, stanceArt.Hover);
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

    /// <summary>The part of a retail image inside <paramref name="painting"/>; null when there is no art.</summary>
    private static IImage? Painting(IGameArtSource? art, uint id, PixelRect painting) =>
        GameArtImageExtension.CreateBitmap(art, id) is { } bitmap ? new CroppedBitmap(bitmap, painting) : null;

    private static DerethMedallion Medallion(DerethGlyph glyph, double diameter, double glyphSize, string name)
    {
        var medallion = new DerethMedallion(glyph, diameter, glyphSize);
        ToolTip.SetTip(medallion, name);
        return medallion;
    }

    /// <summary>Three controls side by side; the middle one takes the width the outer two leave.</summary>
    private static Grid Columns(Control left, Control middle, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { left, middle, right } };
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        return grid;
    }

    /// <summary>
    /// The selection's name: one 13 DIP line when it fits, or else up to two 11 DIP lines, as retail wraps a long name; a name too long for
    /// those ends in an ellipsis. The plaque's 31 DIPs inside hold either with the meter, and each is an odd height, as the inside is.
    /// </summary>
    private sealed class FittedName : TextBlock
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            // Laid out apart from the block's own layout, which keeps the width it was last measured at.
            using var oneLine = new Avalonia.Media.TextFormatting.TextLayout(Text, new Typeface(FontFamily, FontStyle, FontWeight), 13, null);
            Fit(wrapped: oneLine.WidthIncludingTrailingWhitespace > availableSize.Width - Padding.Left - Padding.Right);
            return base.MeasureOverride(availableSize);
        }

        // Set only on a change: each set asks for a new measure.
        private void Fit(bool wrapped)
        {
            var (size, line, wrapping, lines) = wrapped ? (11, 11.5, TextWrapping.Wrap, 2) : (13, 13, TextWrapping.NoWrap, 1);
            if (FontSize != size) FontSize = size;
            if (LineHeight != line) LineHeight = line;
            if (TextWrapping != wrapping) TextWrapping = wrapping;
            if (MaxLines != lines) MaxLines = lines;
        }
    }

    /// <summary>Asks for no width of its own, so a long name in it never widens the toolbar; it takes the width it is given.</summary>
    private sealed class NoWidth : Decorator
    {
        protected override Size MeasureOverride(Size availableSize) => base.MeasureOverride(availableSize).WithWidth(0);
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
