using System;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>A paperdoll slot, in the order of the Dereth paperdoll table (neck down to pants).</summary>
public enum PaperdollSlot
{
    Neck, Trinket, RightWrist, RightRing, Shield,
    Head, UpperArms, Chest, LowerArms, Abdomen, UpperLegs, Hands, LowerLegs, Feet,
    AetheriaOne, AetheriaTwo, AetheriaThree,
    LeftWrist, LeftRing, Weapon, Ammo,
    Cloak, Shirt, Pants,
}

/// <summary>One object the character holds, as the Decal reads give it. Packs, their contents and wielded items all appear here.</summary>
public sealed class InventoryObjectRead
{
    public InventoryObjectRead(uint id, string name, uint container, int slot, uint equippedMask, int stackCount, int itemSlots,
        bool isContainer, int itemType, ItemVisual visual)
    {
        Id = id;
        Name = name ?? string.Empty;
        Container = container;
        Slot = slot;
        EquippedMask = equippedMask;
        StackCount = stackCount;
        ItemSlots = itemSlots;
        IsContainer = isContainer;
        ItemType = itemType;
        Visual = visual ?? throw new ArgumentNullException(nameof(visual));
    }

    public uint Id { get; }
    public string Name { get; }
    /// <summary>The object holding this one: the character for the main pack and wielded items, a pack for a pack's contents.</summary>
    public uint Container { get; }
    /// <summary>The slot index in the container. Wielded items and the character's own objects use -1 or their wield location.</summary>
    public int Slot { get; }
    /// <summary>The wield location (the AC EquipMask bits) of an equipped item; zero when it is not equipped.</summary>
    public uint EquippedMask { get; }
    public int StackCount { get; }
    /// <summary>For a container, its number of slots; zero for anything else.</summary>
    public int ItemSlots { get; }
    public bool IsContainer { get; }
    /// <summary>The item-type value the client reports; <see cref="InventorySnapshotBuilder.PyrealItemType"/> marks pyreals.</summary>
    public int ItemType { get; }
    public ItemVisual Visual { get; }
}

/// <summary>The Decal-facing reads the snapshot needs. The client implements it over WorldFilter and CharacterFilter on the game thread.</summary>
public interface IInventoryReader
{
    uint CharacterId { get; }
    /// <summary>Everything the character holds: the packs, the items in them and the wielded items.</summary>
    IReadOnlyList<InventoryObjectRead> Objects { get; }
    /// <summary>The main pack's slot count and icon, which are the character's own.</summary>
    int MainPackCapacity { get; }
    uint MainPackIcon { get; }
    /// <summary>How many side-pack slots the character has (the client's PackSlots value).</summary>
    int SidePackCapacity { get; }
    int Burden { get; }
    /// <summary>The character's strength, with the same effective value the client reports.</summary>
    int Strength { get; }
    /// <summary>How many "Might of the Seventh Mule" augmentations the character has, which raise the burden limit.</summary>
    int CarryingAugmentations { get; }
    uint OpenContainer { get; }
    uint Selected { get; }
}

/// <summary>Builds an <see cref="InventorySnapshot"/> from the reads. It has no Decal dependency, so the rules are tested here.</summary>
public static class InventorySnapshotBuilder
{
    /// <summary>The Decal item type of a pyreal stack (UtilityBelt's value for pyreals).</summary>
    public const int PyrealItemType = 273;

