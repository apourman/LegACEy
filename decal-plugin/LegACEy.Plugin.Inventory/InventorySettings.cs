using System.Drawing;

namespace LegACEy.Plugin.Inventory;

/// <summary>The two window layouts: the paperdoll on top with the pack column down the side, or the packs across the top.</summary>
public enum InventoryLayout
{
    Vertical,
    Horizontal,
}

/// <summary>What the window remembers for a character: the layout, and whether the armour slots show over the doll.</summary>
public readonly struct InventorySettings
{
    public InventorySettings(InventoryLayout layout, bool showSlots)
    {
        Layout = layout;
        ShowSlots = showSlots;
    }

    public InventoryLayout Layout { get; }
    public bool ShowSlots { get; }

    /// <summary>The first run's choice: vertical, with the armour slots showing.</summary>
    public static InventorySettings Default { get; } = new(InventoryLayout.Vertical, showSlots: true);

    /// <summary>The settings as the client stores them: the layout as X (0 vertical, 1 horizontal), the Slots toggle as Y.</summary>
    public Point ToPoint() => new((int)Layout, ShowSlots ? 1 : 0);

    /// <summary>The saved settings, or the default when none are saved.</summary>
    public static InventorySettings FromPoint(Point? saved) =>
        saved is not { } point ? Default
            : new InventorySettings(point.X == (int)InventoryLayout.Horizontal ? InventoryLayout.Horizontal : InventoryLayout.Vertical, point.Y != 0);
}
