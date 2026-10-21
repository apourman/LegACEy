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
/// The paperdoll block: retail's paperdoll (224×214 at 32 px) with every slot scaled to the <see cref="ItemCellSize"/> pitch, which follows the window's scale. The doll area is the
/// one control the 3D character fills; the Slots toggle sits above it, left of the aetheria row. Slots are placed by <see cref="Show"/>.
/// </summary>
internal sealed class InventoryPaperdoll : Canvas
{
    // Retail's cells are 32 px; the Dereth pitch is 35 px at scale 1. Each retail position is scaled to it, then the cell is centred in the pitch.
    private const int RetailCell = 32;
    private const int RetailWidth = 224;
    private const int RetailHeight = 214;
    // The armour columns' left edges. Retail's doll area (51 to 151) leaves its outer columns hanging over its sides, so the doll area here is
    // drawn round the columns instead, the same 2 DIPs clear of them on each side.
    private const int RetailArmourLeft = 48;
    private const int RetailArmourRight = 120;
    private const double DollClearance = 2;
    // The Slots toggle sits in the doll column's top strip, left of the aetheria row, so the doll runs down to the column's foot.
    private const int RetailToggleLeft = 48;
    private const int ToggleTop = 10;

    private static readonly (PaperdollSlot Slot, int X, int Y)[] Positions =
    {
        (PaperdollSlot.Neck, 8, 8), (PaperdollSlot.Trinket, 8, 44), (PaperdollSlot.RightWrist, 8, 80), (PaperdollSlot.RightRing, 8, 116),
        (PaperdollSlot.Shield, 8, 172),
        (PaperdollSlot.Head, 84, 28), (PaperdollSlot.UpperArms, 48, 64), (PaperdollSlot.Chest, 84, 64), (PaperdollSlot.LowerArms, 48, 100),
        (PaperdollSlot.Abdomen, 84, 100), (PaperdollSlot.UpperLegs, 120, 100), (PaperdollSlot.Hands, 48, 136), (PaperdollSlot.LowerLegs, 120, 136),
        (PaperdollSlot.Feet, 120, 172),
        // Retail packs the aetheria at 126, 158 and 190 and the ammo at 190, off the columns below; here each sits on a column, the first aetheria inside the doll area.
        (PaperdollSlot.AetheriaOne, 120, RetailAetheriaTop), (PaperdollSlot.AetheriaTwo, 156, RetailAetheriaTop), (PaperdollSlot.AetheriaThree, 192, RetailAetheriaTop),
        (PaperdollSlot.LeftWrist, 156, 80), (PaperdollSlot.LeftRing, 156, 116), (PaperdollSlot.Weapon, 156, 172), (PaperdollSlot.Ammo, 192, 172),
        (PaperdollSlot.Cloak, 192, 44), (PaperdollSlot.Shirt, 192, 80), (PaperdollSlot.Pants, 192, 116),
    };

    // The panel keeps its full size, and whatever is put in it (the 3D character) is inset to clear the aetheria cells and the Slots toggle
    // above it. The inset follows the aetheria row's foot at every scale: at scale 1 the row ends at y 44 and, with the 1 px border, the inside runs from y 46.
    private const int RetailAetheriaTop = 8;

    /// <summary>The armour slots, which sit over the doll and show only while the Slots toggle is on.</summary>
    private static readonly HashSet<PaperdollSlot> Armour = new()
    {
        PaperdollSlot.Head, PaperdollSlot.UpperArms, PaperdollSlot.Chest, PaperdollSlot.LowerArms, PaperdollSlot.Abdomen,
        PaperdollSlot.UpperLegs, PaperdollSlot.Hands, PaperdollSlot.LowerLegs, PaperdollSlot.Feet,
    };

    private readonly List<(Control Cell, int X, int Y)> _placed = new();
    private ItemCellSize _size;
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
            CornerRadius = new CornerRadius(3),
            BorderBrush = DerethPalette.GrooveEdgeBrush, BorderThickness = new Thickness(1),
            Background = new RadialGradientBrush { GradientStops = { new GradientStop(Color.Parse("#2A2519"), 0), new GradientStop(Color.Parse("#0A0805"), 1) } },
        };
        Children.Add(DollArea);

        // A plain row, not a framed button: the toggle reads as text under the doll, as the retail checkbox does. It acts on release.
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        label.Children.Add(_slotsMark);
        label.Children.Add(_slotsLabel);
        SlotsToggle = new Border { Name = "SlotsToggle", Background = Brushes.Transparent, Padding = new Thickness(2, 0), Child = label };
        SlotsToggle.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) SlotsPressed?.Invoke();
        };
        SetTop(SlotsToggle, ToggleTop);
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

    // Everything is placed from the pitch, which follows the window's scale and can change without a rebuild.
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = ItemCellSize.Of(this);
        if (size != _size) Place(size);
        base.MeasureOverride(availableSize);
        return new Size(Scale(RetailWidth), Scale(RetailHeight));
    }

    private void Place(ItemCellSize size)
    {
        _size = size;
        var height = Scale(RetailHeight);
        DollArea.Width = Scale(RetailArmourRight) - Scale(RetailArmourLeft) + size.Cell + 2 * DollClearance;
        DollArea.Height = height;
        DollArea.Padding = new Thickness(0, Scale(RetailAetheriaTop) + size.Cell + 2, 0, 0);
        SetLeft(DollArea, Scale(RetailArmourLeft) + 1 - DollClearance);
        SetLeft(SlotsToggle, Scale(RetailToggleLeft));
        SetLeft(ExpandButton, GetLeft(DollArea) + DollArea.Width - ExpandButton.Width - 4);
        SetTop(ExpandButton, height - ExpandButton.Height - 4);
        foreach (var (cell, x, y) in _placed) PlaceCell(cell, x, y);
    }

    private void PlaceCell(Control cell, int x, int y)
    {
        SetLeft(cell, Scale(x) + 1);
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
