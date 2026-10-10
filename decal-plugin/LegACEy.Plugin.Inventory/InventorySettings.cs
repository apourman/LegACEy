namespace LegACEy.Plugin.Inventory;

/// <summary>The two window layouts: the paperdoll on top with the pack column down the side, or the packs across the top.</summary>
public enum InventoryLayout
{
    Vertical,
    Horizontal,
}

/// <summary>
/// What the window remembers for a character: the layout, whether the armour slots show over the doll, and whether the equipment
/// and paperdoll section is collapsed, leaving the packs and their contents.
/// </summary>
public readonly struct InventorySettings
{
    private const int HorizontalBit = 1;
    private const int SlotsBit = 2;
    private const int CollapsedBit = 4;

    public InventorySettings(InventoryLayout layout, bool showSlots, bool collapsed = false)
    {
        Layout = layout;
        ShowSlots = showSlots;
        Collapsed = collapsed;
    }

    public InventoryLayout Layout { get; }
    public bool ShowSlots { get; }
    public bool Collapsed { get; }

    /// <summary>The window these settings show.</summary>
    public InventoryView View => new(Layout, Collapsed);

    /// <summary>The first run's choice: vertical, with the armour slots showing.</summary>
    public static InventorySettings Default { get; } = new(InventoryLayout.Vertical, showSlots: true);

    /// <summary>
    /// The settings as the one integer the client keeps for the plugin: bit 1 is the horizontal layout, bit 2 the Slots toggle, bit 3
    /// the collapsed section.
    /// </summary>
    public int ToInt() => (Layout == InventoryLayout.Horizontal ? HorizontalBit : 0) | (ShowSlots ? SlotsBit : 0) | (Collapsed ? CollapsedBit : 0);

    /// <summary>The saved settings, or the default when none are saved.</summary>
    public static InventorySettings FromInt(int? saved) =>
        saved is not { } value ? Default
            : new InventorySettings((value & HorizontalBit) != 0 ? InventoryLayout.Horizontal : InventoryLayout.Vertical, (value & SlotsBit) != 0,
                (value & CollapsedBit) != 0);
}

/// <summary>
/// One of the inventory's windows: a layout, full or with the equipment and paperdoll section collapsed. Each is its own window with
/// its own saved size and position. Its properties are set by the constructor: netstandard2.0 has no init accessors.
/// </summary>
public readonly record struct InventoryView
{
    public InventoryView(InventoryLayout layout, bool collapsed)
    {
        Layout = layout;
        Collapsed = collapsed;
    }

    public InventoryLayout Layout { get; }
    public bool Collapsed { get; }
}
