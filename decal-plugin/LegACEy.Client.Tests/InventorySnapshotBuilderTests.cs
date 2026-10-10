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
    private const uint ChestWear = 0x00000002;
    private const uint AbdomenWear = 0x00000004;
    private const uint UpperArmWear = 0x00000008;
    private const uint LowerArmWear = 0x00000010;
    private const uint UpperLegWear = 0x00000040;
    private const uint LowerLegWear = 0x00000080;

    [Fact]
    public void Side_packs_follow_retail_order_and_free_slots_show_as_placeholders_up_to_capacity()
    {
        var reader = new FakeReader { SidePackCapacity = 3 };
        reader.Objects.Add(Pack(PackInSlotTwo, slot: 2, capacity: 24));
        reader.Objects.Add(Pack(PackInSlotZero, slot: 0, capacity: 17));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(new uint[] { PackInSlotZero, 0, PackInSlotTwo }, snapshot.SidePacks.Select(pack => pack.Id));
        Assert.Equal(new[] { 17, 0, 24 }, snapshot.SidePacks.Select(pack => pack.Capacity));
        Assert.Equal(Character, snapshot.MainPack.Id);
    }

    [Fact]
    public void Items_sit_at_their_slot_index_in_their_own_pack_and_equipped_items_are_not_items()
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
    public void A_multi_slot_armour_item_is_in_every_slot_it_covers()
    {
        var reader = new FakeReader();
        reader.Objects.Add(Item(0x50000200, container: Character, slot: -1, equippedMask: ChestArmor | UpperArmArmor | LowerArmArmor));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        var coat = snapshot.Wielded.Single(item => item.Id == 0x50000200);
        Assert.Equal(new[] { PaperdollSlot.UpperArms, PaperdollSlot.Chest, PaperdollSlot.LowerArms }, coat.Slots);
    }

    [Theory]
    [InlineData(PaperdollSlot.Neck, 0x00008000u)]
    [InlineData(PaperdollSlot.Trinket, 0x04000000u)]
    [InlineData(PaperdollSlot.RightWrist, 0x00020000u)]
    [InlineData(PaperdollSlot.RightRing, 0x00080000u)]
    [InlineData(PaperdollSlot.Shield, 0x00200000u)]
    [InlineData(PaperdollSlot.Head, 0x00000001u)]
    [InlineData(PaperdollSlot.UpperArms, 0x00000800u)]
    [InlineData(PaperdollSlot.Chest, 0x00000200u)]
    [InlineData(PaperdollSlot.LowerArms, 0x00001000u)]
    [InlineData(PaperdollSlot.Abdomen, 0x00000400u)]
    [InlineData(PaperdollSlot.UpperLegs, 0x00002000u)]
    [InlineData(PaperdollSlot.Hands, 0x00000020u)]
    [InlineData(PaperdollSlot.LowerLegs, 0x00004000u)]
    [InlineData(PaperdollSlot.Feet, 0x00000100u)]
    [InlineData(PaperdollSlot.AetheriaOne, 0x10000000u)]
    [InlineData(PaperdollSlot.AetheriaTwo, 0x20000000u)]
    [InlineData(PaperdollSlot.AetheriaThree, 0x40000000u)]
    [InlineData(PaperdollSlot.LeftWrist, 0x00010000u)]
    [InlineData(PaperdollSlot.LeftRing, 0x00040000u)]
    [InlineData(PaperdollSlot.Weapon, 0x00100000u)]
    [InlineData(PaperdollSlot.Ammo, 0x00800000u)]
    [InlineData(PaperdollSlot.Cloak, 0x08000000u)]
    [InlineData(PaperdollSlot.Shirt, ChestWear)]
    [InlineData(PaperdollSlot.Pants, AbdomenWear)]
    public void A_single_bit_mask_is_in_exactly_its_slot(PaperdollSlot slot, uint mask)
    {
        var reader = new FakeReader();
        reader.Objects.Add(Item(0x50000300, container: Character, slot: -1, equippedMask: mask));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(new[] { slot }, snapshot.Wielded.Single().Slots);
    }

    [Fact]
    public void A_full_robe_covers_the_shirt_and_the_pants()
    {
        var robe = ChestWear | AbdomenWear | UpperArmWear | LowerArmWear | UpperLegWear | LowerLegWear;
        var reader = new FakeReader();
        reader.Objects.Add(Item(0x50000400, container: Character, slot: -1, equippedMask: robe));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(new[] { PaperdollSlot.Shirt, PaperdollSlot.Pants }, snapshot.Wielded.Single().Slots);
    }

    [Fact]
    public void Wield_mask_is_the_item_s_valid_locations_within_the_slot_and_zero_where_it_cannot_go()
    {
        var reader = new FakeReader();
        reader.Objects.Add(Item(0x50000500, container: PackInSlotZero, slot: 0, validLocations: Shield));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        var valid = snapshot.ValidLocationsOf(0x50000500) ?? 0;
        Assert.Equal(Shield, InventorySnapshot.MaskFor(valid, PaperdollSlot.Shield));
        Assert.Equal(0u, InventorySnapshot.MaskFor(valid, PaperdollSlot.Chest));
    }

    [Fact]
    public void The_burden_limit_stops_growing_at_the_augmentation_cap_and_pyreals_total_the_pyreal_stacks_in_packs()
    {
        var reader = new FakeReader { Strength = 100, CarryingAugmentations = 10 };
        reader.Objects.Add(Pack(PackInSlotZero, slot: 0, capacity: 24));
        reader.Objects.Add(Item(0x50000110, container: Character, slot: 0, itemType: 273, stackCount: 40));
        reader.Objects.Add(Item(0x50000111, container: PackInSlotZero, slot: 3, itemType: 273, stackCount: 10));
        reader.Objects.Add(Item(0x50000112, container: PackInSlotZero, slot: 1, itemType: 1, stackCount: 1));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(30000, snapshot.BurdenLimit);
        Assert.Equal(50, snapshot.Pyreals);
    }

    private static InventoryObjectRead Pack(uint id, int slot, int capacity) =>
        new(id, "Pack", Character, slot, 0, 0, 1, 0, capacity, true, 0, Visual(id));

    private static InventoryObjectRead Item(uint id, uint container, int slot, uint equippedMask = 0, uint validLocations = 0,
        int stackCount = 1, int itemType = 1) =>
        new(id, "Item", container, slot, equippedMask, validLocations, stackCount, 1, 0, false, itemType, Visual(id));

    private static ItemVisual Visual(uint icon) => new(icon, underlay: 0, overlay: 0, uiEffects: 0);

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
