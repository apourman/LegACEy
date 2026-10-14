using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>
/// The shortcut edits a drag and a drop make, as retail makes them, through the port's add and remove only. The list has 18 slots: row 1
/// is 0 to 8, row 2 is 9 to 17. A shortcut dragged off its slot is removed when the drag begins, as retail's ItemListBeginDrag does.
/// </summary>
public static class ShortcutEdits
{
    public const int Slots = 18;

    /// <summary>The object a slot's shortcut uses, or 0 when the slot is empty.</summary>
    public static uint ObjectAt(IReadOnlyList<RetailShortcut> shortcuts, int index)
    {
        foreach (var shortcut in shortcuts)
            if (shortcut.Index == index) return shortcut.ObjectId;
        return 0;
    }

    /// <summary>
    /// A drop from outside the bars (the Dereth inventory, a retail window, the world) on a slot: the item binds there. An item already on
    /// another slot leaves it first, as retail's drop does (CreateShortcutToItem removes the object before it adds it), so an item is bound
    /// once. A shortcut already in the target slot goes to the first empty slot to the right, and is lost when none is empty. A slot
    /// already holding the item is left alone.
    /// </summary>
    public static void Bind(IRetailToolbarPort port, int slot, uint objectId)
    {
        var shortcuts = port.Shortcuts;
        var occupant = ObjectAt(shortcuts, slot);
        if (occupant == objectId) return;
        if (IndexOf(shortcuts, objectId) is var held and >= 0) port.RemoveShortcut(held);
        if (occupant != 0) port.RemoveShortcut(slot);
        port.AddShortcut(slot, objectId);
        if (occupant == 0) return;
        var empty = FirstEmptyRightOf(port.Shortcuts, slot);
        if (empty >= 0) port.AddShortcut(empty, occupant);
    }

    /// <summary>The slot holding the object, or -1 when no slot holds it.</summary>
    public static int IndexOf(IReadOnlyList<RetailShortcut> shortcuts, uint objectId)
    {
        foreach (var shortcut in shortcuts)
            if (shortcut.ObjectId == objectId) return shortcut.Index;
        return -1;
    }

    /// <summary>A drag begins: the shortcut in <paramref name="slot"/> leaves its slot. Nothing happens when the slot is empty.</summary>
    public static void Lift(IRetailToolbarPort port, int slot)
    {
        if (ObjectAt(port.Shortcuts, slot) != 0) port.RemoveShortcut(slot);
    }

    /// <summary>
    /// A shortcut lifted from slot <paramref name="from"/> is dropped on slot <paramref name="to"/>: it takes the slot, and the shortcut there
    /// swaps into <paramref name="from"/>, which the lift freed. Dropped on its own slot, it goes back there.
    /// </summary>
    public static void Land(IRetailToolbarPort port, int from, int to, uint dragged)
    {
        var occupant = ObjectAt(port.Shortcuts, to);
        if (occupant != 0) port.RemoveShortcut(to);
        port.AddShortcut(to, dragged);
        if (occupant != 0) port.AddShortcut(from, occupant);
    }

    /// <summary>A lifted shortcut that no bar took goes back to the slot it left, if that slot is still empty.</summary>
    public static void Restore(IRetailToolbarPort port, int slot, uint objectId)
    {
        if (objectId != 0 && ObjectAt(port.Shortcuts, slot) == 0) port.AddShortcut(slot, objectId);
    }

    /// <summary>
    /// The first empty slot after <paramref name="slot"/>, wrapping round to the start of the list, and ending at <paramref name="slot"/>
    /// itself. This is the order of retail's GetFirstEmptyShortcutToTheRightOf (0x004BE1F0): slot+1 to 17, then 0 to slot. -1 when none is empty.
    /// </summary>
    public static int FirstEmptyRightOf(IReadOnlyList<RetailShortcut> shortcuts, int slot)
    {
        for (var step = 1; step <= Slots; step++)
        {
            var index = (slot + step) % Slots;
            if (ObjectAt(shortcuts, index) == 0) return index;
        }
        return -1;
    }
}

/// <summary>
/// The shortcut a drag began with, shared by the shortcut bars: the slot it left and its object. A bar that lands it clears <see cref="Slot"/>;
/// a drop on any other window leaves it set, and the bar the drag began on then ends the drag.
/// </summary>
public sealed class ShortcutDragSource
{
    public int? Slot { get; set; }
    public uint ObjectId { get; set; }
}
