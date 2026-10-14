using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>
/// The shortcut edits a drop makes, as retail's drop on a slot makes them, through the port's add and remove only. The list has 18
/// slots: row 1 is 0 to 8, row 2 is 9 to 17. The first empty slot to the right of a slot wraps round to the start of the list.
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
    /// A drop from outside the bars (the Dereth inventory, a retail window, the world) on a slot: the item binds there. A shortcut already
    /// there goes to the first empty slot to the right, and is lost when none is empty.
    /// </summary>
    public static void Bind(IRetailToolbarPort port, int slot, uint objectId)
    {
        var occupant = ObjectAt(port.Shortcuts, slot);
        if (occupant != 0) port.RemoveShortcut(slot);
        port.AddShortcut(slot, objectId);
        if (occupant == 0 || occupant == objectId) return;
        var empty = FirstEmptyRightOf(port.Shortcuts, slot);
        if (empty >= 0) port.AddShortcut(empty, occupant);
    }

    /// <summary>
    /// A shortcut dragged from slot <paramref name="from"/> onto slot <paramref name="to"/>: it takes the slot, and the shortcut there
    /// swaps into <paramref name="from"/>. Dropped on its own slot, nothing changes.
    /// </summary>
    public static void Move(IRetailToolbarPort port, int from, int to)
    {
        var dragged = ObjectAt(port.Shortcuts, from);
        if (dragged == 0 || from == to) return;
        port.RemoveShortcut(from);
        var occupant = ObjectAt(port.Shortcuts, to);
        if (occupant != 0) port.RemoveShortcut(to);
        port.AddShortcut(to, dragged);
        if (occupant != 0) port.AddShortcut(from, occupant);
    }

    /// <summary>A shortcut dragged off the bars leaves them: its slot is cleared. An empty slot is left alone.</summary>
    public static void Take(IRetailToolbarPort port, int slot)
    {
        if (ObjectAt(port.Shortcuts, slot) != 0) port.RemoveShortcut(slot);
    }

    /// <summary>The first empty slot after <paramref name="slot"/>, wrapping round to the start of the list; -1 when none is empty.</summary>
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
/// The slot a drag of a shortcut started from, shared by the shortcut bars. A bar that takes the dragged shortcut moves it and clears this;
/// when no bar does, the bar it came from takes it off, as retail's drag start does.
/// </summary>
public sealed class ShortcutDragSource
{
    public int? Slot { get; set; }
}
