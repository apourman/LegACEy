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
        var point = Centre(host, control);
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

    /// <summary>Presses the header button that collapses or shows the equipment and paperdoll section.</summary>
    public static void PressEquipment(AvaloniaPanel host) =>
        Press(host, host.Content.GetVisualDescendants().OfType<DerethButton>().Single(button => button.Name == "EquipmentToggle"));

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

    /// <summary>The tile of a pack in the pack list, the main pack's included.</summary>
    public static DerethSlot PackSlot(AvaloniaPanel host, uint pack) =>
        Slots(host).Single(candidate => Id(candidate).Place == SlotPlace.Pack && Id(candidate).Container == pack);

    /// <summary>Presses a slot, drags it past the threshold to another, and holds there: the drop indicator shows, and nothing is sent yet.</summary>
    public static void DragTo(AvaloniaPanel host, Control from, Control to)
    {
        Tick(host);
        var start = Centre(host, from);
        host.PointerDown(start.X, start.Y, KeyModifiers.None);
        host.PointerMove(start.X + 10, start.Y);
        var end = Centre(host, to);
        host.PointerMove(end.X, end.Y);
        Tick(host);
    }

    /// <summary>Lets go over a control, ending the drag begun by <see cref="DragTo"/>.</summary>
    public static void Release(AvaloniaPanel host, Control at)
    {
        var point = Centre(host, at);
        host.PointerUp(point.X, point.Y);
        Tick(host);
    }

    /// <summary>A plain drag: presses the source, moves to the target and lets go there.</summary>
    public static void Drop(AvaloniaPanel host, Control from, Control to)
    {
        DragTo(host, from, to);
        Release(host, to);
    }

    /// <summary>The point at a control's centre, in panel pixels, after the last frame was drawn.</summary>
    public static Point Centre(AvaloniaPanel host, Control control)
    {
        Tick(host);
        return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host.Content)!.Value;
    }
}
