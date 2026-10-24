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
    private const uint FootWear = 0x00000100;

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
    public void A_focus_keeps_its_overlay_so_Foci_of_Shadow_is_not_drawn_as_Foci_of_Strife()
    {
        var reader = new FakeReader { SidePackCapacity = 1 };
        // Foci of Shadow (WCID 43173) is Foci of Strife's icon under a purple overlay.
        reader.Objects.Add(new InventoryObjectRead(PackInSlotZero, "Foci of Shadow", Character, 0, 0, 0, 1, 0, 0, true, 43173,
            new ItemVisual(0x06001F64, underlay: 0, overlay: 0x06006E7A, uiEffects: 0, plate: 0x06005D2D)));

        var focus = InventorySnapshotBuilder.Build(reader).SidePacks.Single().Visual;

        Assert.Equal(new ItemVisual(0x06001F64, 0, 0x06006E7A, 0), focus);
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
    [InlineData(PaperdollSlot.RightWrist, 0x00020000u)]
    [InlineData(PaperdollSlot.RightRing, 0x00080000u)]
    [InlineData(PaperdollSlot.LeftWrist, 0x00010000u)]
    [InlineData(PaperdollSlot.LeftRing, 0x00040000u)]
    public void A_single_bit_mask_is_in_exactly_its_slot_and_left_and_right_do_not_swap(PaperdollSlot slot, uint mask)
    {
        var reader = new FakeReader();
        reader.Objects.Add(Item(0x50000300, container: Character, slot: -1, equippedMask: mask));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(new[] { slot }, snapshot.Wielded.Single().Slots);
    }

    [Fact]
    public void A_full_robe_is_a_shirt_and_takes_only_the_shirt_slot()
    {
        var robe = ChestWear | AbdomenWear | UpperArmWear | LowerArmWear | UpperLegWear | LowerLegWear;
        var reader = new FakeReader();
        reader.Objects.Add(Item(0x50000400, container: Character, slot: -1, equippedMask: robe));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(new[] { PaperdollSlot.Shirt }, snapshot.Wielded.Single().Slots);
    }

    [Fact]
    public void A_shirt_that_covers_the_abdomen_shows_in_the_shirt_slot_only_and_the_pants_keep_theirs()
    {
        var shirt = ChestWear | AbdomenWear | UpperArmWear | LowerArmWear;
        var pants = UpperLegWear | LowerLegWear;
        var reader = new FakeReader();
        reader.Objects.Add(Item(0x50000460, container: Character, slot: -1, equippedMask: shirt, validLocations: shirt));
        reader.Objects.Add(Item(0x50000461, container: Character, slot: -1, equippedMask: pants, validLocations: pants));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(new[] { PaperdollSlot.Shirt }, snapshot.Wielded.Single(item => item.Id == 0x50000460).Slots);
        Assert.Equal(new[] { PaperdollSlot.Pants }, snapshot.Wielded.Single(item => item.Id == 0x50000461).Slots);
        Assert.Equal(0u, InventorySnapshot.MaskFor(shirt, PaperdollSlot.Pants));
    }

    [Fact]
    public void Boots_that_cover_the_lower_legs_show_in_feet_only_and_cannot_be_dropped_on_the_pants()
    {
        var boots = FootWear | LowerLegWear;
        var reader = new FakeReader();
        reader.Objects.Add(Item(0x50000450, container: Character, slot: -1, equippedMask: boots, validLocations: boots));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(new[] { PaperdollSlot.Feet }, snapshot.Wielded.Single().Slots);
        Assert.Equal(0u, InventorySnapshot.MaskFor(boots, PaperdollSlot.Pants));
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
        reader.Objects.Add(Item(0x50000110, container: Character, slot: 0, wcid: 273, stackCount: 40));
        reader.Objects.Add(Item(0x50000111, container: PackInSlotZero, slot: 3, wcid: 273, stackCount: 10));
        reader.Objects.Add(Item(0x50000112, container: PackInSlotZero, slot: 1, wcid: 1, stackCount: 1));

        var snapshot = InventorySnapshotBuilder.Build(reader);

        Assert.Equal(30000, snapshot.BurdenLimit);
        Assert.Equal(50, snapshot.Pyreals);
    }

    private static InventoryObjectRead Pack(uint id, int slot, int capacity) =>
        new(id, "Pack", Character, slot, 0, 0, 1, 0, capacity, true, 0, Visual(id));

    private static InventoryObjectRead Item(uint id, uint container, int slot, uint equippedMask = 0, uint validLocations = 0,
        int stackCount = 1, int wcid = 1) =>
        new(id, "Item", container, slot, equippedMask, validLocations, stackCount, 1, 0, false, wcid, Visual(id));

    private static ItemVisual Visual(uint icon) => new(icon, underlay: 0, overlay: 0, uiEffects: 0);

    private sealed class FakeReader : IInventoryReader
    {
        public uint CharacterId => Character;
        public string CharacterName => "Rand";
        public List<InventoryObjectRead> Objects { get; } = new();
        IReadOnlyList<InventoryObjectRead> IInventoryReader.Objects => Objects;
        public int MainPackCapacity { get; set; } = 96;
        public int SidePackCapacity { get; set; }
        public int Burden { get; set; }
        public int Strength { get; set; }
        public int CarryingAugmentations { get; set; }
        public uint OpenContainer { get; set; }
        public uint Selected { get; set; }
    }

    [Fact]
    public void The_doll_picks_the_item_on_top_of_each_body_part_by_where_it_is_worn()
    {
        WieldedItem Worn(uint id, uint mask) => new(id, "", new ItemVisual(0, 0, 0, 0), 1, 1, 0, mask, Array.Empty<PaperdollSlot>(), mask);
        var shirt = Worn(1, 0x2 | 0x8 | 0x10 | 0x4); // a baggy shirt: chest, arms and abdomen
        var coat = Worn(2, 0x200 | 0x800);
        var boots = Worn(3, 0x100 | 0x80);           // reaching the knee
        var greaves = Worn(4, 0x4000);
        var wielded = new[] { shirt, coat, boots, greaves };

        Assert.Same(coat, InventorySnapshotBuilder.WornOn(wielded, 9));
        Assert.Same(shirt, InventorySnapshotBuilder.WornOn(wielded, 0));
        Assert.Same(boots, InventorySnapshotBuilder.WornOn(wielded, 2));
        Assert.Same(shirt, InventorySnapshotBuilder.WornOn(wielded, 11));
        // A bare thigh, under nothing: the shirt reaches the abdomen but not the legs.
        Assert.Null(InventorySnapshotBuilder.WornOn(wielded, 1));

        // Knee-high boots cover the feet and shins; shoes only the feet.
        Assert.Equal(new[] { 2, 3, 4, 6, 7, 8 }, InventorySnapshotBuilder.PartsUnder(0x100 | 0x80));
        Assert.Equal(new[] { 3, 4, 7, 8 }, InventorySnapshotBuilder.PartsUnder(0x100));
        // A long coat's coverage reaches past its wield slots: chest, abdomen, both arms, and its skirt over the thighs.
        Assert.Equal(new[] { 0, 1, 5, 9, 10, 11, 13, 14 }, InventorySnapshotBuilder.PartsCovered(0x0400 | 0x0800 | 0x1000 | 0x2000 | 0x0100));
    }
}
