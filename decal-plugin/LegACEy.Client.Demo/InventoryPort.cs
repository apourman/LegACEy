using System;
using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>
/// What a LegACEy inventory window can read and do with the character's inventory. The client reads the retail
/// state through Decal and calls retail's own actions for every command; the port never sends game messages itself.
/// </summary>
public interface IInventoryPort
{
    /// <summary>The inventory as of the last change. Immutable; a new snapshot replaces it on every change.</summary>
    InventorySnapshot Snapshot { get; }
    /// <summary>Raised on the render thread when <see cref="Snapshot"/> changed. Several game events in one frame raise it once.</summary>
    event Action? Changed;
    /// <summary>Opens a pack through retail's own use of it.</summary>
    void OpenContainer(uint containerId);
    /// <summary>Selects an item through retail's selection, or clears the selection with zero.</summary>
    void Select(uint itemId);
    /// <summary>Uses an item as retail does on a double-click (drink, read, open, wield or unwield).</summary>
    void Use(uint itemId);
    /// <summary>Moves an item to a slot of a container. The server decides the outcome; the snapshot changes only when it moves.</summary>
    void MoveToContainer(uint itemId, uint containerId, int slotIndex);
    /// <summary>Wields an item into the slots of an equip mask (the AC EquipMask bits, as ACE defines them).</summary>
    void Wield(uint itemId, uint equipMask);
    /// <summary>Moves a stack onto another stack of the same item, which merges them where the server allows.</summary>
    void MergeStack(uint itemId, uint targetStackId);
}

/// <summary>A pack in the pack column: the main pack, or a side pack. Id zero is an empty side-pack slot.</summary>
public sealed class InventoryPack
{
    public InventoryPack(uint id, string name, uint icon, int capacity, int order)
    {
        Id = id;
        Name = name ?? string.Empty;
        Icon = icon;
        Capacity = capacity;
        Order = order;
    }

    public uint Id { get; }
    public string Name { get; }
    /// <summary>The portal icon of the pack's container.</summary>
    public uint Icon { get; }
    /// <summary>The number of slots the pack holds.</summary>
    public int Capacity { get; }
    /// <summary>The pack's place in the pack column: zero for the main pack, then one for each side-pack slot.</summary>
    public int Order { get; }
}

/// <summary>An object's icon layers and UI effects, as retail draws them. Every value is a portal id, or zero.</summary>
public sealed class ItemVisual
{
    public ItemVisual(uint icon, uint underlay, uint overlay, uint outline)
    {
        Icon = icon;
        Underlay = underlay;
        Overlay = overlay;
        Outline = outline;
    }

    public uint Icon { get; }
    public uint Underlay { get; }
    public uint Overlay { get; }
    /// <summary>The UI-effect outline (the IconOutline value).</summary>
    public uint Outline { get; }
}

/// <summary>An item in a pack, at a slot index of its container.</summary>
public sealed class InventoryItem
{
    public InventoryItem(uint id, uint container, int slot, ItemVisual visual, int stackCount)
    {
        Id = id;
        Container = container;
        Slot = slot;
        Visual = visual ?? throw new ArgumentNullException(nameof(visual));
        StackCount = stackCount;
    }

    public uint Id { get; }
    /// <summary>The pack the item is in: the main pack's id (the character's) or a side pack's.</summary>
    public uint Container { get; }
    /// <summary>The slot index within the container, where zero is the first slot.</summary>
    public int Slot { get; }
    public ItemVisual Visual { get; }
    public int StackCount { get; }
}

/// <summary>An equipped item and the paperdoll slots it covers.</summary>
public sealed class WieldedItem
{
    public WieldedItem(uint id, ItemVisual visual, int stackCount, IReadOnlyList<PaperdollSlot> slots)
    {
        Id = id;
        Visual = visual ?? throw new ArgumentNullException(nameof(visual));
        StackCount = stackCount;
        Slots = slots ?? throw new ArgumentNullException(nameof(slots));
    }

