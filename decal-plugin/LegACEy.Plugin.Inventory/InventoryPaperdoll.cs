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
/// The paperdoll block: retail's paperdoll on six columns at retail's 32 px pitch (<see cref="Cells"/>). They are the left jewellery
/// column, three armour columns over the doll and two right columns. The aetheria are one row across the top right, as retail's, the
/// first over the doll area's corner, beside the doll's head. Rows keep retail's spacing.
/// The block is as wide as retail's, whatever width it is given, so its columns sit where retail's do and the window's room to their right
/// stays empty, as retail's does. The doll area is the one control the 3D character fills; the Slots toggle sits in its bottom-left corner,
/// under the doll's feet, as retail's does. Slots are placed by <see cref="Show"/>.
/// </summary>
internal sealed class InventoryPaperdoll : Canvas
{
    /// <summary>
    /// Retail's cells: 32 px icons that touch, 32 px apart, not the Dereth 35 px pitch, so the inventory holds as many rows as
    /// retail's (Backlog 067). Its slots are flush <see cref="DerethSlot"/>s, and the grid under the block uses the same pitch.
    /// </summary>
    internal static readonly ItemCellSize Cells = new(32, 32, 32);
    private const int Columns = 6;
    // Retail's rows less its 8 px above the first and 10 px below the last, so the grid under the block shows more rows (Backlog 067).
    private const int BlockHeight = 196;
    // The armour columns run from 1 to 3, over the doll; 4 and 5 are the right side. A gap on each side of the armour columns sets the
    // left and right columns where retail's are, and the doll area spans the armour columns and both gaps, 2 DIPs clear of the side columns.
    private const int ArmourLeft = 1, RightLeft = 4;
    private const double DollClearance = 2, SideGap = 9;
    private static double BlockWidth => Columns * Cells.Pitch + 2 * SideGap;
    // The Slots toggle sits in the doll area's bottom-left corner, as retail's does, on one row with the expand button in the
    // bottom-right corner. The doll takes all the height above that row: the host draws it over the window, so it stays off the row
    // rather than hide the toggle under a foot.
    private const int CornerInset = 3;
    private const int CornerRowHeight = DerethWindow.HeaderButtonSize;
    // How far the doll area keeps clear of the first aetheria, beside and under it.
    private const double AetheriaGap = 4;
    // The doll's view starts this far down, the same for every race, so only the head stands beside the aetheria row: a Lugian's
    // shoulders, the highest and widest, start under the first aetheria rather than over it (Backlog 067).
    private const double DollTop = 16;
    private static readonly HashSet<PaperdollSlot> Aetheria = new() { PaperdollSlot.AetheriaOne, PaperdollSlot.AetheriaTwo, PaperdollSlot.AetheriaThree };

    private static readonly (PaperdollSlot Slot, int Column, int Y)[] Positions =
    {
        (PaperdollSlot.Neck, 0, 0), (PaperdollSlot.Trinket, 0, 36), (PaperdollSlot.RightWrist, 0, 72), (PaperdollSlot.RightRing, 0, 108),
        (PaperdollSlot.Shield, 0, 164),
        (PaperdollSlot.Head, 2, 20), (PaperdollSlot.UpperArms, 1, 56), (PaperdollSlot.Chest, 2, 56), (PaperdollSlot.LowerArms, 1, 92),
        (PaperdollSlot.Abdomen, 2, 92), (PaperdollSlot.UpperLegs, 3, 92), (PaperdollSlot.Hands, 1, 128), (PaperdollSlot.LowerLegs, 3, 128),
        (PaperdollSlot.Feet, 3, 164),
        // The aetheria are one row, as retail's, the first over the doll area's corner; the cloak heads the clothing column.
        (PaperdollSlot.AetheriaOne, 3, 0), (PaperdollSlot.AetheriaTwo, 4, 0), (PaperdollSlot.AetheriaThree, 5, 0),
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

    private readonly List<Control> _placed = new();
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

        // A plain row, not a framed button: the toggle reads as text in the doll area's corner, as the retail checkbox does. It acts on release.
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
        Place();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(BlockWidth, BlockHeight);
    }

    private void Place()
    {
        DollArea.Width = 3 * Cells.Pitch + 2 * SideGap - 2 * DollClearance;
        DollArea.Height = BlockHeight;
        SetLeft(DollArea, ArmourLeft * Cells.Pitch + DollClearance);
        // The first aetheria reaches over the doll area's top-right corner, so that corner is cut away round it, a gap clear of its
        // cell, and the cell sits in the window's own space beside the doll.
        var rowBottom = Cells.Cell + AetheriaGap;
        var notchLeft = 3 * Cells.Pitch + 2 * SideGap - AetheriaGap - GetLeft(DollArea);
        DollArea.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, DollArea.Width, BlockHeight)), new RectangleGeometry(new Rect(notchLeft, 0, DollArea.Width - notchLeft, rowBottom)));
        // The camera fits the whole figure, the widest race's arms too, in the view between DollTop and the corner row, so every race
        // stands whole in the same rectangle and covers no slot.
        DollArea.Padding = new Thickness(0, DollTop, 0, CornerRowHeight + 2 * CornerInset);
        SetLeft(SlotsToggle, GetLeft(DollArea) + CornerInset);
        SetTop(SlotsToggle, BlockHeight - CornerRowHeight - CornerInset);
        SetLeft(ExpandButton, GetLeft(DollArea) + DollArea.Width - ExpandButton.Width - CornerInset);
        SetTop(ExpandButton, BlockHeight - CornerRowHeight - CornerInset);
    }

    private static void PlaceCell(Control cell, int column, int y, bool right)
    {
        // The left column keeps to the left edge; a gap sets the armour columns off it, and another the right side off them.
        var shift = right ? 2 * SideGap : column >= ArmourLeft ? SideGap : 0;
        // A flush slot asks no width of its own, and a canvas gives a child only what it asks.
        cell.Width = Cells.Cell;
        SetLeft(cell, column * Cells.Pitch + shift);
        SetTop(cell, y);
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
        foreach (var control in _placed) Children.Remove(control);
        _placed.Clear();
        foreach (var (slot, x, y) in Positions)
        {
            if (!showArmour && Armour.Contains(slot)) continue;
            var cell = slotFor(slot);
            PlaceCell(cell, x, y, x >= RightLeft || Aetheria.Contains(slot));
            Children.Add(cell);
            _placed.Add(cell);
        }
    }
}
