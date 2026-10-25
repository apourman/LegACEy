namespace LegACEy.Plugin.Inventory;

/// <summary>What the window remembers for a character: whether the armour slots show over the doll.</summary>
public readonly struct InventorySettings
{
    // Bit 1 was the horizontal layout and bit 3 the collapsed equipment section, both gone; a saved value with either set opens the
    // one window there is.
    private const int SlotsBit = 2;

    public InventorySettings(bool showSlots) => ShowSlots = showSlots;

    public bool ShowSlots { get; }

    /// <summary>The first run's choice: the armour slots showing.</summary>
    public static InventorySettings Default { get; } = new(showSlots: true);

    /// <summary>The settings as the one integer the client keeps for the plugin: bit 2 the Slots toggle.</summary>
    public int ToInt() => ShowSlots ? SlotsBit : 0;

    /// <summary>The saved settings, or the default when none are saved.</summary>
    public static InventorySettings FromInt(int? saved) => saved is not { } value ? Default : new InventorySettings((value & SlotsBit) != 0);
}
