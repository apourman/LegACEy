using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The inventory port over Decal. Reads and commands run on the game thread, the render callback's thread. Decal events
/// mark the snapshot stale only when they touch the character's inventory; <see cref="Flush"/> rebuilds it once per frame.
/// </summary>
internal sealed class DecalInventoryPort : IInventoryPort
{
    private readonly InventoryRefresh _refresh;

    public DecalInventoryPort(Action<string> log) =>
        _refresh = new InventoryRefresh(() => InventorySnapshotBuilder.Build(new DecalInventoryReader()), log);

    public InventorySnapshot Snapshot => _refresh.Snapshot;

    public event Action? Changed
    {
        add => _refresh.Changed += value;
        remove => _refresh.Changed -= value;
    }

    /// <summary>Subscribes to the Decal events that can change the inventory. Called once the filters are ready.</summary>
    public void Attach()
    {
        var world = CoreManager.Current.WorldFilter;
        world.CreateObject += OnCreated;
        world.ChangeObject += OnChanged;
        world.ReleaseObject += OnReleased;
        world.MoveObject += OnMoved;
        // The open container and the selection are part of the snapshot, whoever changed them.
        CoreManager.Current.ContainerOpened += OnSessionChanged;
        CoreManager.Current.ItemSelected += OnSessionChanged;
    }

    public void Detach()
    {
        var world = CoreManager.Current.WorldFilter;
        world.CreateObject -= OnCreated;
        world.ChangeObject -= OnChanged;
        world.ReleaseObject -= OnReleased;
        world.MoveObject -= OnMoved;
        CoreManager.Current.ContainerOpened -= OnSessionChanged;
        CoreManager.Current.ItemSelected -= OnSessionChanged;
    }

    public void MarkStale() => _refresh.MarkStale();

    /// <summary>Rebuilds the snapshot when it is stale and the character is in the world.</summary>
    public void Flush(bool inGame)
    {
        if (inGame) _refresh.Flush();
    }

    /// <summary>Logoff: the snapshot empties.</summary>
    public void Clear() => _refresh.Reset();

    private void OnCreated(object? sender, CreateObjectEventArgs e) => MarkIfMine(e.New);
    private void OnChanged(object? sender, ChangeObjectEventArgs e) => MarkIfMine(e.Changed);
    private void OnReleased(object? sender, ReleaseObjectEventArgs e) => MarkIfMine(e.Released);
    private void OnMoved(object? sender, MoveObjectEventArgs e) => MarkIfMine(e.Moved);
    private void OnSessionChanged(object? sender, EventArgs e) => _refresh.MarkStale();

    /// <summary>An object matters when it is the character, sits in the character's inventory, or is already in the snapshot.</summary>
    private void MarkIfMine(WorldObject? item)
    {
        if (item == null) return;
        var characterId = unchecked((uint)CoreManager.Current.CharacterFilter.Id);
        var id = unchecked((uint)item.Id);
        var container = unchecked((uint)item.Container);
        var snapshot = _refresh.Snapshot;
        if (id == characterId || container == characterId || snapshot.Contains(id) || snapshot.Contains(container))
            _refresh.MarkStale();
    }

    // Opening a pack is Decal's UseItem with state 0, as retail's double-click does it.
    // Gate: opening an already-open pack, and the main pack, match retail.
    public void OpenContainer(uint containerId) => CoreManager.Current.Actions.UseItem(Id(containerId), 0);

    public void Select(uint itemId) => CoreManager.Current.Actions.SelectItem(Id(itemId));

    public void Use(uint itemId) => CoreManager.Current.Actions.UseItem(Id(itemId), 0);

    // Stack false places the item in the slot rather than adding it to a stack there.
    public void MoveToContainer(uint itemId, uint containerId, int slotIndex) =>
        CoreManager.Current.Actions.MoveItem(Id(itemId), Id(containerId), slotIndex, false);

