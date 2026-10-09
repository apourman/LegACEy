using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The inventory port over Decal. Reads and commands run on the game thread, which is the render callback's thread
/// (Decal raises its filter events there too). Decal events only mark the snapshot stale; <see cref="Flush"/>, called
/// once per frame, rebuilds it, so any number of events in a frame raise <see cref="Changed"/> once.
/// </summary>
internal sealed class DecalInventoryPort : IInventoryPort
{
    private InventorySnapshot _snapshot = InventorySnapshot.Empty;
    private bool _stale;

    public InventorySnapshot Snapshot => _snapshot;
    public event Action? Changed;

    /// <summary>Subscribes to the Decal events that change the inventory. Called once the filters are ready.</summary>
    public void Attach()
    {
        var world = CoreManager.Current.WorldFilter;
        world.CreateObject += OnChanged;
        world.ChangeObject += OnChanged;
        world.ReleaseObject += OnChanged;
        world.MoveObject += OnChanged;
        // Decal's client hooks: a pack opened (by anyone) and a selection made (by anyone), so the window never disagrees with retail.
        CoreManager.Current.ContainerOpened += OnChanged;
        CoreManager.Current.ItemSelected += OnChanged;
    }

    public void Detach()
    {
        var world = CoreManager.Current.WorldFilter;
        world.CreateObject -= OnChanged;
        world.ChangeObject -= OnChanged;
        world.ReleaseObject -= OnChanged;
        world.MoveObject -= OnChanged;
        CoreManager.Current.ContainerOpened -= OnChanged;
        CoreManager.Current.ItemSelected -= OnChanged;
    }

    public void MarkStale() => _stale = true;

    /// <summary>Rebuilds the snapshot when a change is pending and the character is in the world, then raises <see cref="Changed"/>.</summary>
    public void Flush(bool inGame)
    {
        if (!_stale || !inGame) return;
        try { _snapshot = InventorySnapshotBuilder.Build(new DecalInventoryReader()); }
        catch (COMException)
        {
            // Decal's objects are not readable yet; keep the last snapshot and retry next frame.
            return;
        }
        _stale = false;
        Changed?.Invoke();
    }

    /// <summary>Logoff: the inventory is gone, so the snapshot empties without a change event.</summary>
    public void Clear()
    {
        _snapshot = InventorySnapshot.Empty;
        _stale = false;
    }

    private void OnChanged(object? sender, EventArgs e) => _stale = true;

    // Opening a pack is retail's use of the pack (UseItem with state 0). Mag-Tools/Macros/OpenMainPackOnLogin.cs opens
    // the main pack this way with the character's id, and Mag-Tools/Views/InventoryToolsView.cs opens a container by its id.
    // Decal.Adapter XML: HooksWrapper.UseItem(Int32, Int32) "Uses an item ... 0 uses an item by itself".
    public void OpenContainer(uint containerId) => CoreManager.Current.Actions.UseItem(Id(containerId), 0);

    // Decal.Adapter XML: HooksWrapper.SelectItem(Int32), "Selects an item", 0 clears the selection.
    // Mag-Tools/PluginCore.cs selects by id with CoreManager.Current.Actions.SelectItem(objectId).
    public void Select(uint itemId) => CoreManager.Current.Actions.SelectItem(Id(itemId));

    // Decal.Adapter XML: HooksWrapper.UseItem(Int32, Int32), state 0. Mag-Tools/PluginCore.cs: UseItem(objectId, 0).
    public void Use(uint itemId) => CoreManager.Current.Actions.UseItem(Id(itemId), 0);

    // Decal.Adapter XML: HooksWrapper.MoveItem(Int32, Int32, Int32, Boolean): "slot ... where 0 is the first slot", and
    // stack false places the item in that slot rather than adding it to a stack there. Mag-Tools/Macros/InventoryPacker.cs
    // calls MoveItem(item.Id, packId, 0, true) the same way.
    public void MoveToContainer(uint itemId, uint containerId, int slotIndex) =>
        CoreManager.Current.Actions.MoveItem(Id(itemId), Id(containerId), slotIndex, false);

    // Decal.Adapter XML: HooksWrapper.AutoWield(Int32, Int32, Int32, Int32): "1 if explicit placement, 0 if automatic", the
    // second flag is 0 when the first is 1. The slot argument is the equip mask. Mag-Tools/Macros/InventoryPacker.cs and the
    // patri0t86/ACManager StateMachine/States/Equipping.cs passes an EquipMask value as the slot. Gate: confirm the explicit
    // placement lands in the mask's slot.
    public void Wield(uint itemId, uint equipMask) =>
        CoreManager.Current.Actions.AutoWield(Id(itemId), unchecked((int)equipMask), 1, 0);

