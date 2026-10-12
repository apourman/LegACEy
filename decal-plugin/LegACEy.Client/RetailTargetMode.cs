using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's "choose a target" mode: using a gem, a kit or a mana stone leaves <c>ClientUISystem</c>'s target mode (+0x2C) set, and the next
/// object the player clicks becomes the target. Retail's item lists finish it on a click: select the item, then, while the mode is set,
/// <c>ExecuteTargetModeForItem(item, mode)</c> and <c>SetTargetMode(0)</c> (gmToolbarUI at 0x004BE037). A click in a LegACEy window never
/// reaches those lists, so <see cref="TryTarget"/> does the same for it. Call on the game thread.
/// </summary>
internal sealed class RetailTargetMode
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xDA0 in this region) for names; our objdump read of installed acclient.exe";
    private const uint ExecuteTargetModeForItemAddress = 0x005658D0, SetTargetModeAddress = 0x005656E0;
    private static readonly IntPtr UiSystemInstance = new(0x00871354);
    private const int TargetModeOffset = 0x2C;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("ClientUISystem::GetUISystem", 0x00564D30, NativeUiCatalogue.Bytes("A1 54 13 87 00 C3"), Source,
            "Cdecl () -> ClientUISystem*, read from 0x00871354"),
        new NativeUiEntry("ClientUISystem::SetTargetMode", SetTargetModeAddress, NativeUiCatalogue.Bytes("8B 44 24 04 56 8B F1 39 46 2C 74 5D 33 D2 3B C2 89 46 2C"),
            Source, "ThisCall (Target_Mode mode): stores the mode at +0x2C and updates the cursor"),
        new NativeUiEntry("ClientUISystem::ExecuteTargetModeForItem", ExecuteTargetModeForItemAddress, NativeUiCatalogue.Bytes(
            "8B 44 24 08 48 74 23 48 74 13 48 75 2E 8B 44 24 04 50 E8 39 44 02 00"), Source,
            "ThisCall (uint objectId, Target_Mode mode); mode 3 calls ItemHolder::TargetAcquired (0x00589D20), which uses the held item on the object"),
        new NativeUiEntry("ItemHolder::TargetAcquired reads the held item", 0x00589D20, NativeUiCatalogue.Bytes("55 8B 6C 24 08 56 8B 35 E4 1A 87 00"), Source,
            "Cdecl (uint target); the held source item is at 0x00871AE4"),
        new NativeUiEntry("gmToolbarUI item click finishes the target mode", 0x004BE037, NativeUiCatalogue.Bytes(
            "E8 F4 6C 0A 00 8B 48 2C 85 C9 74 32 E8 E8 6C 0A 00 8B B6 FC 05 00 00 85 F6 8B 40 2C 74 0E 50 56 E8 D4 6C 0A 00 8B C8"), Source,
            "the retail pattern followed here: mode at +0x2C, ExecuteTargetModeForItem(item, mode), then SetTargetMode(0)")
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ExecuteFn(IntPtr uiSystem, uint objectId, int mode);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetModeFn(IntPtr uiSystem, int mode);

    private readonly ExecuteFn? _execute;
    private readonly SetModeFn? _setMode;

    public RetailTargetMode(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
        {
            log("Using an item on a target in LegACEy windows is off: the running client does not match its native entries.");
            return;
        }
        _execute = Marshal.GetDelegateForFunctionPointer<ExecuteFn>(new IntPtr(ExecuteTargetModeForItemAddress));
        _setMode = Marshal.GetDelegateForFunctionPointer<SetModeFn>(new IntPtr(SetTargetModeAddress));
    }

    /// <summary>When retail is waiting for a target, makes the object that target, as retail's item lists do; false when it is not waiting.</summary>
    public bool TryTarget(uint objectId)
    {
        if (_execute == null || _setMode == null || objectId == 0) return false;
        var ui = Marshal.ReadIntPtr(UiSystemInstance);
        if (ui == IntPtr.Zero) return false;
        var mode = Marshal.ReadInt32(ui, TargetModeOffset);
        if (mode == 0) return false;
        _execute(ui, objectId, mode);
        _setMode(ui, 0);
        return true;
    }
}
