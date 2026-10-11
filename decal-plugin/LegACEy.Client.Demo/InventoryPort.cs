using System;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>
/// What a LegACEy inventory window can read and do with the character's inventory. Every command is a retail action;
/// the port never sends game messages itself.
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
    /// <summary>Uses an item as retail does on a double-click.</summary>
    void Use(uint itemId);
    /// <summary>Selects an item and asks the server to appraise it, as retail does on a right-click, so retail's examination window shows it.</summary>
    void Assess(uint itemId);
    /// <summary>Drops an item of the player's on the ground, as retail does for one released over the world.</summary>
    void DropOnGround(uint itemId);
    /// <summary>Moves an item to a slot of a container. The server decides the outcome, and the snapshot changes only when the item moves.</summary>
    void MoveToContainer(uint itemId, uint containerId, int slotIndex);
    /// <summary>
    /// The wield mask for an item in a paperdoll slot: its valid locations within the slot, and zero where it cannot go. An item the
    /// snapshot does not hold (one in another window's container, a corpse, a chest) is read from the world.
    /// </summary>
    uint WieldMask(uint itemId, PaperdollSlot slot);
    /// <summary>Wields an item into a paperdoll slot. Nothing is sent when <see cref="WieldMask"/> is zero for it.</summary>
    void Wield(uint itemId, PaperdollSlot slot);
    /// <summary>Moves a stack onto another stack of the same item, which merges them where the server allows.</summary>
    void MergeStack(uint itemId, uint targetStackId);
}

/// <summary>A pack in the pack column. The main pack comes first, then the side packs in slot order. Id zero is an empty side-pack slot.</summary>
public sealed class InventoryPack : IEquatable<InventoryPack>
{
    public InventoryPack(uint id, string name, uint icon, int capacity)
    {
        Id = id;
        Name = name ?? string.Empty;
        Icon = icon;
        Capacity = capacity;
    }

    public uint Id { get; }
    public string Name { get; }
    /// <summary>The portal icon of the pack's container.</summary>
    public uint Icon { get; }
    /// <summary>The number of slots the pack holds.</summary>
    public int Capacity { get; }

    public bool Equals(InventoryPack? other) => other != null && (Id, Name, Icon, Capacity).Equals((other.Id, other.Name, other.Icon, other.Capacity));
    public override bool Equals(object? obj) => Equals(obj as InventoryPack);
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>An object's icon layers and UI effects, as retail draws them. Every value is a portal id, or zero.</summary>
public sealed class ItemVisual : IEquatable<ItemVisual>
{
    public ItemVisual(uint icon, uint underlay, uint overlay, uint uiEffects, uint plate = 0)
    {
        Plate = plate;
        Icon = icon;
        Underlay = underlay;
        Overlay = overlay;
        UiEffects = uiEffects;
    }

    public uint Icon { get; }
    public uint Underlay { get; }
    public uint Overlay { get; }
    /// <summary>The client's UI-effect flags for the object (Decal's IconOutline value). Flags, not a portal id.</summary>
    public uint UiEffects { get; }
    /// <summary>The item-type plate a cell draws behind the icon; a drag image leaves it out.</summary>
    public uint Plate { get; }

    public bool Equals(ItemVisual? other) => other != null
        && (Icon, Underlay, Overlay, UiEffects, Plate).Equals((other.Icon, other.Underlay, other.Overlay, other.UiEffects, other.Plate));
    public override bool Equals(object? obj) => Equals(obj as ItemVisual);
    public override int GetHashCode() => Icon.GetHashCode();
}

/// <summary>An item in a pack, at a slot index of its container.</summary>
public sealed class InventoryItem : IEquatable<InventoryItem>
{
    public InventoryItem(uint id, string name, uint container, int slot, ItemVisual visual, int stackCount, int stackMax, int wcid, uint validLocations)
    {
        Id = id;
        Name = name ?? string.Empty;
        Container = container;
        Slot = slot;
        Visual = visual ?? throw new ArgumentNullException(nameof(visual));
        StackCount = stackCount;
        StackMax = stackMax;
        Wcid = wcid;
        ValidLocations = validLocations;
    }

    public uint Id { get; }
    public string Name { get; }
    /// <summary>The pack the item is in: the main pack's id (the character's) or a side pack's.</summary>
    public uint Container { get; }
    /// <summary>The slot index within the container, where zero is the first slot.</summary>
    public int Slot { get; }
    public ItemVisual Visual { get; }
    public int StackCount { get; }
    public int StackMax { get; }
    /// <summary>The weenie class id (WCID) of the item: the kind of object it is, as Decal's LongValueKey.Type reports it.</summary>
    public int Wcid { get; }
    /// <summary>The wield locations the item can take (the AC EquipMask bits).</summary>
    public uint ValidLocations { get; }

    public bool Equals(InventoryItem? other) => other != null && (Id, Name, Container, Slot, Visual, StackCount, StackMax, Wcid, ValidLocations)
        .Equals((other.Id, other.Name, other.Container, other.Slot, other.Visual, other.StackCount, other.StackMax, other.Wcid, other.ValidLocations));
    public override bool Equals(object? obj) => Equals(obj as InventoryItem);
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>An equipped item and the paperdoll slots it covers.</summary>
public sealed class WieldedItem : IEquatable<WieldedItem>
{
    public WieldedItem(uint id, string name, ItemVisual visual, int stackCount, int stackMax, int wcid, uint validLocations, IReadOnlyList<PaperdollSlot> slots)
    {
        Id = id;
        Name = name ?? string.Empty;
        Visual = visual ?? throw new ArgumentNullException(nameof(visual));
        StackCount = stackCount;
        StackMax = stackMax;
        Wcid = wcid;
        ValidLocations = validLocations;
        Slots = slots ?? throw new ArgumentNullException(nameof(slots));
    }

