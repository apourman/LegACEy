using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's own appraisal request, CM_Item::Event_Appraise (game action 0x00C8), as retail's examination panel sends it. Decal's ID queue
/// sends one request at a time, so ours could wait half a second behind it while retail's panel follows a click at once (Backlog 112).
/// Called by address, so it is checked against the running client once and only used when it matches.
/// </summary>
internal static class NativeAppraisalSender
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>CM_Item::Event_Appraise (uint objectId), cdecl, returns bool; the examination request calls it at 0x004ABB53.</summary>
    private const uint Appraise = 0x006A94A0;

    // The prologue is left out: Decal patches prologues in the running client. The body after the first call and the opcode store tie
    // the address to the sender.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("CM_Item::Event_Appraise body (game action 0x00C8)", Appraise + 0x0B,
            NativeUiCatalogue.Bytes("89 44 24 14 6A 00 8D 44 24 10 50"), Source, "cdecl (uint objectId)"),
        new NativeUiEntry("CM_Item::Event_Appraise opcode store (0x00C8)", Appraise + 0x5A,
            NativeUiCatalogue.Bytes("C7 02 C8 00 00 00"), Source, "instruction reference (mov dword ptr [edx], 0xC8)"),
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool AppraiseFn(uint objectId);

    // Null until checked, and after a check the running client failed.
    private static AppraiseFn? _send;
    private static bool _checked;

    /// <summary>Sends the appraisal request. False when the running client does not match or retail refused to send it, so the caller falls
    /// back to Decal's ID queue.</summary>
    public static bool TrySend(uint objectId, Action<string> log)
    {
        if (!_checked)
        {
            if (!NativeUi.Ready) return false;
            _checked = true;
            try
            {
                if (NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, "Native appraisal sender is off; appraisals go through Decal's ID queue."))
                    _send = Marshal.GetDelegateForFunctionPointer<AppraiseFn>(new IntPtr(Appraise));
            }
            catch (Exception exception) { log($"Native appraisal sender is off: its address could not be checked: {exception.Message}"); }
        }
        return _send?.Invoke(objectId) == true;
    }
}
