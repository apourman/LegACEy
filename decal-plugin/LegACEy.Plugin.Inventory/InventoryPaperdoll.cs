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
/// The paperdoll block: retail's paperdoll on six columns at the <see cref="ItemCellSize"/> pitch: the left
/// jewellery column, three armour columns over the doll and two right columns. The aetheria are one row across the top right, as retail's,
/// the first over the doll area's corner, so the doll stands below that row. Rows keep retail's spacing. It fills the width it is given: the left column keeps to the left edge, the
/// right columns to the right edge, and the doll area takes the rest, with the armour slots centred over it. The doll area is the one control
/// the 3D character fills; the Slots toggle sits in its bottom-left corner. Slots are placed by <see cref="Show"/>.
/// </summary>
internal sealed class InventoryPaperdoll : Canvas
{
    // Retail's cells are 32 px; rows keep retail's spacing scaled to the Dereth pitch (35 px at scale 1), and columns sit one pitch apart.
    private const int RetailCell = 32;
    private const int Columns = 6;
    // Retail's rows less its 8 px above the first and 10 px below the last, so the grid under the block shows more rows (Backlog 067).
    private const int RetailHeight = 196;
    // The armour columns run from 1 to 3, round which the doll area is drawn, 2 DIPs clear of them on each side; 4 and 5 are the right side.
    // A gap on each side of the armour columns keeps the doll area clear of the left and right columns.
    private const int ArmourLeft = 1, ArmourRight = 3, RightLeft = 4;
    private const double DollClearance = 2, SideGap = 6;
    // The Slots toggle sits in the doll area's bottom-left corner, as retail's does, on one row with the expand button in the
    // bottom-right corner; the doll takes all the height above that row, as retail's fills its panel. The host draws the doll over
    // the window, so it stays off the row rather than hide the toggle.
    private const int CornerInset = 4;
    private const int CornerRowHeight = 20;
    // The aetheria row's top. The row moves with the right side, so its cells stay one pitch apart.
    private const int AetheriaY = 0;
    // How far the doll area keeps clear of the aetheria row, beside and under the first aetheria.
    private const double AetheriaGap = 4;
    private static readonly HashSet<PaperdollSlot> Aetheria = new() { PaperdollSlot.AetheriaOne, PaperdollSlot.AetheriaTwo, PaperdollSlot.AetheriaThree };

    private static readonly (PaperdollSlot Slot, int Column, int Y)[] Positions =
    {
        (PaperdollSlot.Neck, 0, 0), (PaperdollSlot.Trinket, 0, 36), (PaperdollSlot.RightWrist, 0, 72), (PaperdollSlot.RightRing, 0, 108),
        (PaperdollSlot.Shield, 0, 164),
        (PaperdollSlot.Head, 2, 20), (PaperdollSlot.UpperArms, 1, 56), (PaperdollSlot.Chest, 2, 56), (PaperdollSlot.LowerArms, 1, 92),
        (PaperdollSlot.Abdomen, 2, 92), (PaperdollSlot.UpperLegs, 3, 92), (PaperdollSlot.Hands, 1, 128), (PaperdollSlot.LowerLegs, 3, 128),
        (PaperdollSlot.Feet, 3, 164),
        // The aetheria are one row, as retail's, the first over the doll area's corner; the cloak heads the clothing column.
        (PaperdollSlot.AetheriaOne, 3, AetheriaY), (PaperdollSlot.AetheriaTwo, 4, AetheriaY), (PaperdollSlot.AetheriaThree, 5, AetheriaY),
        (PaperdollSlot.Cloak, 5, 36),
        (PaperdollSlot.LeftWrist, 4, 72), (PaperdollSlot.LeftRing, 4, 108), (PaperdollSlot.Weapon, 4, 164), (PaperdollSlot.Ammo, 5, 164),
        (PaperdollSlot.Shirt, 5, 72), (PaperdollSlot.Pants, 5, 108),
    };
    /// <summary>The armour slots, which sit over the doll and show only while the Slots toggle is on.</summary>
    private static readonly HashSet<PaperdollSlot> Armour = new()
    {
        PaperdollSlot.Head, PaperdollSlot.UpperArms, PaperdollSlot.Chest, PaperdollSlot.LowerArms, PaperdollSlot.Abdomen,
        PaperdollSlot.UpperLegs, PaperdollSlot.Hands, PaperdollSlot.LowerLegs, PaperdollSlot.Feet,
    };

