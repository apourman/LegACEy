using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's own wield request, CM_Inventory::Event_GetAndWieldItem (game action 0x001A), as retail's paperdoll sends it on a drop. Decal's
/// AutoWield goes through CPlayerSystem::AutoWield instead, which sends nothing for a second ring while the other finger is worn. Called by
/// address, so it is checked against the running client once and only used when it matches.
/// </summary>
internal static class NativeWieldSender
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>CM_Inventory::Event_GetAndWieldItem (uint itemId, uint location), cdecl, returns bool; PDB 0x006AB9F0 +0xF60 as its neighbours.</summary>
    private const uint GetAndWieldItem = 0x006AC950;

    // The prologue is left out: Decal patches prologues in the running client. The body after the first call and the opcode store tie
    // the address to the sender.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("CM_Inventory::Event_GetAndWieldItem body (game action 0x001A)", GetAndWieldItem + 0x0B,
            NativeUiCatalogue.Bytes("89 44 24 14 6A 00 8D 44 24 10 50"), Source, "cdecl (uint itemId, uint location); PDB +0xF60"),
        new NativeUiEntry("CM_Inventory::Event_GetAndWieldItem opcode store (0x001A)", GetAndWieldItem + 0x5A,
            NativeUiCatalogue.Bytes("C7 02 1A 00 00 00"), Source, "instruction reference (mov dword ptr [edx], 0x1A)"),
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool GetAndWieldItemFn(uint itemId, uint location);

    // Null until checked, and after a check the running client failed.
    private static GetAndWieldItemFn? _send;
    private static bool _checked;

    /// <summary>
    /// Sends the wield request. False when the running client does not match or retail refused to send it, so the caller falls back to
    /// Decal's AutoWield.
    /// </summary>
    public static bool TrySend(uint itemId, uint location, Action<string> log)
    {
        if (!_checked)
        {
            if (!NativeUi.Ready) return false;
            _checked = true;
            try
            {
                if (NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, "Native wield sender is off; drops on the paperdoll use Decal's AutoWield."))
                    _send = Marshal.GetDelegateForFunctionPointer<GetAndWieldItemFn>(new IntPtr(GetAndWieldItem));
            }
            catch (Exception exception) { log($"Native wield sender is off: its address could not be checked: {exception.Message}"); }
        }
        return _send?.Invoke(itemId, location) == true;
    }
}