    public uint Id { get; }
    public ItemVisual Visual { get; }
    public int StackCount { get; }
    /// <summary>Every paperdoll slot the item is drawn in: one for most items, several for a multi-slot item.</summary>
    public IReadOnlyList<PaperdollSlot> Slots { get; }
}

/// <summary>The character's inventory at one moment. Immutable once built.</summary>
public sealed class InventorySnapshot
{
    public InventorySnapshot(InventoryPack mainPack, IReadOnlyList<InventoryPack> sidePacks, IReadOnlyList<InventoryItem> items,
        IReadOnlyList<WieldedItem> wielded, int burden, int burdenLimit, int pyreals, uint openContainer, uint selected)
    {
        MainPack = mainPack ?? throw new ArgumentNullException(nameof(mainPack));
        SidePacks = sidePacks ?? throw new ArgumentNullException(nameof(sidePacks));
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Wielded = wielded ?? throw new ArgumentNullException(nameof(wielded));
        Burden = burden;
        BurdenLimit = burdenLimit;
        Pyreals = pyreals;
        OpenContainer = openContainer;
        Selected = selected;
    }

    /// <summary>A snapshot with no packs and nothing carried: what a window shows before the character is in the world.</summary>
    public static InventorySnapshot Empty { get; } = new(new InventoryPack(0, string.Empty, 0, 0, 0), Array.Empty<InventoryPack>(),
        Array.Empty<InventoryItem>(), Array.Empty<WieldedItem>(), 0, 0, 0, 0, 0);

    public InventoryPack MainPack { get; }
    /// <summary>The side packs in retail's order, one entry per side-pack slot up to the character's capacity. Free slots are empty placeholders.</summary>
    public IReadOnlyList<InventoryPack> SidePacks { get; }
    /// <summary>Every item in a pack, empty slots excluded.</summary>
    public IReadOnlyList<InventoryItem> Items { get; }
    public IReadOnlyList<WieldedItem> Wielded { get; }
    /// <summary>The current burden in the client's units (the same units as <see cref="BurdenLimit"/>).</summary>
    public int Burden { get; }
    public int BurdenLimit { get; }
    /// <summary>The total stack count of every pyreal carried in a pack.</summary>
    public int Pyreals { get; }
    /// <summary>The open pack's id, or zero when no pack is open.</summary>
    public uint OpenContainer { get; }
    /// <summary>The selected item's id, or zero when nothing is selected.</summary>
    public uint Selected { get; }
}

/// <summary>A port for tests: records commands as text, and lets a test set the snapshot and raise <see cref="Changed"/>.</summary>
public sealed class FakeInventoryPort : IInventoryPort
{
    public InventorySnapshot Snapshot { get; private set; } = InventorySnapshot.Empty;
    /// <summary>Every command received, in order, as text: "open 0x…", "select 0x…", "use 0x…", "move 0x… to 0x… slot 3", "wield 0x… mask 0x…", "merge 0x… into 0x…".</summary>
    public List<string> Commands { get; } = new();
    public event Action? Changed;

    /// <summary>Replaces the snapshot and raises <see cref="Changed"/>.</summary>
    public void Push(InventorySnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Changed?.Invoke();
    }

    public void OpenContainer(uint containerId) => Commands.Add($"open 0x{containerId:X8}");
    public void Select(uint itemId) => Commands.Add($"select 0x{itemId:X8}");
    public void Use(uint itemId) => Commands.Add($"use 0x{itemId:X8}");
    public void MoveToContainer(uint itemId, uint containerId, int slotIndex) => Commands.Add($"move 0x{itemId:X8} to 0x{containerId:X8} slot {slotIndex}");
    public void Wield(uint itemId, uint equipMask) => Commands.Add($"wield 0x{itemId:X8} mask 0x{equipMask:X8}");
    public void MergeStack(uint itemId, uint targetStackId) => Commands.Add($"merge 0x{itemId:X8} into 0x{targetStackId:X8}");
}