    private readonly List<(Control Cell, int X, int Y, bool Right)> _placed = new();
    private static readonly ItemCellSize Cells = ItemCellSize.Standard;
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
        var expand = new DerethPathGlyph(ExpandGlyph) { Stroke = DerethPalette.CreamBrush, StrokeThickness = 1.2 };
        // As tall as the corner row it sits in.
        ExpandButton = new DerethHeaderButton(expand) { Name = "ExpandDoll", Width = CornerRowHeight, Height = CornerRowHeight, IsVisible = false };
        ToolTip.SetTip(ExpandButton, "Inspect in a larger window");
        Children.Add(ExpandButton);
    }

    // Everything is placed from the pitch and from the width given, again when the width changes.
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Max(Columns * Cells.Pitch + 2 * SideGap, double.IsInfinity(availableSize.Width) ? 0 : Math.Floor(availableSize.Width));
        if (width != _width) Place(width);
        base.MeasureOverride(availableSize);
        return new Size(width, Scale(RetailHeight));
    }

    private void Place(double width)
    {
        _width = width;
        _spare = width - Columns * Cells.Pitch - 2 * SideGap;
        var height = Scale(RetailHeight);
        DollArea.Width = (ArmourRight - ArmourLeft) * Cells.Pitch + Cells.Cell + 2 * DollClearance + _spare;
        DollArea.Height = height;
        SetLeft(DollArea, ArmourLeft * Cells.Pitch + SideGap + 1 - DollClearance);
        // The first aetheria reaches over the doll area's top-right corner, so that corner is cut away round it, a gap clear of its
        // cell, and the cell sits in the window's own space beside the doll.
        var rowBottom = Scale(AetheriaY) + Cells.Cell + AetheriaGap;
        var notchLeft = 3 * Cells.Pitch + 2 * SideGap + _spare + 1 - AetheriaGap - GetLeft(DollArea);
        // The doll stands above the corner row with its head beside the aetheria row; the host draws only the figure, not its view.
        // Starting a third of the way from the panel top to that row's bottom puts the stance's shoulders below the first aetheria and its gap.
        DollArea.Padding = new Thickness(0, rowBottom / 3, 0, CornerRowHeight + 2 * CornerInset);
        DollArea.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, DollArea.Width, height)), new RectangleGeometry(new Rect(notchLeft, 0, DollArea.Width - notchLeft, rowBottom)));
        SetLeft(SlotsToggle, GetLeft(DollArea) + CornerInset);
        SetTop(SlotsToggle, height - CornerRowHeight - CornerInset);
        SetLeft(ExpandButton, GetLeft(DollArea) + DollArea.Width - ExpandButton.Width - CornerInset);
        SetTop(ExpandButton, height - CornerRowHeight - CornerInset);
        foreach (var (cell, column, y, right) in _placed) PlaceCell(cell, column, y, right);
    }

    private void PlaceCell(Control cell, int column, int y, bool right)
    {
        // The left column stays at the left edge, the armour over the doll moves half the spare width, and the right side all of it.
        var shift = right ? 2 * SideGap + _spare : column >= ArmourLeft ? SideGap + Math.Round(_spare / 2) : 0;
        SetLeft(cell, column * Cells.Pitch + shift + 1);
        SetTop(cell, Scale(y) + 1);
    }

    private const string ExpandGlyph = "M1,4.5 V1 H4.5 M7.5,1 H11 V4.5 M11,7.5 V11 H7.5 M4.5,11 H1 V7.5";

    /// <summary>Opens the character in the Inspect window. Shown only while the 3D character is.</summary>
    public DerethHeaderButton ExpandButton { get; }

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
        foreach (var (control, _, _, _) in _placed) Children.Remove(control);
        _placed.Clear();
        foreach (var (slot, x, y) in Positions)
        {
            if (!showArmour && Armour.Contains(slot)) continue;
            var cell = slotFor(slot);
            var right = x >= RightLeft || Aetheria.Contains(slot);
            PlaceCell(cell, x, y, right);
            Children.Add(cell);
            _placed.Add((cell, x, y, right));
        }
    }

    /// <summary>Retail's position, scaled to the pitch and rounded to whole pixels.</summary>
    private double Scale(int retail) => Math.Round(retail * Cells.Pitch / RetailCell);
}
