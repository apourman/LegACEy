using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Inventory;

namespace LegACEy.Client.Tests;

/// <summary>Finds and presses the controls of an inventory panel the way a player's clicks reach them.</summary>
internal static class InventoryDriver
{
    /// <summary>Presses the control at its centre, and lets go there. The press lands on what the last frame drew.</summary>
    public static void Press(AvaloniaPanel host, Control control)
    {
        Tick(host);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host.Content)!.Value;
        host.PointerDown(point.X, point.Y, KeyModifiers.None);
        host.PointerUp(point.X, point.Y);
        Tick(host);
    }

    public static void Tick(AvaloniaPanel host)
    {
        for (var i = 0; i < 3; i++) host.Tick();
    }

    public static void PressSlots(AvaloniaPanel host) => Press(host, SlotsToggle(host));

    public static void PressLayout(AvaloniaPanel host, InventoryLayout layout) => Press(host, LayoutToggle(host, layout));

    /// <summary>The Slots toggle under the doll.</summary>
    public static Border SlotsToggle(AvaloniaPanel host) => host.Content.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SlotsToggle");

    /// <summary>The header button that switches to a layout.</summary>
    public static DerethButton LayoutToggle(AvaloniaPanel host, InventoryLayout layout) =>
        host.Content.GetVisualDescendants().OfType<DerethButton>().Single(button => button.Tag is InventoryLayout tagged && tagged == layout);

    /// <summary>Every slot the window draws, each identified by its <see cref="InventorySlotId"/> tag.</summary>
    public static DerethSlot[] Slots(AvaloniaPanel host) =>
        host.Content.GetVisualDescendants().OfType<DerethSlot>().Where(slot => slot.Tag is InventorySlotId).ToArray();

    public static InventorySlotId Id(DerethSlot slot) => (InventorySlotId)slot.Tag!;

    /// <summary>The paperdoll slot for an equipment slot, or null when the slot is not drawn (the armour slots while Slots is off).</summary>
    public static DerethSlot? SlotOrNull(AvaloniaPanel host, PaperdollSlot slot) =>
        Slots(host).SingleOrDefault(candidate => Id(candidate).Place == SlotPlace.Paperdoll && Id(candidate).Equipment == slot);
}
