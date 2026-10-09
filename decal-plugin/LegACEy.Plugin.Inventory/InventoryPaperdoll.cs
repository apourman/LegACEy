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
/// The paperdoll block: retail's paperdoll (224×214 at 32 px) with every slot scaled to the 36 px pitch. The doll area is the
/// one control the 3D character fills; the Slots toggle sits under it. Slots are placed by <see cref="Show"/>.
/// </summary>
internal sealed class InventoryPaperdoll : Canvas
{
    // Retail's cells are 32 px; the Dereth pitch is 36 px. Each retail position is scaled to it, then the 34 px cell is centred in the pitch.
    private const int RetailCell = 32;
    private const int RetailWidth = 224;
    private const int RetailHeight = 214;
    private const int RetailDollLeft = 51;
    private const int RetailDollWidth = 100;
    private const int RetailToggleLeft = 48;
    private const int RetailToggleTop = 190;

    private static readonly (PaperdollSlot Slot, int X, int Y)[] Positions =
    {
        (PaperdollSlot.Neck, 8, 8), (PaperdollSlot.Trinket, 8, 44), (PaperdollSlot.RightWrist, 8, 80), (PaperdollSlot.RightRing, 8, 116),
        (PaperdollSlot.Shield, 8, 172),
        (PaperdollSlot.Head, 84, 28), (PaperdollSlot.UpperArms, 48, 64), (PaperdollSlot.Chest, 84, 64), (PaperdollSlot.LowerArms, 48, 100),
        (PaperdollSlot.Abdomen, 84, 100), (PaperdollSlot.UpperLegs, 120, 100), (PaperdollSlot.Hands, 48, 136), (PaperdollSlot.LowerLegs, 120, 136),
        (PaperdollSlot.Feet, 120, 172),
        (PaperdollSlot.AetheriaOne, 126, 8), (PaperdollSlot.AetheriaTwo, 158, 8), (PaperdollSlot.AetheriaThree, 190, 8),
        (PaperdollSlot.LeftWrist, 156, 80), (PaperdollSlot.LeftRing, 156, 116), (PaperdollSlot.Weapon, 156, 172), (PaperdollSlot.Ammo, 190, 172),
        (PaperdollSlot.Cloak, 192, 44), (PaperdollSlot.Shirt, 192, 80), (PaperdollSlot.Pants, 192, 116),
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
        Width = Scale(RetailWidth);
        Height = Scale(RetailHeight);
        DollArea = new Border
        {
            Width = Scale(RetailDollWidth), Height = Height, CornerRadius = new CornerRadius(3),
            BorderBrush = DerethPalette.GrooveEdgeBrush, BorderThickness = new Thickness(1),
            Background = new RadialGradientBrush { GradientStops = { new GradientStop(Color.Parse("#16222C"), 0), new GradientStop(Color.Parse("#05090D"), 1) } },
        };
        SetLeft(DollArea, Scale(RetailDollLeft));
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
        SetLeft(SlotsToggle, Scale(RetailToggleLeft));
        SetTop(SlotsToggle, Scale(RetailToggleTop) + 6);
        Children.Add(SlotsToggle);
    }

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
            SetLeft(cell, Scale(x) + 1);
            SetTop(cell, Scale(y) + 1);
            Children.Add(cell);
            _placed.Add(cell);
        }
    }

    /// <summary>Puts a control in the doll area, or takes the one there out. Taking it out is what stops the 3D look's requests.</summary>
    public void ShowDoll(Control? doll)
    {
        if (DollArea.Child != doll) DollArea.Child = doll;
    }

    /// <summary>Retail's position, scaled to the pitch and rounded to whole pixels.</summary>
    private static double Scale(int retail) => Math.Round(retail * DerethSlotGrid.Pitch / RetailCell);
}
