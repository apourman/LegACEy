using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class InventorySnapshotBuilderTests
{
    private const uint Character = 0x50000001;
    private const uint PackInSlotZero = 0x50000010;
    private const uint PackInSlotTwo = 0x50000011;

    // AC EquipMask bits (ACE.Entity.Enum.EquipMask)
    private const uint ChestArmor = 0x00000200;
    private const uint UpperArmArmor = 0x00000800;
    private const uint LowerArmArmor = 0x00001000;
    private const uint Shield = 0x00200000;

    [Fact]
    public void Side_packs_follow_retail_order_and_free_slots_show_as_placeholders_up_to_capacity()
    {
        var reader = new FakeReader { SidePackCapacity = 3 };
        reader.Objects.Add(Pack(PackInSlotTwo, slot: 2, capacity: 24));
        reader.Objects.Add(Pack(PackInSlotZero, slot: 0, capacity: 17));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(new uint[] { PackInSlotZero, 0, PackInSlotTwo }, snapshot.SidePacks.Select(pack => pack.Id));
        Assert.Equal(new[] { 17, 0, 24 }, snapshot.SidePacks.Select(pack => pack.Capacity));
        Assert.Equal(new[] { 1, 2, 3 }, snapshot.SidePacks.Select(pack => pack.Order));
        Assert.Equal(Character, snapshot.MainPack.Id);
    }

    [Fact]
    public void Items_sit_at_their_slot_index_in_their_own_pack_and_packs_and_wielded_items_are_not_items()
    {
        var reader = new FakeReader { SidePackCapacity = 1 };
        reader.Objects.Add(Pack(PackInSlotZero, slot: 0, capacity: 24));
        reader.Objects.Add(Item(0x50000100, container: Character, slot: 4));
        reader.Objects.Add(Item(0x50000101, container: PackInSlotZero, slot: 0));
        reader.Objects.Add(Item(0x50000102, container: Character, slot: 0, equippedMask: ChestArmor));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(new[] { (Character, 4), (PackInSlotZero, 0) }, snapshot.Items.Select(item => (item.Container, item.Slot)));
        Assert.Equal(new uint[] { 0x50000102 }, snapshot.Wielded.Select(item => item.Id));
    }

    [Fact]
    public void A_multi_slot_armour_item_is_in_every_slot_it_covers_and_an_off_hand_weapon_is_in_the_shield_slot()
    {
        var reader = new FakeReader();
        reader.Objects.Add(Item(0x50000200, container: Character, slot: -1, equippedMask: ChestArmor | UpperArmArmor | LowerArmArmor));
        reader.Objects.Add(Item(0x50000201, container: Character, slot: -1, equippedMask: Shield));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        var coat = snapshot.Wielded.Single(item => item.Id == 0x50000200);
        Assert.Equal(new[] { PaperdollSlot.UpperArms, PaperdollSlot.Chest, PaperdollSlot.LowerArms }, coat.Slots);
        var offHand = snapshot.Wielded.Single(item => item.Id == 0x50000201);
        Assert.Equal(new[] { PaperdollSlot.Shield }, offHand.Slots);
    }

    [Fact]
    public void Burden_is_shown_against_the_server_limit_and_pyreals_total_the_pyreal_stacks_in_packs()
    {
        var reader = new FakeReader { Burden = 4200, Strength = 100, CarryingAugmentations = 2, OpenContainer = PackInSlotZero, Selected = 0x50000101 };
        reader.Objects.Add(Pack(PackInSlotZero, slot: 0, capacity: 24));
        reader.Objects.Add(Item(0x50000110, container: Character, slot: 0, itemType: InventorySnapshotBuilder.PyrealItemType, stackCount: 40));
        reader.Objects.Add(Item(0x50000111, container: PackInSlotZero, slot: 3, itemType: InventorySnapshotBuilder.PyrealItemType, stackCount: 10));
        reader.Objects.Add(Item(0x50000101, container: PackInSlotZero, slot: 1, itemType: 1, stackCount: 1));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(4200, snapshot.Burden);
        Assert.Equal(21000, snapshot.BurdenLimit);
        Assert.Equal(50, snapshot.Pyreals);
        Assert.Equal(PackInSlotZero, snapshot.OpenContainer);
        Assert.Equal(0x50000101u, snapshot.Selected);
    }

    private static InventoryObjectRead Pack(uint id, int slot, int capacity) =>
        new(id, "Pack", Character, slot, equippedMask: 0, stackCount: 1, itemSlots: capacity, isContainer: true, itemType: 0, Visual(id));

    private static InventoryObjectRead Item(uint id, uint container, int slot, uint equippedMask = 0, int stackCount = 1, int itemType = 1) =>
        new(id, "Item", container, slot, equippedMask, stackCount, itemSlots: 0, isContainer: false, itemType, Visual(id));

    private static ItemVisual Visual(uint icon) => new(icon, underlay: 0, overlay: 0, outline: 0);

    private sealed class FakeReader : IInventoryReader
    {
        public uint CharacterId => Character;
        public List<InventoryObjectRead> Objects { get; } = new();
        IReadOnlyList<InventoryObjectRead> IInventoryReader.Objects => Objects;
        public int MainPackCapacity { get; set; } = 96;
        public uint MainPackIcon { get; set; }
        public int SidePackCapacity { get; set; }
        public int Burden { get; set; }
        public int Strength { get; set; }
        public int CarryingAugmentations { get; set; }
        public uint OpenContainer { get; set; }
        public uint Selected { get; set; }
    }
}