    // An item the snapshot does not hold (one from a chest or corpse) has its valid locations read from the world, the same
    // EquipableSlots key the snapshot reads.
    public uint WieldMask(uint itemId, PaperdollSlot slot) =>
        InventorySnapshot.MaskFor(_refresh.Snapshot.ValidLocationsOf(itemId) ?? LiveValidLocations(itemId), slot);

    // Explicit placement (1) into the item's valid locations within the slot; nothing is sent when there are none.
    // Gate: the mask lands in that slot, and a refused slot sends nothing.
    public void Wield(uint itemId, PaperdollSlot slot)
    {
        var mask = WieldMask(itemId, slot);
        if (mask != 0) CoreManager.Current.Actions.AutoWield(Id(itemId), unchecked((int)mask), 1, 0);
    }

    // A move onto a stack merges the stacks where the server allows it.
    public void MergeStack(uint itemId, uint targetStackId) => CoreManager.Current.Actions.MoveItem(Id(itemId), Id(targetStackId));

    private static int Id(uint id) => unchecked((int)id);

    // Runs every frame during a retail drag over a paperdoll slot, so a COMException or a released object reads as no slots.
    private static uint LiveValidLocations(uint itemId)
    {
        try
        {
            var item = CoreManager.Current.WorldFilter[Id(itemId)];
            return item == null ? 0 : unchecked((uint)item.Values(LongValueKey.EquipableSlots, 0));
        }
        catch (COMException) { return 0; }
    }

    /// <summary>The client's inventory reads, made on the game thread once per rebuild.</summary>
    private sealed class DecalInventoryReader : IInventoryReader
    {
        public uint CharacterId => unchecked((uint)CoreManager.Current.CharacterFilter.Id);

        public IReadOnlyList<InventoryObjectRead> Objects
        {
            get
            {
                var objects = new List<InventoryObjectRead>();
                foreach (WorldObject item in CoreManager.Current.WorldFilter.GetInventory())
                    objects.Add(Read(item));
                return objects;
            }
        }

        // The main pack is the character's own object: ItemSlots is its slot count, and PackSlots counts the side packs.
        public int MainPackCapacity => CharacterObject()?.Values(LongValueKey.ItemSlots, 0) ?? 0;
        public uint MainPackIcon => DecalIcons.Texture(CharacterObject()?.Icon ?? 0);
        public int SidePackCapacity => CharacterObject()?.Values(LongValueKey.PackSlots, 0) ?? 0;

        public int Burden => (int)CoreManager.Current.CharacterFilter.BurdenUnits;

        public int Strength => CoreManager.Current.CharacterFilter.EffectiveAttribute[CharFilterAttributeType.Strength];

        // The augmentation count is ACE's AugmentationIncreasedCarryingCapacity (PropertyInt 230).
        // Gate: BurdenLimit must match the server's EncumbranceCapacity.
        public int CarryingAugmentations => CoreManager.Current.CharacterFilter.GetCharProperty(CarryingAugmentationProperty);

        // Decal documents OpenedContainer as a chest or corpse. Gate: a side pack and the main pack report here too.
        public uint OpenContainer => unchecked((uint)CoreManager.Current.Actions.OpenedContainer);
        public uint Selected => unchecked((uint)CoreManager.Current.Actions.CurrentSelection);

        private const int CarryingAugmentationProperty = 230;

        private static WorldObject? CharacterObject() => CoreManager.Current.WorldFilter[CoreManager.Current.CharacterFilter.Id];

        private static InventoryObjectRead Read(WorldObject item) => new(unchecked((uint)item.Id), item.Name,
            unchecked((uint)item.Container), item.Values(LongValueKey.Slot, -1), unchecked((uint)item.Values(LongValueKey.EquippedSlots, 0)),
            unchecked((uint)item.Values(LongValueKey.EquipableSlots, 0)), item.Values(LongValueKey.StackCount, 1),
            item.Values(LongValueKey.StackMax, 0), item.Values(LongValueKey.ItemSlots, 0), item.ObjectClass == ObjectClass.Container,
            // Decal's Type is the weenie class id (WCID): pyreals are 273.
            item.Values(LongValueKey.Type, 0), DecalIcons.Visual(item));
    }
}
