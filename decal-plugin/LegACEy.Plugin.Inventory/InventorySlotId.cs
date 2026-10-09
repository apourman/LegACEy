using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Inventory;

/// <summary>Where a slot sits: a paperdoll slot, a pack in the pack list, or a cell of the open container's grid.</summary>
public enum SlotPlace
{
    Paperdoll,
    Pack,
    Cell,
}

/// <summary>
/// Identifies a slot the window draws: carried as the <c>Tag</c> of its <c>DerethSlot</c>, so the commands that come later find it
/// by what it shows and where it is. Its properties are set once, by the constructor (no init accessors: netstandard2.0 has none).
/// </summary>
public sealed record InventorySlotId
{
    /// <param name="place">The kind of slot.</param>
    /// <param name="itemId">The item the slot shows; zero when it is empty or a pack slot.</param>
    /// <param name="container">The pack a cell sits in, or the pack a pack slot stands for; zero for the paperdoll and empty pack slots.</param>
    /// <param name="slotIndex">The container slot index of a cell; -1 otherwise.</param>
    /// <param name="equipment">The paperdoll slot of a paperdoll slot; null otherwise.</param>
    public InventorySlotId(SlotPlace place, uint itemId, uint container, int slotIndex, PaperdollSlot? equipment)
    {
        Place = place;
        ItemId = itemId;
        Container = container;
        SlotIndex = slotIndex;
        Equipment = equipment;
    }

    public SlotPlace Place { get; }
    public uint ItemId { get; }
    public uint Container { get; }
    public int SlotIndex { get; }
    public PaperdollSlot? Equipment { get; }
}
