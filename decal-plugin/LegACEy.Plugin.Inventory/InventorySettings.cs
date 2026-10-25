namespace LegACEy.Plugin.Inventory;

/// <summary>
/// What the window remembers for a character: whether the armour slots show over the doll, and whether the equipment and paperdoll
/// section is collapsed, leaving the packs and their contents.
/// </summary>
public readonly struct InventorySettings
{
    // Bit 1 was the horizontal layout, which is gone; a saved value with it set opens the one layout there is.
    private const int SlotsBit = 2;
    private const int CollapsedBit = 4;

    public InventorySettings(bool showSlots, bool collapsed = false)
    {
        ShowSlots = showSlots;
        Collapsed = collapsed;
    }

    public bool ShowSlots { get; }
    public bool Collapsed { get; }

    /// <summary>The first run's choice: the armour slots showing.</summary>
    public static InventorySettings Default { get; } = new(showSlots: true);

    /// <summary>The settings as the one integer the client keeps for the plugin: bit 2 the Slots toggle, bit 3 the collapsed section.</summary>
    public int ToInt() => (ShowSlots ? SlotsBit : 0) | (Collapsed ? CollapsedBit : 0);

    /// <summary>The saved settings, or the default when none are saved.</summary>
    public static InventorySettings FromInt(int? saved) =>
        saved is not { } value ? Default : new InventorySettings((value & SlotsBit) != 0, (value & CollapsedBit) != 0);
}
