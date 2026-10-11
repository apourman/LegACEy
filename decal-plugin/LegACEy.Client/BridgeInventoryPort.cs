using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The inventory port over the bridge. Reads and commands run on the game thread, the frame event's thread. Inventory events mark the
/// snapshot stale only when they touch the character's inventory; <see cref="Flush"/> rebuilds it once per frame.
/// </summary>
internal sealed class BridgeInventoryPort : IInventoryPort
{
    private readonly InventoryRefresh _refresh;

    public BridgeInventoryPort(Action<string> log) =>
        _refresh = new InventoryRefresh(() => InventorySnapshotBuilder.Build(new BridgeInventoryReader()), log);

    public InventorySnapshot Snapshot => _refresh.Snapshot;

    public event Action? Changed
    {
        add => _refresh.Changed += value;
        remove => _refresh.Changed -= value;
    }

    public void MarkStale() => _refresh.MarkStale();

    /// <summary>Rebuilds the snapshot when it is stale and the character is in the world.</summary>
    public void Flush(bool inGame)
    {
        if (inGame) _refresh.Flush();
    }

    /// <summary>Logoff: the snapshot empties.</summary>
    public void Clear() => _refresh.Reset();

    /// <summary>
    /// A world object was created, changed, released or moved. It matters when it is the character, sits in the character's inventory,
    /// or is already in the snapshot.
    /// </summary>
    public void ObjectChanged(uint id, uint container)
    {
        var characterId = (uint)Bridge.Fact(BridgeFact.CharacterId);
        var snapshot = _refresh.Snapshot;
        if (id == characterId || container == characterId || snapshot.Contains(id) || snapshot.Contains(container))
            _refresh.MarkStale();
    }

    // Opening a pack is Decal's UseItem with state 0, as retail's double-click does it.
    // Gate: opening an already-open pack, and the main pack, match retail.
    public void OpenContainer(uint containerId) => Bridge.UseItem(containerId, 0);

    public void Select(uint itemId) => Bridge.SelectItem(itemId);

    public void Use(uint itemId) => Bridge.UseItem(itemId, 0);

    public void Assess(uint itemId)
    {
        Bridge.SelectItem(itemId);
        Bridge.RequestId(itemId);
    }

    public void DropOnGround(uint itemId) => Bridge.DropItem(itemId);

    // Stack false places the item in the slot rather than adding it to a stack there.
    public void MoveToContainer(uint itemId, uint containerId, int slotIndex) => Bridge.MoveItem(itemId, containerId, slotIndex, stack: false);

    // An item the snapshot does not hold (one from a chest or corpse) has its valid locations read from the world, the same
    // EquipableSlots key the snapshot reads.
    public uint WieldMask(uint itemId, PaperdollSlot slot) =>
        InventorySnapshot.MaskFor(_refresh.Snapshot.ValidLocationsOf(itemId) ?? LiveValidLocations(itemId), slot);

    // Explicit placement (1) into the item's valid locations within the slot; nothing is sent when there are none.
    // Gate: the mask lands in that slot, and a refused slot sends nothing.
    public void Wield(uint itemId, PaperdollSlot slot)
    {
        var mask = WieldMask(itemId, slot);
        if (mask != 0) Bridge.AutoWield(itemId, mask, 1, 0);
    }

    // A move onto a stack merges the stacks where the server allows it.
    public void MergeStack(uint itemId, uint targetStackId) => Bridge.MergeItem(itemId, targetStackId);

    // Runs every frame during a retail drag over a paperdoll slot, so a failed read or a released object reads as no slots.
    private static uint LiveValidLocations(uint itemId)
    {
        try { return Bridge.Item(itemId)?.ValidLocations ?? 0; }
        catch (COMException) { return 0; }
    }

    /// <summary>The client's inventory reads, made on the game thread once per rebuild.</summary>
    private sealed class BridgeInventoryReader : IInventoryReader
    {
        public uint CharacterId => (uint)Bridge.Fact(BridgeFact.CharacterId);
        public IReadOnlyList<InventoryObjectRead> Objects => Bridge.Inventory();
        public int MainPackCapacity => Bridge.Fact(BridgeFact.MainPackCapacity);
        public uint MainPackIcon => (uint)Bridge.Fact(BridgeFact.MainPackIcon);
        public int SidePackCapacity => Bridge.Fact(BridgeFact.SidePackCapacity);
        public int Burden => Bridge.Fact(BridgeFact.Burden);
        public int Strength => Bridge.Fact(BridgeFact.Strength);
        // Gate: BurdenLimit must match the server's EncumbranceCapacity.
        public int CarryingAugmentations => Bridge.Fact(BridgeFact.CarryingAugmentations);
        // Decal documents OpenedContainer as a chest or corpse. Gate: a side pack and the main pack report here too.
        public uint OpenContainer => (uint)Bridge.Fact(BridgeFact.OpenedContainer);
        public uint Selected => (uint)Bridge.Fact(BridgeFact.CurrentSelection);
    }
}
