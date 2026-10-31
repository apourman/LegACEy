using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's own object selection. The selected id is read from the global ACCWeenieObject::SetSelectedObject keeps it in, and selecting
/// is SetSelectedObject as retail's windows call it on a click: it keeps the selected id, lights the object, and tells the UI and plugins
/// the selection changed. Called by address, so both are checked against the running client once at start; when it does not match, nothing
/// can be selected and the selection reads as none.
/// </summary>
internal static class NativeSelection
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>ACCWeenieObject::SetSelectedObject (uint id, int force), cdecl; PDB 0x0058C2E0 +0xE30 as SetSelectedObjectID beside it.</summary>
    private const uint SetSelectedObject = 0x0058D110;
    /// <summary>The selected object's id (uint32), stored by SetSelectedObject; RynthCore's SelectedTargetHooks reads it too.</summary>
    private const uint SelectedId = 0x00871E54;
    /// <summary>SetSelectedObject's second argument: select (and tell the UI) even when the object is already selected.</summary>
    private const int Force = 1;

    // The prologue is left out: Decal patches prologues in the running client. The stores to the selected and previous ids
    // (0x00871E54 / 0x00871E58) and the object's selected flag tie the address to the function.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("ACCWeenieObject::SetSelectedObject stores the selected id", SetSelectedObject + 0x33,
            NativeUiCatalogue.Bytes("89 35 54 1E 87 00"), Source, "cdecl (uint id, int force); PDB +0xE30; the selected id global is 0x00871E54"),
        new NativeUiEntry("ACCWeenieObject::SetSelectedObject marks the object selected", SetSelectedObject + 0x48,
            NativeUiCatalogue.Bytes("C7 40 6C 01 00 00 00"), Source, "instruction reference (mov dword ptr [eax+0x6C], 1)"),
        new NativeUiEntry("ACCWeenieObject::SetSelectedObject keeps the previous id", SetSelectedObject + 0x55,
            NativeUiCatalogue.Bytes("89 3D 58 1E 87 00"), Source, "instruction reference (mov [0x00871E58], edi)"),
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSelectedObjectFn(uint id, int force);

    // Null until checked, and after a check the running client failed.
    private static SetSelectedObjectFn? _select;

    /// <summary>Checks the running client once, after <see cref="NativeUi.Initialize"/>. A mismatch is logged once and leaves selection off.</summary>
    public static void Initialize(Action<string> log)
    {
        if (!NativeUi.Ready) return;
        try
        {
            if (NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, "Native selection is off; LegACEy windows cannot select, and read no selection."))
                _select = Marshal.GetDelegateForFunctionPointer<SetSelectedObjectFn>(new IntPtr(SetSelectedObject));
        }
        catch (Exception exception) { log($"Native selection is off: its address could not be checked: {exception.Message}"); }
    }

    /// <summary>The selected object's id, 0 for none (and when the running client does not match).</summary>
    public static uint Selected => _select == null ? 0 : unchecked((uint)Marshal.ReadInt32(new IntPtr(unchecked((int)SelectedId))));

    /// <summary>Selects the object as retail's windows do. Does nothing when the running client does not match.</summary>
    public static void Select(uint id) => _select?.Invoke(id, Force);
}
