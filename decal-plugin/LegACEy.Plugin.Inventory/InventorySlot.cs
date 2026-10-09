using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Inventory;

/// <summary>Where a slot sits: a paperdoll slot, a pack in the pack list, or a cell of the open container's grid.</summary>
public enum SlotPlace
{
    Paperdoll,
    Pack,
    Cell,
}

/// <summary>
/// A slot the window draws, identified by what it shows and where it is, so the commands that come later can find it:
/// the item id, the container and slot index for a grid cell or a pack, and the paperdoll slot for an equipment slot.
/// </summary>
public sealed class InventorySlot : DerethSlot
{
    /// <param name="itemId">The item the slot shows; zero when it is empty or a pack slot.</param>
    /// <param name="container">The pack a cell sits in, or the pack a pack slot stands for; zero for the paperdoll.</param>
    /// <param name="slotIndex">The container slot index of a cell; -1 otherwise.</param>
    /// <param name="equipment">The paperdoll slot of a paperdoll slot; null otherwise.</param>
    public InventorySlot(SlotPlace place, uint itemId, uint container, int slotIndex, PaperdollSlot? equipment, Control? content)
        : base(content)
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
