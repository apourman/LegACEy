using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
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
    /// <summary>The selected object, or null when nothing is selected.</summary>
    RetailSelection? Selection { get; }
    /// <summary>The amount retail splits off the selected stack when it is dragged. Set it clamped to 1 and the stack's count.</summary>
    int SplitAmount { get; set; }
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
    private static readonly IBrush AmountEdge = DerethPalette.Brush(Color.Parse("#7A5E30"));
    private static readonly IBrush ThumbBrush = Gradient(Color.Parse("#7A5E30"), Color.Parse("#D9B66A"));
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
    // The plaque's parts are fixed; a refresh only changes what they show, so a typed figure and the focus survive it.
    private readonly TextBlock _name = new()
    {
        FontFamily = DerethPalette.Title,
        FontSize = 11,
        // The mockup's name line is 11 DIP tall, so the amount row fits under it in the plaque's 31.
        LineHeight = 11,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly Border _amountSlot = new() { IsVisible = false };
    private readonly Border _meterSlot = new() { IsVisible = false };
    private readonly StackPanel _plaqueContent;
    // The amount row of the stack being split: its stack, the count it shows, whether a write is in progress, and its controls.
    private uint _amountStackId;
    private int _amountSize;
    private bool _amountBusy;
    private Grid? _amountRow;
    private TextBox? _amountBox;
    private Slider? _amountSlider;

    /// <summary>The stack's amount box, shown while a stack is selected; null otherwise.</summary>
    public TextBox? AmountBox => _amountBox;

    /// <summary>The stack's amount slider, shown while a stack is selected; null otherwise.</summary>
    public Slider? AmountSlider => _amountSlider;

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
        _use.Clicked += _port.UseSelected;
        row.Children.Add(Centred(_use));
        _plaqueContent = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { _name, _amountSlot, _meterSlot } };
        _plaque = new Border
        {
            Width = 150,
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
        _name.Text = selection?.Name ?? "Nothing selected";
        _name.FontStyle = selection is null ? FontStyle.Italic : FontStyle.Normal;
        _name.Foreground = selection is null ? DerethPalette.MutedBrush : DerethPalette.CreamBrush;
        // A stack's amount row stays while that stack stays selected; another object drops it.
        var stack = selection is { Kind: SelectionKind.Stack } stackSelection ? stackSelection : null;
        _amountSlot.IsVisible = stack is not null;
        var row = stack is null ? ForgetAmount() : AmountRow(stack);
        if (!ReferenceEquals(_amountSlot.Child, row)) _amountSlot.Child = row;
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

    /// <summary>The amount row for a stack: the same row while the stack stays selected, a new one for another stack.</summary>
    private Control AmountRow(RetailSelection stack)
    {
        if (_amountRow is null || _amountStackId != stack.Id) BuildAmountRow(stack.Id);
        ShowAmount(stack.StackCount);
        return _amountRow!;
    }

    /// <summary>Drops the amount row once no stack is selected. Its controls are no longer the current ones, so their focus-loss writes nothing.</summary>
    private Control? ForgetAmount()
    {
        _amountStackId = 0;
        _amountRow = null;
        _amountBox = null;
        _amountSlider = null;
        return null;
    }

    /// <summary>
    /// The amount box and slider of a split. Typing or sliding writes the amount, clamped to 1 and the stack's count; the box takes the
    /// clamped figure when it loses focus or Enter is pressed. Events from a row that is no longer the current one write nothing.
    /// </summary>
    private void BuildAmountRow(uint stackId)
    {
        var box = new TextBox
        {
            Foreground = DerethPalette.TextBrush, CaretBrush = DerethPalette.GoldBrush, FontFamily = DerethPalette.Body, FontSize = 8,
            Background = TrackBrush, BorderBrush = AmountEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Padding = new Thickness(0), MinHeight = 0, Height = 11, TextAlignment = TextAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Minimum = 1, TickFrequency = 1, IsSnapToTickEnabled = true, Template = AmountSliderTemplate(),
            VerticalAlignment = VerticalAlignment.Center, Height = 10
        };
        _amountStackId = stackId;
        _amountBox = box;
        _amountSlider = slider;
        box.TextChanged += (_, _) =>
        {
            // Only typing writes: the text the plaque sets itself can raise this event after the refresh that set it.
            if (box == _amountBox && box.IsFocused && int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var typed)) Apply(typed, box);
        };
        box.LostFocus += (_, _) => Settle(box);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Settle(box); };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && slider == _amountSlider) Apply((int)Math.Round(slider.Value), slider);
        };
        Grid.SetColumn(slider, 1);
        _amountRow = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*"), ColumnSpacing = 5, Children = { box, slider } };
    }

    /// <summary>Shows the stack's amount at the port's figure, clamped to the count. A box being typed in keeps its text.</summary>
    private void ShowAmount(int size)
    {
        _amountSize = size;
        var amount = Between(_port.SplitAmount, 1, size);
        _amountBusy = true;
        try
        {
            _amountSlider!.Maximum = size;
            _amountSlider.Value = amount;
            if (!_amountBox!.IsFocused) _amountBox.Text = amount.ToString(CultureInfo.InvariantCulture);
        }
        finally { _amountBusy = false; }
    }

    /// <summary>Writes an amount, clamped to 1 and the count, and shows it in the control that did not set it; source null shows it in both.</summary>
    private void Apply(int amount, Control? source)
    {
        if (_amountBusy || _amountBox is null || _amountSlider is null) return;
        _amountBusy = true;
        try
        {
            amount = Between(amount, 1, _amountSize);
            _port.SplitAmount = amount;
            if (source != _amountBox) _amountBox.Text = amount.ToString(CultureInfo.InvariantCulture);
            if (source != _amountSlider) _amountSlider.Value = amount;
        }
        finally { _amountBusy = false; }
    }

    /// <summary>Enter or losing focus settles the box on the figure typed, or the amount in force when none parses.</summary>
    private void Settle(TextBox box)
    {
        if (box != _amountBox) return;
        Apply(int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var typed) ? typed : _port.SplitAmount, null);
    }

    /// <summary>A dark track with a gold thumb, as the retail amount slider draws.</summary>
    private static FuncControlTemplate<Slider> AmountSliderTemplate() => new((_, scope) =>
    {
        var track = new Track
        {
            Name = "PART_Track",
            Orientation = Orientation.Horizontal,
            [!RangeBase.MinimumProperty] = new TemplateBinding(RangeBase.MinimumProperty),
            [!RangeBase.MaximumProperty] = new TemplateBinding(RangeBase.MaximumProperty),
            [!RangeBase.ValueProperty] = new Binding(nameof(RangeBase.Value)) { Mode = BindingMode.TwoWay, RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) }
        };
        track.Thumb = new Thumb { Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Width = 6, Height = 10, CornerRadius = new CornerRadius(2), Background = ThumbBrush }) };
        scope.Register(track.Name, track);
        return new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), Background = TrackBrush, BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = track
        };
    });

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

    /// <summary>The value held within [<paramref name="low"/>, <paramref name="high"/>]. netstandard2.0 has no Math.Clamp.</summary>
    private static int Between(int value, int low, int high) => Math.Min(Math.Max(value, low), high);

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
