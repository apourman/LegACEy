using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly Action<string> _log;

    public BridgeInventoryPort(Action<string> log)
    {
        _log = log;
        _refresh = new InventoryRefresh(() => InventorySnapshotBuilder.Build(new BridgeInventoryReader(log)).WithShortcuts(Shortcuts()), log);
        var clock = Stopwatch.StartNew();
        // Without retail's request flag, every move after the first waits out the queue's timeout.
        _moves = new InventoryMoveQueue(Send, () => clock.Elapsed, () => Request?.Pending ?? true);
    }

    private readonly InventoryMoveQueue _moves;

    /// <summary>Retail's shortcuts, read into each snapshot; none until the toolbar is read.</summary>
    public Func<IReadOnlyList<RetailShortcut>> Shortcuts { get; set; } = Array.Empty<RetailShortcut>;

    /// <summary>Retail's inventory request in flight, set once the native UI is checked; null paces moves by the timeout alone.</summary>
    public RetailInventoryRequest? Request { get; set; }

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
        if (!inGame) return;
        // The burden meter follows Strength, the carrying augmentation and the burden itself, none of which come with an inventory event:
        // a buff moves the limit, and the server's burden update lands after the rebuild a drop or an unwield triggered.
        // A read fails while the character leaves the world, or when the client does not match; the meter keeps the last value then,
        // and the snapshot still rebuilds on the inventory's own events.
        if (NativeCharacterStats.TryReadAttribute(NativeCharacterStats.Strength, raw: false, _log, out var strength) && strength != _strength)
        {
            _strength = strength;
            _refresh.MarkStale();
        }
        if (NativeCharacterStats.TryReadInt(NativeCharacterStats.EncumbranceVal, _log, out var burden) && burden != _burden)
        {
            _burden = burden;
            _refresh.MarkStale();
        }
        if (NativeCharacterStats.TryReadInt(NativeCharacterStats.CarryingAugmentations, _log, out var carrying) && carrying != _carrying)
        {
            _carrying = carrying;
            _refresh.MarkStale();
        }
        _refresh.Flush();
        _moves.Pump();
    }

    private int _strength, _burden, _carrying;

    /// <summary>Logoff: the snapshot empties.</summary>
    public void Clear()
    {
        _refresh.Reset();
        _moves.Clear();
    }

    /// <summary>
    /// A world object was created, changed, released or moved. It matters when it is the character, sits in the character's inventory,
    /// or is already in the snapshot.
    /// </summary>
    public void ObjectChanged(uint id, uint container)
    {
        var characterId = ClientUiRuntime.Identity.CharacterId;
        var snapshot = _refresh.Snapshot;
        if (id == characterId || container == characterId || snapshot.Contains(id) || snapshot.Contains(container))
            _refresh.MarkStale();
    }

    // Retail's own client-side pack switch: its inventory lists follow the pack the window shows, so a drag handed to retail finds the item.
    public void OpenContainer(uint containerId) => NativeContainerOpener.Open(containerId, _log);

    /// <summary>Retail's target mode, set once the native UI is checked; null leaves a click a plain selection.</summary>
    public RetailTargetMode? TargetMode { get; set; }

    // A click selects, and while retail waits for a target (a gem or kit in use) makes the item that target, as retail's item lists do.
    // An item the held one cannot be used on cancels the use instead, and is not selected.
    public void Select(uint itemId)
    {
        if (TargetMode?.CancelIfInvalid(itemId) == true) return;
        NativeSelection.Select(itemId);
        TargetMode?.TryTarget(itemId);
    }

    public void Use(uint itemId) => Bridge.UseItem(itemId, 0);

    public void Hover(uint itemId) => TargetMode?.Hover(itemId);

    public void Assess(uint itemId)
    {
        NativeSelection.Select(itemId);
        Bridge.RequestId(itemId);
    }

    // The selected stack's split by retail's amount, when the catalogue is valid; null keeps every move whole, as before.
    public StackSplitter? Split { get; set; }

    // A selected stack below the split amount drops that amount, not the whole stack.
    public void DropOnGround(uint itemId)
    {
        if (Split?.TrySplitTo3D(itemId) == true) return;
        Bridge.DropItem(itemId);
    }

    // Queued: retail refuses a move sent while another request waits on the server.
    public void MoveToContainer(uint itemId, uint containerId, int slotIndex) => _moves.Move(itemId, containerId, slotIndex);

    // Stack false places the item in the slot rather than adding it to a stack there.
    private void Send(uint itemId, uint containerId, int slotIndex)
    {
        if (Split?.TrySplitToContainer(itemId, containerId, slotIndex) == true) return;
        Bridge.MoveItem(itemId, containerId, slotIndex, stack: false);
    }

    // An item the snapshot does not hold (one from a chest or corpse) has its valid locations read from the world, the same
    // EquipableSlots key the snapshot reads.
    public uint WieldMask(uint itemId, PaperdollSlot slot) =>
        InventorySnapshot.MaskFor(_refresh.Snapshot.ValidLocationsOf(itemId) ?? LiveValidLocations(itemId), slot);

    // The item's valid locations within the slot, sent as retail's paperdoll sends a drop; nothing is sent when there are none.
    // Decal's AutoWield (explicit placement 1) is the fallback: it sends nothing for a second ring while the other finger is worn.
    public void Wield(uint itemId, PaperdollSlot slot)
    {
        var mask = WieldMask(itemId, slot);
        if (mask != 0 && !NativeWieldSender.TrySend(itemId, mask, _log)) Bridge.AutoWield(itemId, mask, 1, 0);
    }

    /// <summary>Answers <see cref="IsPack"/> from what the server sent about opened containers; set once the container window exists.</summary>
    public Func<uint, bool> OutsidePack { get; set; } = _ => false;

    public bool IsPack(uint itemId) => OutsidePack(itemId);

    // Read as the pointer moves during a drag, so a failed read or a released object reads as covering nothing.
    public uint CoverageOf(uint itemId)
    {
        try { return Bridge.CoverageOf(itemId); }
        catch (COMException) { return 0; }
    }

    // A move onto a stack merges the stacks where the server allows it.
    public void MergeStack(uint itemId, uint targetStackId) => Bridge.MergeItem(itemId, targetStackId);

    // Runs every frame during a retail drag over a paperdoll slot, so a failed read or a released object reads as no slots.
    private static uint LiveValidLocations(uint itemId)
    {
        try { return Bridge.ValidLocations(itemId); }
        catch (COMException) { return 0; }
    }

    /// <summary>The client's inventory reads, made on the game thread once per rebuild.</summary>
    private sealed class BridgeInventoryReader : IInventoryReader
    {
        private readonly Action<string> _log;

        public BridgeInventoryReader(Action<string> log) => _log = log;

        public uint CharacterId => ClientUiRuntime.Identity.CharacterId;
        public string CharacterName => ClientUiRuntime.Identity.CharacterName;
        public IReadOnlyList<InventoryObjectRead> Objects => Bridge.Inventory();
        // What the game cannot give reads as 0, which the snapshot builder takes as unknown (the default pack size, no burden limit).
        public int MainPackCapacity => NativeCharacterStats.TryReadPackCapacities(_log, out var main, out _) ? main : 0;
        public int SidePackCapacity => NativeCharacterStats.TryReadPackCapacities(_log, out _, out var side) ? side : 0;
        public int Burden => Int(NativeCharacterStats.EncumbranceVal);
        public int Strength => NativeCharacterStats.TryReadAttribute(NativeCharacterStats.Strength, raw: false, _log, out var strength) ? strength : 0;
        // Gate: BurdenLimit must match the server's EncumbranceCapacity.
        public int CarryingAugmentations => Int(NativeCharacterStats.CarryingAugmentations);
        // Decal documents OpenedContainer as a chest or corpse. Gate: a side pack and the main pack report here too.
        public uint OpenContainer => (uint)Bridge.Fact(BridgeFact.OpenedContainer);
        public uint Selected => NativeSelection.Selected;

        private int Int(uint property) => NativeCharacterStats.TryReadInt(property, _log, out var value) ? value : 0;
    }
}
