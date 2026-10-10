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
    /// <paramref name="count"/> is how many items the drag carries: above one, the icon carries a "×count" badge.
    /// </summary>
    IDisposable ShowDragIcon(GameImage? icon, int count);

    /// <summary>What is under the pointer now, outside LegACEy windows.</summary>
    ItemDropTarget DropTargetAtPointer();
}

/// <summary>
/// A LegACEy window that shows the player's inventory. A Vault withdraw released over it counts as released over the inventory,
/// whether or not the retail inventory panel is taken over.
/// </summary>
public interface IInventoryDropZone
{
}

/// <summary>
/// Hands an item dragged out of a LegACEy window to the LegACEy window under the pointer, as a retail drag released there would
/// reach it (<see cref="IRetailItemDropTarget"/>). The inventory uses it to deposit an item in the Vault.
/// </summary>
public interface IItemDropRelay
{
    /// <summary>True when the window under the pointer used the item.</summary>
    bool DeliverAtPointer(uint itemId, string itemName);
}

/// <summary>A LegACEy window on screen, for routing a drop: its id, its content and where it is, in screen pixels.</summary>
public sealed class DropArea
{
    public DropArea(string id, object content, System.Drawing.Rectangle bounds)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Bounds = bounds;
    }

    public string Id { get; }
    public object Content { get; }
    public System.Drawing.Rectangle Bounds { get; }
}

/// <summary>Which LegACEy window a drop released at a screen point lands on. The areas are listed topmost first.</summary>
public static class ItemDropRouting
{
    /// <summary>The topmost window under the point, or null over none.</summary>
    public static DropArea? TopAt(System.Drawing.Point point, IEnumerable<DropArea> areas)
    {
        foreach (var area in areas)
            if (area.Bounds.Contains(point)) return area;
        return null;
    }

    /// <summary>Where a Vault withdraw released at the point counts as the inventory: Inventory over an inventory window, else null.</summary>
    public static ItemDropTarget? InventoryAt(System.Drawing.Point point, IEnumerable<DropArea> areas) =>
        TopAt(point, areas)?.Content is IInventoryDropZone ? ItemDropTarget.Inventory : (ItemDropTarget?)null;

    /// <summary>Delivers an item released at the point to the topmost window there, if that window takes retail drops. True if it used the item.</summary>
    public static bool Deliver(System.Drawing.Point point, IEnumerable<DropArea> areas, uint itemId, string itemName)
    {
        var area = TopAt(point, areas);
        if (area?.Content is not IRetailItemDropTarget target) return false;
        return target.RetailDrop(itemId, itemName, new Point(point.X - area.Bounds.X, point.Y - area.Bounds.Y));
    }
}

/// <summary>Drag host for tests: no icon, and a drop target the caller chooses.</summary>
public sealed class FakeItemDragHost : IItemDragHost
{
    public ItemDropTarget Target { get; set; } = ItemDropTarget.Inventory;
    public List<GameImage?> IconsShown { get; } = new();
    /// <summary>The count each icon was shown with, as the host was asked to badge it.</summary>
    public List<int> CountsShown { get; } = new();
    public int IconsOpen { get; private set; }

    public IDisposable ShowDragIcon(GameImage? icon, int count)
    {
        IconsShown.Add(icon);
        CountsShown.Add(count);
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
