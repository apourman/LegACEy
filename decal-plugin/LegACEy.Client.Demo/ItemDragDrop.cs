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

/// <summary>
/// A LegACEy window that shows the player's inventory. A Vault withdraw released over it counts as released over the inventory,
/// whether or not the retail inventory panel is taken over.
/// </summary>
public interface IInventoryDropZone
{
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

/// <summary>Host services for dragging an item out of a LegACEy window and onto the retail UI, and for handing one to a LegACEy window.</summary>
public interface IItemDragHost
{
    /// <summary>
    /// Shows the item's icon, already drawn (see ItemIcon.Draw), under the pointer, above every window, until disposed.
    /// <paramref name="count"/> is how many items the drag carries: above one, the icon carries a "×count" badge.
    /// <paramref name="retailDropIndicator"/> is true for a drag the retail inventory can take (a Vault withdrawal): the retail
    /// panel then shows its own drop indicator over the cell under the pointer. Our inventory's drags pass false.
    /// </summary>
    IDisposable ShowDragIcon(GameImage? icon, int count, bool retailDropIndicator);

    /// <summary>What is under the pointer now, outside LegACEy windows.</summary>
    ItemDropTarget DropTargetAtPointer();

    /// <summary>
    /// Hands an item released outside every slot of a LegACEy window to the LegACEy window under the pointer, as a retail drag
    /// released there would reach it. True when that window used the item. Nothing is under the pointer: false.
    /// </summary>
    bool DeliverAtPointer(uint itemId, string itemName);
}

/// <summary>
/// Which LegACEy window a drop released at a screen point lands on, by the window manager's hit test (topmost first).
/// <c>contentOf</c> gives a window's content by its id.
/// </summary>
public static class ItemDropRouting
{
    /// <summary>Where a Vault withdraw released at the point counts as the inventory: Inventory over an inventory window, else null.</summary>
    public static ItemDropTarget? InventoryAt(WindowManager windows, Func<string, object?> contentOf, System.Drawing.Point point) =>
        windows.HitTest(point) is { } window && contentOf(window.Id) is IInventoryDropZone ? ItemDropTarget.Inventory : (ItemDropTarget?)null;

    /// <summary>Delivers an item released at the point to the window there, if that window takes retail drops. True if it used the item.</summary>
    public static bool Deliver(WindowManager windows, Func<string, object?> contentOf, System.Drawing.Point point, uint itemId, string itemName) =>
        windows.HitTest(point) is { } window && DeliverTo(window, contentOf, point, itemId, itemName);

    /// <summary>Delivers an item released at the point to a window the window manager's hit test already found. True if it used the item.</summary>
    public static bool DeliverTo(ManagedWindow window, Func<string, object?> contentOf, System.Drawing.Point point, uint itemId, string itemName) =>
        contentOf(window.Id) is IRetailItemDropTarget target
        && target.RetailDrop(itemId, itemName, new Point(point.X - window.Location.X, point.Y - window.Location.Y));
}

/// <summary>Drag host for tests: no icon, a drop target the caller chooses, and a relay the caller answers.</summary>
public sealed class FakeItemDragHost : IItemDragHost
{
    public ItemDropTarget Target { get; set; } = ItemDropTarget.Inventory;
    /// <summary>Answers a drop handed to a LegACEy window. False by default: no window took it.</summary>
    public Func<uint, string, bool> Relay { get; set; } = (_, _) => false;
    public List<GameImage?> IconsShown { get; } = new();
    /// <summary>The count each icon was shown with, as the host was asked to badge it.</summary>
    public List<int> CountsShown { get; } = new();
    /// <summary>Whether each icon was shown with the retail drop indicator.</summary>
    public List<bool> RetailIndicators { get; } = new();
    /// <summary>The items handed to a LegACEy window, in order.</summary>
    public List<(uint Id, string Name)> Delivered { get; } = new();
    public int IconsOpen { get; private set; }

    public IDisposable ShowDragIcon(GameImage? icon, int count, bool retailDropIndicator)
    {
        IconsShown.Add(icon);
        RetailIndicators.Add(retailDropIndicator);
        CountsShown.Add(count);
        IconsOpen++;
        return new Icon(this);
    }

    public ItemDropTarget DropTargetAtPointer() => Target;

    public bool DeliverAtPointer(uint itemId, string itemName)
    {
        Delivered.Add((itemId, itemName));
        return Relay(itemId, itemName);
    }

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