    // Decal.Adapter XML: HooksWrapper.MoveItem(Int32, Int32), "Moves an item to the front of the specified container". Given a
    // stack as the destination it merges with that stack. Mag-Tools/Macros/InventoryPacker.cs merges same-named stacks this way:
    // MoveItem(item.Id, secondItem.Id).
    public void MergeStack(uint itemId, uint targetStackId) =>
        CoreManager.Current.Actions.MoveItem(Id(itemId), Id(targetStackId));

    private static int Id(uint id) => unchecked((int)id);

    /// <summary>The client's inventory reads. Each one is read on the game thread, once per rebuild.</summary>
    private sealed class DecalInventoryReader : IInventoryReader
    {
        public uint CharacterId => unchecked((uint)CoreManager.Current.CharacterFilter.Id);

        public IReadOnlyList<InventoryObjectRead> Objects
        {
            get
            {
                // Decal's inventory: the packs, what is in them, and what is wielded. Mag-Tools/Macros/InventoryPacker.cs and
                // UtilityBelt's Util.PyrealCount both enumerate WorldFilter.GetInventory() this way.
                var objects = new List<InventoryObjectRead>();
                foreach (WorldObject item in CoreManager.Current.WorldFilter.GetInventory())
                    objects.Add(Read(item));
                return objects;
            }
        }

        // The main pack is the character's own object: its ItemSlots is the pack's slot count, and PackSlots counts the side packs.
        // Mag-Tools/Macros/InventoryPacker.cs reads PackSlots from the character object; Mag-Plugins' Shared/Util.cs reads ItemSlots.
        public int MainPackCapacity => CharacterObject()?.Values(LongValueKey.ItemSlots, 0) ?? 0;
        public uint MainPackIcon => Texture(CharacterObject()?.Icon ?? 0);
        public int SidePackCapacity => CharacterObject()?.Values(LongValueKey.PackSlots, 0) ?? 0;

        // Decal.Adapter XML names: CharacterFilter.BurdenUnits (the current burden in the client's units).
        // UtilityBelt's Util.GetFriendlyBurden reads BurdenUnits the same way.
        public int Burden => (int)CoreManager.Current.CharacterFilter.BurdenUnits;

        // UtilityBelt's Util.GetFriendlyBurden reads CharacterFilter.EffectiveAttribute[CharFilterAttributeType.Strength].
        public int Strength => CoreManager.Current.CharacterFilter.EffectiveAttribute[Decal.Adapter.Wrappers.CharFilterAttributeType.Strength];

        // The augmentation count is the server's AugmentationIncreasedCarryingCapacity property (ACE PropertyInt 230, the
        // Might of the Seventh Mule count), read with CharacterFilter.GetCharProperty as UtilityBelt reads it.
        // Gate: the snapshot's BurdenLimit must match the server's EncumbranceCapacity.
        public int CarryingAugmentations => CoreManager.Current.CharacterFilter.GetCharProperty(CarryingAugmentationProperty);

        // Decal.Adapter XML: HooksWrapper.OpenedContainer and HooksWrapper.CurrentSelection, both 0 when none.
        public uint OpenContainer => unchecked((uint)CoreManager.Current.Actions.OpenedContainer);
        public uint Selected => unchecked((uint)CoreManager.Current.Actions.CurrentSelection);

        private const int CarryingAugmentationProperty = 230;

        private static WorldObject? CharacterObject() => CoreManager.Current.WorldFilter[CoreManager.Current.CharacterFilter.Id];

        private static InventoryObjectRead Read(WorldObject item)
        {
            var visual = new ItemVisual(Texture(item.Icon), Texture(item.Values(LongValueKey.IconUnderlay, 0)),
                Texture(item.Values(LongValueKey.IconOverlay, 0)), unchecked((uint)item.Values(LongValueKey.IconOutline, 0)));
            return new InventoryObjectRead(unchecked((uint)item.Id), item.Name, unchecked((uint)item.Container),
                item.Values(LongValueKey.Slot, -1), unchecked((uint)item.Values(LongValueKey.EquippedSlots, 0)),
                item.Values(LongValueKey.StackCount, 1), item.Values(LongValueKey.ItemSlots, 0),
                item.ObjectClass == ObjectClass.Container, item.Values(LongValueKey.Type, 0), visual);
        }

        // Decal reports portal texture ids without their 0x06 prefix; the same rule as ClientUiRuntime.ObjectIcon.
        private static uint Texture(int value) => value == 0 ? 0 : (value & 0xFF000000) == 0 ? unchecked((uint)value) | 0x06000000 : unchecked((uint)value);
    }
}
