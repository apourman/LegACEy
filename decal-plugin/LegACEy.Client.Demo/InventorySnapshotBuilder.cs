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

/// <summary>One object the character holds, as the Decal reads give it: packs, the items in them, and wielded items.</summary>
public sealed class InventoryObjectRead
{
    public InventoryObjectRead(uint id, string name, uint container, int slot, uint equippedMask, uint validLocations,
        int stackCount, int stackMax, int itemSlots, bool isContainer, int wcid, ItemVisual visual)
    {
        Id = id;
        Name = name ?? string.Empty;
        Container = container;
        Slot = slot;
        EquippedMask = equippedMask;
        ValidLocations = validLocations;
        StackCount = stackCount;
        StackMax = stackMax;
        ItemSlots = itemSlots;
        IsContainer = isContainer;
        Wcid = wcid;
        Visual = visual ?? throw new ArgumentNullException(nameof(visual));
    }

    public uint Id { get; }
    public string Name { get; }
    /// <summary>The object holding this one: the character for the main pack and wielded items, a pack for a pack's contents.</summary>
    public uint Container { get; }
    /// <summary>The slot index in the container.</summary>
    public int Slot { get; }
    /// <summary>The wield location (the AC EquipMask bits) of an equipped item; zero when it is not equipped.</summary>
    public uint EquippedMask { get; }
    public uint ValidLocations { get; }
    public int StackCount { get; }
    public int StackMax { get; }
    /// <summary>For a container, its number of slots; zero for anything else.</summary>
    public int ItemSlots { get; }
    public bool IsContainer { get; }
    /// <summary>The weenie class id (WCID) of the object: Decal's LongValueKey.Type.</summary>
    public int Wcid { get; }
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
    /// <summary>How many side-pack slots the character has.</summary>
    int SidePackCapacity { get; }
    int Burden { get; }
    int Strength { get; }
    /// <summary>The "Might of the Seventh Mule" augmentation count, which raises the burden limit.</summary>
    int CarryingAugmentations { get; }
    uint OpenContainer { get; }
    uint Selected { get; }
}

/// <summary>Builds an <see cref="InventorySnapshot"/> from the reads. It has no Decal dependency, so the rules are tested here.</summary>
public static class InventorySnapshotBuilder
{
    // The weenie class id of a pyreal stack (ACE's pyreal weenie: Player_Death and Player_House check it the same way).
    private const int PyrealWcid = 273;
    // The main pack's size when the client reports none. The server's value wins when it reports one.
    private const int DefaultMainPackCapacity = 96;
    private const int MaxAugmentationBurden = 150;

    // One row per slot: the AC EquipMask bits (ACE.Entity.Enum.EquipMask) that the slot covers.
    // Shirt and Pants take the underplating wear bits; armour takes the armour bits.
    private static readonly (PaperdollSlot Slot, uint Mask)[] SlotMasks =
    {
        (PaperdollSlot.Neck, 0x00008000u),           // NeckWear
        (PaperdollSlot.Trinket, 0x04000000u),        // TrinketOne
        (PaperdollSlot.RightWrist, 0x00020000u),     // WristWearRight
        (PaperdollSlot.RightRing, 0x00080000u),      // FingerWearRight
        (PaperdollSlot.Shield, 0x00200000u),         // Shield (a dual-wielded off-hand weapon also carries it)
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

    /// <summary>The EquipMask bits of one paperdoll slot.</summary>
    internal static uint MaskOf(PaperdollSlot slot) => SlotMasks.Single(pair => pair.Slot == slot).Mask;

    public static InventorySnapshot Build(IInventoryReader reader)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        var characterId = reader.CharacterId;
        var objects = reader.Objects;

        var mainCapacity = reader.MainPackCapacity > 0 ? reader.MainPackCapacity : DefaultMainPackCapacity;
        var mainPack = new InventoryPack(characterId, "Main Pack", reader.MainPackIcon, mainCapacity);
        var sidePacks = SidePacks(objects, characterId, reader.SidePackCapacity);

        var items = new List<InventoryItem>();
        var wielded = new List<WieldedItem>();
        var pyreals = 0;
        foreach (var read in objects)
        {
            if (read.EquippedMask != 0)
            {
                wielded.Add(new WieldedItem(read.Id, read.Name, read.Visual, read.StackCount, read.StackMax, read.Wcid,
                    read.ValidLocations, SlotsFor(read.EquippedMask)));
                continue;
            }
            if (read.IsContainer || read.Slot < 0) continue;
            items.Add(new InventoryItem(read.Id, read.Name, read.Container, read.Slot, read.Visual, read.StackCount, read.StackMax,
                read.Wcid, read.ValidLocations));
            if (read.Wcid == PyrealWcid) pyreals += read.StackCount;
        }

        return new InventorySnapshot(mainPack, sidePacks, items, wielded, reader.Burden,
            BurdenLimit(reader.Strength, reader.CarryingAugmentations), pyreals, reader.OpenContainer, reader.Selected);
    }

    /// <summary>The paperdoll slots an equip mask covers, in paperdoll order. A multi-slot mask covers every slot it touches.</summary>
    private static IReadOnlyList<PaperdollSlot> SlotsFor(uint equipMask) =>
        SlotMasks.Where(pair => (equipMask & pair.Mask) != 0).Select(pair => pair.Slot).ToArray();

    /// <summary>The burden limit the server enforces: 150 per strength point, plus a bonus per augmentation capped at 150.</summary>
    private static int BurdenLimit(int strength, int carryingAugmentations)
    {
        if (strength <= 0) return 0;
        var bonus = Math.Min(30 * Math.Max(carryingAugmentations, 0), MaxAugmentationBurden);
        return 150 * strength + strength * bonus;
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
                ? new InventoryPack(0, string.Empty, 0, 0)
                : new InventoryPack(pack.Id, pack.Name, pack.Visual.Icon, pack.ItemSlots);
        }
        return result;
    }
}
