using System;
using System.Collections.Generic;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The character's attributes, skills, burden and pack capacities as retail's own character window and inventory read them, from the
/// player's CACQualities and weenie (the same lookup as <see cref="NativeVitals"/>). Each group is checked against the running client once;
/// a group that does not match, or a player not in the world yet, reads as nothing (false), and the caller shows what it has without it.
/// </summary>
internal static unsafe class NativeCharacterStats
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>CACQualities::InqAttribute (uint attribute, uint* value, int raw) -> int, thiscall, callee pops 12: 1 Strength to 6 Self.</summary>
    private const uint InqAttribute = 0x00592700;
    /// <summary>CACQualities::InqSkill (uint skill, int* value, int raw) -> int, thiscall, callee pops 12: with attributes; raw 0 adds enchantments and vitae.</summary>
    private const uint InqSkill = 0x00593380;
    /// <summary>CBaseQualities::InqInt (uint property, int* value, int raw, int allowNegative) -> int, thiscall, callee pops 16.</summary>
    private const uint InqInt = 0x00590C20;
    /// <summary>The CBaseQualities inside CACQualities, which the property reads take as their receiver.</summary>
    private const int BaseQualities = 0x38;
    /// <summary>ACCWeenieObject's copies of the PublicWeenieDesc ItemsCapacity and ContainersCapacity, -1 for none.</summary>
    private const int ItemsCapacity = 0xC8, ContainersCapacity = 0xCC;

    /// <summary>ACE's PropertyInt numbers.</summary>
    public const uint EncumbranceVal = 5, HeritageGroup = 188, CarryingAugmentations = 230, Faction1Bits = 281;
    public const uint Strength = 1;

    // The prologues are left out: Decal patches prologues in the running client.
    private static readonly IReadOnlyList<NativeUiEntry> AttributeEntries = new[]
    {
        new NativeUiEntry("CACQualities::InqAttribute body: the attribute cache at +0x60, enchantments only for raw 0", InqAttribute + 3,
            NativeUiCatalogue.Bytes("8B 4B 60 85 C9 56 57 74 13 8B 74 24 14 8B 7C 24 10 56 57 E8 15 AD 03 00 85 C0 75 08 5F 5E 33 C0 5B C2 0C 00"),
            Source, "thiscall (CACQualities*, uint attribute, uint* value, int raw) -> int"),
        new NativeUiEntry("AttributeInfoRegion::Update reads an attribute raw 1, then raw 0, through InqAttribute", 0x004F2589,
            NativeUiCatalogue.Bytes("6A 01 8D 44 24 10 50 51 8B CF C7 44 24 18 00 00 00 00 E8 60 01 0A 00 8B 46 14 6A 00 8D 54 24 18 52 50 8B CF "
                + "C7 44 24 20 00 00 00 00 E8 46 01 0A 00"), Source, "call reference"),
        NativeVitals.QualitiesRead,
        RetailHudCatalogue.WeenieLookup,
        RetailHudCatalogue.PlayerIdRead,
    };

    private static readonly IReadOnlyList<NativeUiEntry> SkillEntries = new[]
    {
        new NativeUiEntry("CACQualities::InqSkill body: the base level, then the skill table at +0x64", InqSkill + 7,
            NativeUiCatalogue.Bytes("53 55 8B 6C 24 18 57 8B 7C 24 20 50 57 55 8B D9 E8 C4 FA FF FF 85 C0 75 09 5F 5D 5B 83 C4 0C C2 0C 00"),
            Source, "thiscall (CACQualities*, uint skill, int* value, int raw) -> int"),
        new NativeUiEntry("SkillInfoRegion::Update reads a skill raw 1, then raw 0, through InqSkill", 0x004F27A1,
            NativeUiCatalogue.Bytes("8B 56 14 6A 01 8D 4C 24 1C 51 52 8B CF 89 6C 24 24 E8 C9 0B 0A 00 8B 4E 14 55 8D 44 24 28 50 51 8B CF "
                + "89 6C 24 30 E8 B4 0B 0A 00"), Source, "call reference"),
        NativeVitals.QualitiesRead,
        RetailHudCatalogue.WeenieLookup,
        RetailHudCatalogue.PlayerIdRead,
    };

    private static readonly IReadOnlyList<NativeUiEntry> IntEntries = new[]
    {
        new NativeUiEntry("CBaseQualities::InqInt body: the int table, the value stored, the enchantment hook only for raw 0", InqInt + 3,
            NativeUiCatalogue.Bytes("8B 4E 08 85 C9 74 0E 8D 44 24 08 50 E8 2C 4B 04 00 85 C0 75 06 33 C0 5E C2 10 00"), Source,
            "thiscall (CBaseQualities*, uint property, int* value, int raw, int allowNegative) -> int"),
        new NativeUiEntry("gmCharacterInfoUI::UpdateLoad reads Strength (raw 0), burden (5) and the carrying augmentation (230)", 0x004B965D,
            NativeUiCatalogue.Bytes("6A 00 8D 4C 24 18 51 6A 01 8B CE C7 44 24 20 0A 00 00 00 E8 8B 90 0D 00 6A 00 6A 00 8D 54 24 14 52 83 C6 38 "
                + "6A 05 8B CE C7 44 24 1C 00 00 00 00 E8 8E 75 0D 00 6A 00 6A 00 8D 44 24 10 50 68 E6 00 00 00 8B CE C7 44 24 18 00 00 00 00 "
                + "E8 71 75 0D 00"), Source, "call reference"),
        NativeVitals.QualitiesRead,
        RetailHudCatalogue.WeenieLookup,
        RetailHudCatalogue.PlayerIdRead,
    };

    private static readonly IReadOnlyList<NativeUiEntry> CapacityEntries = new[]
    {
        new NativeUiEntry("ACCWeenieObject::IsContainer reads the capacities at +0xC8 (items) and +0xCC (containers)", 0x004A48AC,
            NativeUiCatalogue.Bytes("8B 81 C8 00 00 00 85 C0 75 0D 8B 81 CC 00 00 00 85 C0 75 03 33 C0 C3 B8 01 00 00 00 C3"), Source,
            "instruction reference"),
        RetailHudCatalogue.WeenieLookup,
        RetailHudCatalogue.PlayerIdRead,
    };

    // Null until checked.
    private static bool? _attributesOk, _skillsOk, _intsOk, _capacitiesOk;

    /// <summary>
    /// A primary attribute by ACE's numbering (1 Strength, 2 Endurance, 3 Quickness, 4 Coordination, 5 Focus, 6 Self), as retail's character
    /// window shows it: raw leaves out enchantments. False when the running client does not match or the player is not in the world yet.
    /// </summary>
    public static bool TryReadAttribute(uint attribute, bool raw, Action<string> log, out int value)
    {
        value = 0;
        if (!NativeVitals.Checked(ref _attributesOk, AttributeEntries, "Native attributes are off; the character window shows the server's.", log)) return false;
        var qualities = NativeVitals.PlayerQualities();
        if (qualities == null) return false;
        uint read;
        if (((delegate* unmanaged[Thiscall]<byte*, uint, uint*, int, int>)InqAttribute)(qualities, attribute, &read, raw ? 1 : 0) == 0) return false;
        value = (int)read;
        return true;
    }

    /// <summary>
    /// A skill by ACE's numbering (War Magic 34), as retail's character window shows it: raw is the level without enchantments, otherwise
    /// with them and with vitae. False when the running client does not match or the player is not in the world yet.
    /// </summary>
    public static bool TryReadSkill(uint skill, bool raw, Action<string> log, out int value)
    {
        value = 0;
        if (!NativeVitals.Checked(ref _skillsOk, SkillEntries, "Native skills are off; the character window shows the server's.", log)) return false;
        var qualities = NativeVitals.PlayerQualities();
        if (qualities == null) return false;
        int read;
        if (((delegate* unmanaged[Thiscall]<byte*, uint, int*, int, int>)InqSkill)(qualities, skill, &read, raw ? 1 : 0) == 0) return false;
        value = read;
        return true;
    }

    /// <summary>
    /// One of the player's PropertyInts, such as the burden, as retail's inventory reads it. False when the running client does not match, the
    /// player is not in the world yet, or the player has no such property.
    /// </summary>
    public static bool TryReadInt(uint property, Action<string> log, out int value)
    {
        value = 0;
        if (!NativeVitals.Checked(ref _intsOk, IntEntries, "Native character values are off; burden, the carrying augmentation and society are not read.", log)) return false;
        var qualities = NativeVitals.PlayerQualities();
        if (qualities == null) return false;
        int read;
        if (((delegate* unmanaged[Thiscall]<byte*, uint, int*, int, int, int>)InqInt)(qualities + BaseQualities, property, &read, 0, 0) == 0) return false;
        value = read;
        return true;
    }

    /// <summary>
    /// The player's main-pack and side-pack slot counts (ACE's ItemsCapacity and ContainersCapacity), as retail's pack check reads them from
    /// the player's weenie. False when the running client does not match or the player is not in the world yet.
    /// </summary>
    public static bool TryReadPackCapacities(Action<string> log, out int main, out int side)
    {
        main = side = 0;
        if (!NativeVitals.Checked(ref _capacitiesOk, CapacityEntries, "Native pack capacities are off; the inventory uses its default.", log)) return false;
        var weenie = NativeVitals.PlayerWeenie();
        if (weenie == null) return false;
        main = Math.Max(0, *(int*)(weenie + ItemsCapacity));
        side = Math.Max(0, *(int*)(weenie + ContainersCapacity));
        return true;
    }
}
