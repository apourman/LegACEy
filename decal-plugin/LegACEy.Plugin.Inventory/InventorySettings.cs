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
    private const int HorizontalBit = 1;
    private const int SlotsBit = 2;

    public InventorySettings(InventoryLayout layout, bool showSlots)
    {
        Layout = layout;
        ShowSlots = showSlots;
    }

    public InventoryLayout Layout { get; }
    public bool ShowSlots { get; }

    /// <summary>The first run's choice: vertical, with the armour slots showing.</summary>
    public static InventorySettings Default { get; } = new(InventoryLayout.Vertical, showSlots: true);

    /// <summary>The settings as the one integer the client keeps for the plugin: bit 1 is the horizontal layout, bit 2 the Slots toggle.</summary>
    public int ToInt() => (Layout == InventoryLayout.Horizontal ? HorizontalBit : 0) | (ShowSlots ? SlotsBit : 0);

    /// <summary>The saved settings, or the default when none are saved.</summary>
    public static InventorySettings FromInt(int? saved) =>
        saved is not { } value ? Default
            : new InventorySettings((value & HorizontalBit) != 0 ? InventoryLayout.Horizontal : InventoryLayout.Vertical, (value & SlotsBit) != 0);
}