    public uint Id { get; }
    public string Name { get; }
    public ItemVisual Visual { get; }
    public int StackCount { get; }
    public int StackMax { get; }
    /// <summary>The weenie class id (WCID), as <see cref="InventoryItem.Wcid"/>.</summary>
    public int Wcid { get; }
    /// <summary>The wield locations the item can take (the AC EquipMask bits).</summary>
    public uint ValidLocations { get; }
    /// <summary>Every paperdoll slot the item is drawn in: one for most items, several for a multi-slot item.</summary>
    public IReadOnlyList<PaperdollSlot> Slots { get; }

    public bool Equals(WieldedItem? other) => other != null && (Id, Name, Visual, StackCount, StackMax, Wcid, ValidLocations)
        .Equals((other.Id, other.Name, other.Visual, other.StackCount, other.StackMax, other.Wcid, other.ValidLocations)) && Slots.SequenceEqual(other.Slots);
    public override bool Equals(object? obj) => Equals(obj as WieldedItem);
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>The character's inventory at one moment. Immutable once built, and equal when the content is equal.</summary>
public sealed class InventorySnapshot : IEquatable<InventorySnapshot>
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
    public static InventorySnapshot Empty { get; } = new(new InventoryPack(0, string.Empty, 0, 0), Array.Empty<InventoryPack>(),
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
    /// <summary>The open container's id, or zero when none is open.</summary>
    public uint OpenContainer { get; }
    /// <summary>The selected item's id, or zero when nothing is selected.</summary>
    public uint Selected { get; }

    /// <summary>Whether the id is the main pack, a side pack, or an item or wielded item in this snapshot.</summary>
    public bool Contains(uint id) => id != 0 && (MainPack.Id == id || SidePacks.Any(pack => pack.Id == id)
        || Items.Any(item => item.Id == id) || Wielded.Any(item => item.Id == id));

    /// <summary>The wield locations of an item this snapshot holds, carried or worn; null when it holds no such item.</summary>
    public uint? ValidLocationsOf(uint id) =>
        Items.FirstOrDefault(item => item.Id == id)?.ValidLocations ?? Wielded.FirstOrDefault(item => item.Id == id)?.ValidLocations;

    /// <summary>The explicit wield mask for a set of valid locations in one paperdoll slot. Zero means the item cannot go in that slot.</summary>
    public static uint MaskFor(uint validLocations, PaperdollSlot slot) => InventorySnapshotBuilder.Covers(validLocations, slot);

    public bool Equals(InventorySnapshot? other) => other != null && MainPack.Equals(other.MainPack)
        && SidePacks.SequenceEqual(other.SidePacks) && Items.SequenceEqual(other.Items) && Wielded.SequenceEqual(other.Wielded)
        && (Burden, BurdenLimit, Pyreals, OpenContainer, Selected).Equals((other.Burden, other.BurdenLimit, other.Pyreals, other.OpenContainer, other.Selected));
    public override bool Equals(object? obj) => Equals(obj as InventorySnapshot);
    public override int GetHashCode() => OpenContainer.GetHashCode();
}

/// <summary>A port for tests: records commands as text, and lets a test set the snapshot and raise <see cref="Changed"/>.</summary>
public sealed class FakeInventoryPort : IInventoryPort
{
    public InventorySnapshot Snapshot { get; private set; } = InventorySnapshot.Empty;
    /// <summary>Every command received, in order, as text, for example "open 0x…" or "move 0x… to 0x… slot 3".</summary>
    public List<string> Commands { get; } = new();
    /// <summary>The valid locations of items the snapshot does not hold (another window's container, a corpse), as the port reads them from the world.</summary>
    public Dictionary<uint, uint> WorldLocations { get; } = new();
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
    public void Assess(uint itemId) => Commands.Add($"assess 0x{itemId:X8}");
    public void DropOnGround(uint itemId) => Commands.Add($"drop 0x{itemId:X8}");
    public void MoveToContainer(uint itemId, uint containerId, int slotIndex) => Commands.Add($"move 0x{itemId:X8} to 0x{containerId:X8} slot {slotIndex}");
    public uint WieldMask(uint itemId, PaperdollSlot slot)
    {
        var valid = Snapshot.ValidLocationsOf(itemId) ?? (WorldLocations.TryGetValue(itemId, out var world) ? world : 0u);
        return InventorySnapshot.MaskFor(valid, slot);
    }

    // The same rule as the port: nothing is sent when the item cannot go in the slot.
    public void Wield(uint itemId, PaperdollSlot slot)
    {
        if (WieldMask(itemId, slot) != 0) Commands.Add($"wield 0x{itemId:X8} to {slot}");
    }

    public void MergeStack(uint itemId, uint targetStackId) => Commands.Add($"merge 0x{itemId:X8} into 0x{targetStackId:X8}");
}
