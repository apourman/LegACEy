using System;
using System.Collections.Generic;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The native entries the Dereth attack bar needs from retail's combat system and player options. Each feature has its own list, validated
/// on its own, so a mismatch turns only that part off. Addresses and byte lists were read from the installed end-of-retail acclient.exe;
/// acclient.pdb is a near build, so its names were used and each address was checked in the exe. Recipes are in the attack bar spike note.
/// </summary>
/// <remarks>
/// The panel ids are not byte-checked (an id is not an address). They are the placed ids from the installed client_local_English.dat, and
/// at runtime the port finds the element with UIElementManager::GetElement before hiding it, as the vitals and radar do.
/// </remarks>
internal static class RetailAttackCatalogue
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb (near build)";

    /// <summary>Retail's combat panel (gmCombatPanelUI, type 0x10000054): 610 x 90 at (0, 400), placed in LayoutDesc 0x21000005 from 0x21000073.
    /// It holds the combat bar and the magic spell bar.</summary>
    internal const uint CombatPanel = 0x100006B5;
    /// <summary>gmCombatUI (type 0x1000000C): the height plaques, the Speed and Power labels and the power meter, 600 x 80 inside the panel
    /// at (5, 5), placed from LayoutDesc 0x21000007 as key 0x1000005C.</summary>
    internal const uint CombatBar = 0x1000005C;

    // ClientCombatSystem::SetRequestedAttackHeight (ThisCall, int): compares with [this+0x24] and stores the height there.
    internal const uint SetRequestedAttackHeight = 0x0056D640;
    // CM_Combat::SendNotice_AttackHeightChanged (Cdecl, int): the notice retail's Del, End and PgDn keys send.
    internal const uint SendAttackHeightChanged = 0x006AAE10;
    // CM_Combat::SendNotice_DesiredAttackPowerChanged (Cdecl, float): the notice that tells the combat UI the set point moved.
    internal const uint SendDesiredAttackPowerChanged = 0x006AAE60;
    // ClientCombatSystem::SetCombatMode (ThisCall, int mode, bool playerRequested): reads [this+0x1C] first.
    internal const uint SetCombatMode = 0x0056CB80;
    // ClientCombatSystem::SetPowerBarLevel (ThisCall, float): stores the level at [this+0x38] and sends the level notice with [this+0x34].
    internal const uint SetPowerBarLevel = 0x0056B5D0;
    // gmCombatUI's SetPowerbarLevel handler (mode and level): mode 1 goes to the power meter 0x10000050.
    internal const uint CombatBarSetPowerbarLevel = 0x004CCCD0;
    // gmCombatUI's power-bar click handler stores the set point at ClientCombatSystem+0x4C (mov [eax+0x4C], edx).
    internal const uint CombatBarClickStoresSetPoint = 0x004CD19B;
    // ClientCombatSystem::EndAttackRequest reads the set point (fld [esi+0x4C]) to compare with the charge.
    internal const uint EndAttackRequestReadsSetPoint = 0x0056CE64;
    // ClientCombatSystem's own auto-target check: CPlayerSystem + 0x34 is the receiver, then PlayerModule::AutoTarget is called.
    internal const uint PlayerModuleReceiverForOptions = 0x0056CB5E;
    // PlayerModule::AutoRepeatAttack and SetAutoRepeatAttack (CharacterOptions1 bit 0x2). Setters notify the UI and send the change.
    internal const uint AutoRepeatAttack = 0x005D3A90, SetAutoRepeatAttack = 0x005D3AA0;
    // PlayerModule::AutoTarget and SetAutoTarget (bit 0x2000).
    internal const uint AutoTarget = 0x005D3E90, SetAutoTarget = 0x005D3EA0;
    // PlayerModule::ViewCombatTarget and SetViewCombatTarget (bit 0x80).
    internal const uint ViewCombatTarget = 0x005D3CB0, SetViewCombatTarget = 0x005D3CC0;
    // PlayerModule::AdvancedCombatUI (bit 0x1000). SetCombatMode caches it at ClientCombatSystem+0x50 and picks the power-bar mode from it.
    internal const uint AdvancedCombatUI = 0x005D3E40;

    // The height, set point, charge, options and combat mode lists. Every list is checked on its own.
    internal static readonly IReadOnlyList<NativeUiEntry> Height = new[]
    {
        Entry("ClientCombatSystem::GetCombatSystem (shared with the toolbar)", RetailToolbarCatalogue.GetCombatSystem,
            "A1 6C 16 87 00 C3", "Cdecl () -> ClientCombatSystem*, read from 0x0087166C"),
        Entry("ClientCombatSystem::SetRequestedAttackHeight", SetRequestedAttackHeight, "8B 51 24 8B 44 24 04 3B D0 0F 95 C2",
            "ThisCall (int height); the height is at [this+0x24]"),
        Entry("CM_Combat::SendNotice_AttackHeightChanged", SendAttackHeightChanged,
            "E8 FB F9 DC FF 8B 10 68 FC D1 4D 00 8B C8 FF 52 10 85 C0 74 33 56 8B 70", "Cdecl (int height) -> bool; the Del, End and PgDn notice")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> SetPoint = new[]
    {
        Entry("CM_Combat::SendNotice_DesiredAttackPowerChanged", SendDesiredAttackPowerChanged,
            "E8 AB F9 DC FF 8B 10 68 49 D2 4D 00 8B C8 FF 52 10 85 C0 74 33 56 8B 70", "Cdecl (float set point) -> bool"),
        Entry("ClientCombatSystem's set point is read at +0x4C (EndAttackRequest, fld [esi+0x4C])", EndAttackRequestReadsSetPoint, "D9 46 4C",
            "instruction reference (fld dword ptr [esi+0x4C])"),
        Entry("gmCombatUI's power-bar click stores the set point at +0x4C (mov [eax+0x4C], edx)", CombatBarClickStoresSetPoint, "89 50 4C",
            "instruction reference (mov [eax+0x4C], edx)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Charge = new[]
    {
        Entry("ClientCombatSystem::SetPowerBarLevel stores the level at +0x38 and sends the notice with +0x34", SetPowerBarLevel,
            "8B 44 24 04 56 8B F1 8B 4E 34 50 51 E8 EF 80 13 00 8B 54 24 10 83 C4 08 89 56 38 5E", "ThisCall (float level)"),
        Entry("gmCombatUI::RecvNotice_SetPowerbarLevel: mode 1 goes to the power meter 0x10000050", CombatBarSetPowerbarLevel,
            "83 7C 24 04 01 75 23 68 50 00 00 10", "ThisCall (PowerBarMode, float); gmNoticeHandler at +0x5F8")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Options = new[]
    {
        Entry("PlayerModule::AutoRepeatAttack (CharacterOptions1 bit 0x2)", AutoRepeatAttack, "8B 81 8C 00 00 00 D1 E8 83 E0 01 C3",
            "ThisCall () -> bool"),
        Entry("PlayerModule::SetAutoRepeatAttack (sets bit 0x2 and notifies the change)", SetAutoRepeatAttack,
            "8B 91 8C 00 00 00 8A 44 24 04 D1 EA 80 E2 01 3A D0 74 25", "ThisCall (bool)"),
        Entry("PlayerModule::AutoTarget (CharacterOptions1 bit 0x2000)", AutoTarget, "8B 81 8C 00 00 00 C1 E8 0D 83 E0 01 C3",
            "ThisCall () -> bool"),
        Entry("PlayerModule::SetAutoTarget (sets bit 0x2000 and notifies the change)", SetAutoTarget,
            "8B 91 8C 00 00 00 8A 44 24 04 C1 EA 0D 80 E2 01 3A D0 74 29", "ThisCall (bool)"),
        Entry("PlayerModule::ViewCombatTarget (CharacterOptions1 bit 0x80)", ViewCombatTarget, "8B 81 8C 00 00 00 C1 E8 07 83 E0 01 C3",
            "ThisCall () -> bool"),
        Entry("PlayerModule::SetViewCombatTarget (sets bit 0x80 and notifies the change)", SetViewCombatTarget,
            "8B 91 8C 00 00 00 8A 44 24 04 C1 EA 07 80 E2 01 3A D0 74 29", "ThisCall (bool)"),
        Entry("PlayerModule::AdvancedCombatUI (CharacterOptions1 bit 0x1000)", AdvancedCombatUI, "8B 81 8C 00 00 00 C1 E8 0C 83 E0 01 C3",
            "ThisCall () -> bool"),
        Entry("PlayerModule receiver: CPlayerSystem+0x34 (lea ecx, [eax+0x34]) before the option reads", PlayerModuleReceiverForOptions,
            "8D 48 34 E8 2A 73 06 00", "instruction reference (PlayerModule at +0x34)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> CombatMode = new[]
    {
        Entry("ClientCombatSystem::SetCombatMode reads the stance at +0x1C (mov eax, [esi+0x1C])", SetCombatMode,
            "51 53 56 8B F1 8B 46 1C 57 8B 7C 24 14 3B F8 8B D8 0F 84 EA 01 00 00 8A 44 24 18", "ThisCall (int mode, bool playerRequested)")
    };

    /// <summary>Checks each feature's list on its own: a mismatch, or an address that cannot be read, turns that feature off and logs it.</summary>
    internal static RetailAttackNatives Validate(Func<uint, int, byte[]> read, Action<string> log) => new(
        Check("attack height", Height, read, log), Check("set point", SetPoint, read, log), Check("charge", Charge, read, log),
        Check("options", Options, read, log), Check("combat mode", CombatMode, read, log));

    // NativeUiCatalogue.Validate catches failed reads itself, so nothing here throws.
    private static bool Check(string feature, IEnumerable<NativeUiEntry> entries, Func<uint, int, byte[]> read, Action<string> log) =>
        NativeUiCatalogue.Validate(read, log, entries, $"Dereth attack bar {feature} is off: the running client does not match its native entries.");

    private static NativeUiEntry Entry(string name, uint address, string bytes, string convention) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, convention);
}

/// <summary>Which attack bar features the running client supports: the height and set point, the charge, the options and the combat mode.</summary>
internal sealed record RetailAttackNatives(bool Height, bool SetPoint, bool Charge, bool Options, bool CombatMode);
