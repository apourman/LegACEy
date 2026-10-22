using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Inventory;

/// <summary>
/// The paperdoll block: retail's paperdoll on six columns at the <see cref="ItemCellSize"/> pitch, which follows the window's scale: the left
/// jewellery column, three armour columns over the doll and two right columns, the aetheria and the cloak a square at the top of those, so
/// none of it sits over the doll. Rows keep retail's spacing. It fills the width it is given: the left column keeps to the left edge, the
/// right columns to the right edge, and the doll area takes the rest, with the armour slots centred over it. The doll area is the one control
/// the 3D character fills; the Slots toggle sits in its bottom-left corner. Slots are placed by <see cref="Show"/>.
/// </summary>
internal sealed class InventoryPaperdoll : Canvas
{
    // Retail's cells are 32 px; rows keep retail's spacing scaled to the Dereth pitch (35 px at scale 1), and columns sit one pitch apart.
    private const int RetailCell = 32;
    private const int Columns = 6;
    private const int RetailHeight = 214;
    // The armour columns run from 1 to 3, round which the doll area is drawn, 2 DIPs clear of them on each side; 4 and 5 are the right side.
    // A gap on each side of the armour columns keeps the doll area clear of the left and right columns.
    private const int ArmourLeft = 1, ArmourRight = 3, RightLeft = 4;
    private const double DollClearance = 2, SideGap = 6;
    // The Slots toggle sits in the doll area's bottom-left corner, as retail's does, on one row with the expand button in the
    // bottom-right corner; the doll is inset above that row.
    private const int CornerInset = 4;
    private const int CornerRowHeight = 20;

    private static readonly (PaperdollSlot Slot, int Column, int Y)[] Positions =
    {
        (PaperdollSlot.Neck, 0, 8), (PaperdollSlot.Trinket, 0, 44), (PaperdollSlot.RightWrist, 0, 80), (PaperdollSlot.RightRing, 0, 116),
        (PaperdollSlot.Shield, 0, 172),
        (PaperdollSlot.Head, 2, 28), (PaperdollSlot.UpperArms, 1, 64), (PaperdollSlot.Chest, 2, 64), (PaperdollSlot.LowerArms, 1, 100),
        (PaperdollSlot.Abdomen, 2, 100), (PaperdollSlot.UpperLegs, 3, 100), (PaperdollSlot.Hands, 1, 136), (PaperdollSlot.LowerLegs, 3, 136),
        (PaperdollSlot.Feet, 3, 172),
        // Retail packs the aetheria over the doll's side; here they and the cloak make a square at the top of the right columns.
        (PaperdollSlot.AetheriaOne, 4, 8), (PaperdollSlot.AetheriaTwo, 5, 8), (PaperdollSlot.AetheriaThree, 4, 44), (PaperdollSlot.Cloak, 5, 44),
        (PaperdollSlot.LeftWrist, 4, 80), (PaperdollSlot.LeftRing, 4, 116), (PaperdollSlot.Weapon, 4, 172), (PaperdollSlot.Ammo, 5, 172),
        (PaperdollSlot.Shirt, 5, 80), (PaperdollSlot.Pants, 5, 116),
    };
    /// <summary>The armour slots, which sit over the doll and show only while the Slots toggle is on.</summary>
    private static readonly HashSet<PaperdollSlot> Armour = new()
    {
        PaperdollSlot.Head, PaperdollSlot.UpperArms, PaperdollSlot.Chest, PaperdollSlot.LowerArms, PaperdollSlot.Abdomen,
        PaperdollSlot.UpperLegs, PaperdollSlot.Hands, PaperdollSlot.LowerLegs, PaperdollSlot.Feet,
    };

    private readonly List<(Control Cell, int X, int Y)> _placed = new();
    private ItemCellSize _size, _placedSize;
    // The width the block was placed for, and how much of it is past the narrowest: the doll area takes it.
    private double _width, _spare;
    private readonly Border _slotsMark = new()
    {
        Width = 11, Height = 11, CornerRadius = new CornerRadius(6), BorderBrush = DerethPalette.GoldBrush, BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _slotsLabel = new() { Text = "Slots", FontSize = 11, FontFamily = DerethPalette.Body, VerticalAlignment = VerticalAlignment.Center };

    public InventoryPaperdoll()
    {
        DollArea = new Border
        {
            Name = "DollArea",
            // No outline: the doll stands on its backdrop.
            CornerRadius = new CornerRadius(3),
            Background = new RadialGradientBrush { GradientStops = { new GradientStop(Color.Parse("#2A2519"), 0), new GradientStop(Color.Parse("#0A0805"), 1) } },
        };
        Children.Add(DollArea);

        // A plain row, not a framed button: the toggle reads as text under the doll, as the retail checkbox does. It acts on release.
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        label.Children.Add(_slotsMark);
        label.Children.Add(_slotsLabel);
        SlotsToggle = new Border { Name = "SlotsToggle", Background = Brushes.Transparent, Padding = new Thickness(2, 0), Height = CornerRowHeight, Child = label };
        SlotsToggle.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) SlotsPressed?.Invoke();
        };
        Children.Add(SlotsToggle);