    // The AC EquipMask bits (ACE.Entity.Enum.EquipMask). Shirt and Pants take the underplating bits (ACE names the chest and arm
    // wear "Upper Body" and the abdomen and leg wear "Lower Body"); armour takes the armour bits.
    private static readonly (PaperdollSlot Slot, uint Mask)[] SlotMasks =
    {
        (PaperdollSlot.Neck, 0x00008000u),           // NeckWear
        (PaperdollSlot.Trinket, 0x04000000u),        // TrinketOne
        (PaperdollSlot.RightWrist, 0x00020000u),     // WristWearRight
        (PaperdollSlot.RightRing, 0x00080000u),      // FingerWearRight
        (PaperdollSlot.Shield, 0x00200000u),         // Shield; a dual-wielded off-hand weapon also carries this location
        (PaperdollSlot.Head, 0x00000001u),           // HeadWear
        (PaperdollSlot.UpperArms, 0x00000800u),      // UpperArmArmor
        (PaperdollSlot.Chest, 0x00000200u),          // ChestArmor
        (PaperdollSlot.LowerArms, 0x00001000u),      // LowerArmArmor
        (PaperdollSlot.Abdomen, 0x00000400u),        // AbdomenArmor
        (PaperdollSlot.UpperLegs, 0x00002000u),      // UpperLegArmor
        (PaperdollSlot.Hands, 0x00000020u),          // HandWear
        (PaperdollSlot.LowerLegs, 0x00004000u),      // LowerLegArmor
        (PaperdollSlot.Feet, 0x00000100u),           // FootWear
        (PaperdollSlot.AetheriaOne, 0x10000000u),    // SigilOne
        (PaperdollSlot.AetheriaTwo, 0x20000000u),    // SigilTwo
        (PaperdollSlot.AetheriaThree, 0x40000000u),  // SigilThree
        (PaperdollSlot.LeftWrist, 0x00010000u),      // WristWearLeft
        (PaperdollSlot.LeftRing, 0x00040000u),       // FingerWearLeft
        (PaperdollSlot.Weapon, 0x00100000u | 0x00400000u | 0x01000000u | 0x02000000u), // MeleeWeapon, MissileWeapon, Held, TwoHanded
        (PaperdollSlot.Ammo, 0x00800000u),           // MissileAmmo
        (PaperdollSlot.Cloak, 0x08000000u),          // Cloak
        (PaperdollSlot.Shirt, 0x00000002u | 0x00000008u | 0x00000010u), // ChestWear, UpperArmWear, LowerArmWear
        (PaperdollSlot.Pants, 0x00000004u | 0x00000040u | 0x00000080u), // AbdomenWear, UpperLegWear, LowerLegWear
    };

    // ACE's EncumbranceSystem caps the augmentation bonus at 150 burden per strength point.
    private const int MaxAugmentationBurden = 150;

    /// <summary>The paperdoll slots an equip mask covers, in paperdoll order. A multi-slot mask covers every slot it touches.</summary>
    public static IReadOnlyList<PaperdollSlot> SlotsFor(uint equipMask) =>
        SlotMasks.Where(pair => (equipMask & pair.Mask) != 0).Select(pair => pair.Slot).ToArray();

    /// <summary>The burden limit the server enforces for a strength and augmentation count (ACE's EncumbranceCapacity).</summary>
    public static int BurdenLimit(int strength, int carryingAugmentations)
    {
        if (strength <= 0) return 0;
        var bonus = Math.Min(30 * Math.Max(carryingAugmentations, 0), MaxAugmentationBurden);
        return 150 * strength + strength * bonus;
    }

    public static InventorySnapshot Build(IInventoryReader reader)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        var characterId = reader.CharacterId;
        var objects = reader.Objects;

        var mainPack = new InventoryPack(characterId, "Main Pack", reader.MainPackIcon, reader.MainPackCapacity, 0);
        var sidePacks = SidePacks(objects, characterId, reader.SidePackCapacity);

        var items = new List<InventoryItem>();
        var wielded = new List<WieldedItem>();
        var pyreals = 0;
        foreach (var read in objects)
        {
            if (read.EquippedMask != 0 && read.Container == characterId)
            {
                wielded.Add(new WieldedItem(read.Id, read.Visual, read.StackCount, SlotsFor(read.EquippedMask)));
                continue;
            }
            if (read.IsContainer || read.Slot < 0) continue;
            items.Add(new InventoryItem(read.Id, read.Container, read.Slot, read.Visual, read.StackCount));
            if (read.ItemType == PyrealItemType) pyreals += read.StackCount;
        }

        return new InventorySnapshot(mainPack, sidePacks, items, wielded, reader.Burden,
            BurdenLimit(reader.Strength, reader.CarryingAugmentations), pyreals, reader.OpenContainer, reader.Selected);
    }

    /// <summary>The side packs by slot order, with an empty placeholder for each free slot up to the capacity.</summary>
    private static IReadOnlyList<InventoryPack> SidePacks(IReadOnlyList<InventoryObjectRead> objects, uint characterId, int capacity)
    {
        var packs = objects.Where(read => read.IsContainer && read.Container == characterId && read.Slot >= 0).ToArray();
        var count = Math.Max(capacity, packs.Length == 0 ? 0 : packs.Max(pack => pack.Slot) + 1);
        var result = new InventoryPack[count];
        for (var index = 0; index < count; index++)
        {
            var pack = packs.FirstOrDefault(candidate => candidate.Slot == index);
            result[index] = pack == null
                ? new InventoryPack(0, string.Empty, 0, 0, index + 1)
                : new InventoryPack(pack.Id, pack.Name, pack.Visual.Icon, pack.ItemSlots, index + 1);
        }
        return result;
    }
}
