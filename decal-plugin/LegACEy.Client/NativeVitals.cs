using System;
using System.Collections.Generic;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The player's vitals as retail's own vitals window reads them: CACQualities::InqAttribute2nd(type, out value, raw) on the player's
/// qualities. Not raw, maximums carry enchantments and vitae; raw, they are the base the character window compares against.
/// Checked against the running client once; Decal stands in when it does not match. Also a skill's level as retail's spell examination
/// reads it for a spell's range (<see cref="TryReadSkillLevel"/>), checked on its own.
/// </summary>
internal static unsafe class NativeVitals
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>CACQualities::InqAttribute2nd (uint type, uint* value, int raw) -> int, thiscall, callee pops 12.</summary>
    private const uint InqAttribute2nd = 0x00592D20;
    /// <summary>CACQualities::InqSkillLevel (uint skill, int* value) -> int, thiscall, callee pops 8: the skill's init level plus its raised ranks.</summary>
    private const uint InqSkillLevel = 0x00592B40;
    /// <summary>ACCWeenieObject's CACQualities*, as every ClientObjMaintSystem quality update reads it.</summary>
    private const int WeenieQualities = 0x14C;

    private static readonly NativeUiEntry QualitiesRead = new("ClientObjMaintSystem's quality update reads the weenie's CACQualities at +0x14C",
        0x00558B05, NativeUiCatalogue.Bytes("8B 8E 4C 01 00 00"), Source, "instruction reference");

    // The prologue is left out: Decal patches prologues in the running client.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("CACQualities::InqAttribute2nd body: maximums 1, 3 and 5 take the attribute path", InqAttribute2nd + 4,
            NativeUiCatalogue.Bytes("8B 7C 24 14 33 DB 83 FF 01 8B E9 89 5C 24 0C 74 0A 83 FF 03 74 05 83 FF 05 75 21"), Source,
            "thiscall (CACQualities*, uint type, uint* value, int raw) -> int"),
        new NativeUiEntry("gmVitalsUI::Update reads health (2) then its maximum (1) through InqAttribute2nd, raw 0", 0x004C0B52,
            NativeUiCatalogue.Bytes("57 8D 4C 24 1C 51 6A 02 8B CD 89 7C 24 24 89 7C 24 1C E8 B7 21 0D 00 57 8D 54 24 14 52 6A 01 8B CD E8 A8 21 0D 00"),
            Source, "call reference"),
        QualitiesRead,
        RetailHudCatalogue.WeenieLookup,
        RetailHudCatalogue.PlayerIdRead,
    };

    private static readonly IReadOnlyList<NativeUiEntry> SkillEntries = new[]
    {
        new NativeUiEntry("CACQualities::InqSkillLevel body: the skill's +0xC and +0x10 added", InqSkillLevel + 3,
            NativeUiCatalogue.Bytes("85 C9 74 0E 8D 44 24 04 50 E8 2F FE FF FF 85 C0 75 05 33 C0 C2 08 00 8B 50 0C 8B 48 10 03 CA"), Source,
            "thiscall (CACQualities*, uint skill, int* value) -> int"),
        new NativeUiEntry("SpellExamineUI::DetermineSpellRange reads Creature Enchantment (0x1F) through InqSkillLevel", 0x004AD855,
            NativeUiCatalogue.Bytes("6A 1F 8B CE 89 7C 24 24 E8 DE 52 0E 00"), Source, "call reference"),
        QualitiesRead,
        RetailHudCatalogue.WeenieLookup,
        RetailHudCatalogue.PlayerIdRead,
    };

    // Null until checked.
    private static bool? _ok, _skillsOk;

    /// <summary>
    /// One vital by ACE's numbering (PropertyAttribute2nd): 1 max health, 2 health, 3 max stamina, 4 stamina, 5 max mana, 6 mana. Raw leaves
    /// out enchantments. False when the running client does not match or the player is not in the world yet, so the caller asks Decal.
    /// </summary>
    public static bool TryRead(uint type, bool raw, Action<string> log, out int value)
    {
        value = 0;
        if (!Checked(ref _ok, Entries, "Native vitals are off; vitals come from Decal.", log)) return false;
        var qualities = PlayerQualities();
        if (qualities == null) return false;
        uint read;
        if (((delegate* unmanaged[Thiscall]<byte*, uint, uint*, int, int>)InqAttribute2nd)(qualities, type, &read, raw ? 1 : 0) == 0) return false;
        value = (int)read;
        return true;
    }

    /// <summary>
    /// A skill's level by ACE's Skill numbering (War Magic 34), as SpellExamineUI::DetermineSpellRange reads it: the init level plus raised
    /// ranks, without attributes or enchantments. False when the running client does not match or the player is not in the world yet.
    /// </summary>
    public static bool TryReadSkillLevel(uint skill, Action<string> log, out int value)
    {
        value = 0;
        if (!Checked(ref _skillsOk, SkillEntries, "Spell ranges are left out of the spell details.", log)) return false;
        var qualities = PlayerQualities();
        if (qualities == null) return false;
        int read;
        if (((delegate* unmanaged[Thiscall]<byte*, uint, int*, int>)InqSkillLevel)(qualities, skill, &read) == 0) return false;
        value = read;
        return true;
    }

    private static bool Checked(ref bool? ok, IReadOnlyList<NativeUiEntry> entries, string off, Action<string> log)
    {
        if (ok == null)
        {
            if (!NativeUi.Ready) return false;
            try { ok = NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, entries, off); }
            catch (Exception exception)
            {
                log($"{off} Their addresses could not be checked: {exception.Message}");
                ok = false;
            }
        }
        return ok == true;
    }

    private static byte* PlayerQualities()
    {
        var weenie = ((delegate* unmanaged[Cdecl]<uint, byte*>)RetailHudCatalogue.GetWeenieObject)(RetailHudCatalogue.PlayerId());
        return weenie == null ? null : *(byte**)(weenie + WeenieQualities);
    }
}