        // In the doll area's bottom-right corner, over the character.
        var expand = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(ExpandGlyph), Width = 12, Height = 12, StrokeThickness = 1.2, UseLayoutRounding = false, Stroke = DerethPalette.CreamBrush,
        };
        ExpandButton = new DerethButton { Name = "ExpandDoll", Width = 20, Height = 20, Content = expand, IsVisible = false };
        ToolTip.SetTip(ExpandButton, "Inspect in a larger window");
        Children.Add(ExpandButton);
    }

    // Everything is placed from the pitch, which follows the window's scale and can change without a rebuild, and from the width given.
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = ItemCellSize.Of(this);
        _size = size;
        var width = Math.Max(Columns * size.Pitch + 2 * SideGap, double.IsInfinity(availableSize.Width) ? 0 : Math.Floor(availableSize.Width));
        if (size != _placedSize || width != _width) Place(size, width);
        base.MeasureOverride(availableSize);
        return new Size(width, Scale(RetailHeight));
    }

    private void Place(ItemCellSize size, double width)
    {
        _placedSize = size;
        _width = width;
        _spare = width - Columns * size.Pitch - 2 * SideGap;
        var height = Scale(RetailHeight);
        DollArea.Width = (ArmourRight - ArmourLeft) * size.Pitch + size.Cell + 2 * DollClearance + _spare;
        DollArea.Height = height;
        // The panel keeps its full size, and whatever is put in it (the 3D character) is inset to clear the corner row.
        DollArea.Padding = new Thickness(0, CornerInset, 0, CornerRowHeight + 2 * CornerInset);
        SetLeft(DollArea, ArmourLeft * size.Pitch + SideGap + 1 - DollClearance);
        SetLeft(SlotsToggle, GetLeft(DollArea) + CornerInset);
        SetTop(SlotsToggle, height - CornerRowHeight - CornerInset);
        SetLeft(ExpandButton, GetLeft(DollArea) + DollArea.Width - ExpandButton.Width - CornerInset);
        SetTop(ExpandButton, height - CornerRowHeight - CornerInset);
        foreach (var (cell, column, y) in _placed) PlaceCell(cell, column, y);
    }

    private void PlaceCell(Control cell, int column, int y)
    {
        // The left column stays at the left edge, the armour over the doll moves half the spare width, and the right side all of it.
        var shift = column >= RightLeft ? 2 * SideGap + _spare : column >= ArmourLeft ? SideGap + Math.Round(_spare / 2) : 0;
        SetLeft(cell, column * _size.Pitch + shift + 1);
        SetTop(cell, Scale(y) + 1);
    }

    private const string ExpandGlyph = "M1,4.5 V1 H4.5 M7.5,1 H11 V4.5 M11,7.5 V11 H7.5 M4.5,11 H1 V7.5";

    /// <summary>Opens the character in the Inspect window. Shown only while the 3D character is.</summary>
    public DerethButton ExpandButton { get; }

    /// <summary>The doll area: empty while the armour slots show, and the 3D character once a doll is put in it.</summary>
    public Border DollArea { get; }

    /// <summary>The Slots toggle under the doll. Its state shows through <see cref="SetSlotsOn"/>.</summary>
    public Border SlotsToggle { get; }

    /// <summary>Raised when the Slots toggle is pressed and released with the left button.</summary>
    public event Action? SlotsPressed;

    /// <summary>Sets the Slots toggle's lit state.</summary>
    public void SetSlotsOn(bool on)
    {
        _slotsMark.Background = on ? DerethPalette.TealBrush : DerethPalette.GrooveBrush;
        _slotsMark.BorderBrush = on ? DerethPalette.TealBrush : DerethPalette.GoldBrush;
        _slotsLabel.Foreground = on ? DerethPalette.CreamBrush : DerethPalette.MutedBrush;
    }

    /// <summary>
    /// Places one control per paperdoll slot, from <paramref name="slotFor"/>, and replaces the last placement. The armour
    /// slots are left out while <paramref name="showArmour"/> is off.
    /// </summary>
    public void Show(Func<PaperdollSlot, Control> slotFor, bool showArmour)
    {
        foreach (var (control, _, _) in _placed) Children.Remove(control);
        _placed.Clear();
        foreach (var (slot, x, y) in Positions)
        {
            if (!showArmour && Armour.Contains(slot)) continue;
            var cell = slotFor(slot);
            PlaceCell(cell, x, y);
            Children.Add(cell);
            _placed.Add((cell, x, y));
        }
    }

    /// <summary>Retail's position, scaled to the pitch and rounded to whole pixels.</summary>
    private double Scale(int retail) => Math.Round(retail * _size.Pitch / RetailCell);
}
