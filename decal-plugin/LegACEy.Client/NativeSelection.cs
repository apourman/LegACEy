using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's own object selection, ACCWeenieObject::SetSelectedObject, as retail's windows call it on a click: it keeps the selected id,
/// lights the object, and tells the UI and plugins the selection changed. Called by address, so it is checked against the running client
/// once and only used when it matches; otherwise the caller falls back to Decal's SelectItem.
/// </summary>
internal static class NativeSelection
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>ACCWeenieObject::SetSelectedObject (uint id, int force), cdecl; PDB 0x0058C2E0 +0xE30 as SetSelectedObjectID beside it.</summary>
    private const uint SetSelectedObject = 0x0058D110;
    /// <summary>SetSelectedObject's second argument: select (and tell the UI) even when the object is already selected.</summary>
    private const int Force = 1;

    // The prologue is left out: Decal patches prologues in the running client. The stores to the selected and previous ids
    // (0x00871E54 / 0x00871E58) and the object's selected flag tie the address to the function.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("ACCWeenieObject::SetSelectedObject stores the selected id", SetSelectedObject + 0x33,
            NativeUiCatalogue.Bytes("89 35 54 1E 87 00"), Source, "cdecl (uint id, int force); PDB +0xE30"),
        new NativeUiEntry("ACCWeenieObject::SetSelectedObject marks the object selected", SetSelectedObject + 0x48,
            NativeUiCatalogue.Bytes("C7 40 6C 01 00 00 00"), Source, "instruction reference (mov dword ptr [eax+0x6C], 1)"),
        new NativeUiEntry("ACCWeenieObject::SetSelectedObject keeps the previous id", SetSelectedObject + 0x55,
            NativeUiCatalogue.Bytes("89 3D 58 1E 87 00"), Source, "instruction reference (mov [0x00871E58], edi)"),
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSelectedObjectFn(uint id, int force);

    // Null until checked, and after a check the running client failed.
    private static SetSelectedObjectFn? _select;
    private static bool _checked;

    /// <summary>Selects the object as retail's windows do. False when the running client does not match, so the caller uses Decal.</summary>
    public static bool TrySelect(uint id, Action<string> log)
    {
        if (!_checked)
        {
            if (!NativeUi.Ready) return false;
            _checked = true;
            try
            {
                if (NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, "Native selection is off; LegACEy windows select through Decal."))
                    _select = Marshal.GetDelegateForFunctionPointer<SetSelectedObjectFn>(new IntPtr(SetSelectedObject));
            }
            catch (Exception exception) { log($"Native selection is off: its address could not be checked: {exception.Message}"); }
        }
        if (_select == null) return false;
        _select(id, Force);
        return true;
    }
}
