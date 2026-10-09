using System;
using System.Collections.Generic;
using Avalonia;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// A LegACEy window that accepts items dragged out of the retail UI (inventory, paperdoll). The host polls the
/// client's drag-and-drop state each frame and reports it here; on release over the window it cancels the retail
/// drag, so the item can never fall through to the world under the window, and offers the drop.
/// </summary>
public interface IRetailItemDropTarget
{
    /// <summary>The current retail drag: item 0 when none; position in this control's coordinates, or null when the pointer is outside it.</summary>
    void RetailDragOver(uint itemId, string itemName, Point? position);

    /// <summary>The item was released at the position. True if the window used it.</summary>
    bool RetailDrop(uint itemId, string itemName, Point position);
}

public enum ItemDropTarget
{
    /// <summary>The pointer is over the open retail inventory.</summary>
    Inventory,
    /// <summary>The retail inventory panel exists but is closed.</summary>
    InventoryClosed,
    /// <summary>Anywhere else.</summary>
    Elsewhere
}

/// <summary>Host services for dragging an item out of a LegACEy window and onto the retail UI.</summary>
public interface IItemDragHost
{
    /// <summary>
    /// Shows the item's icon, already drawn (see ItemIcon.Draw), under the pointer, above every window, until disposed.
    /// <paramref name="stack"/> is how many items the drag carries: above one, the icon carries a "×stack" count badge.
    /// </summary>
    IDisposable ShowDragIcon(GameImage? icon, int stack);

    /// <summary>What is under the pointer now, outside LegACEy windows.</summary>
    ItemDropTarget DropTargetAtPointer();
}

/// <summary>Drag host for tests: no icon, and a drop target the caller chooses.</summary>
public sealed class FakeItemDragHost : IItemDragHost
{
    public ItemDropTarget Target { get; set; } = ItemDropTarget.Inventory;
    public List<GameImage?> IconsShown { get; } = new();
    /// <summary>The stack count of each icon shown, as the host was asked to badge it.</summary>
    public List<int> StacksShown { get; } = new();
    public int IconsOpen { get; private set; }

    public IDisposable ShowDragIcon(GameImage? icon, int stack)
    {
        IconsShown.Add(icon);
        StacksShown.Add(stack);
        IconsOpen++;
        return new Icon(this);
    }

    public ItemDropTarget DropTargetAtPointer() => Target;

    private sealed class Icon : IDisposable
    {
        private FakeItemDragHost? _owner;
        public Icon(FakeItemDragHost owner) => _owner = owner;
        public void Dispose()
        {
            if (_owner != null) _owner.IconsOpen--;
            _owner = null;
        }
    }
}
