using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's own examination of an object, as E on the selection makes it: <c>CM_Examine::SendNotice_ExamineObject</c>, which
/// <c>ClientUISystem::ExamineObject</c> (0x005657B0) calls for a nonzero id. Retail's examination panel takes the notice
/// (<c>gmExaminationUI::RecvNotice_ExamineObject</c>, 0x004ABB30), raises the busy cursor, sends the appraisal and shows the panel when it
/// is answered. Used to hand the appraisal window over to retail when its switch turns to retail. Call on the game thread.
/// </summary>
internal sealed class RetailExamine
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xED0 here) for names; our objdump read of installed acclient.exe";
    private const uint SendNoticeAddress = 0x006B0510;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("CM_Examine::SendNotice_ExamineObject", SendNoticeAddress,
            NativeUiCatalogue.Bytes("E8 FB A2 DC FF 8B 10 68 ED D1 4D 00 8B C8 FF 52 10 85 C0 74 33"), Source, "Cdecl (objectId) -> bool"),
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendNoticeFn(uint objectId);

    private readonly SendNoticeFn? _send;

    public RetailExamine(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
        {
            log("Switching the appraisal window to retail won't reopen the item in retail's: the running client does not match its native entry.");
            return;
        }
        _send = Marshal.GetDelegateForFunctionPointer<SendNoticeFn>(new IntPtr(SendNoticeAddress));
    }

    /// <summary>Asks retail's examination panel to examine the object. False when the entry did not validate.</summary>
    public bool Examine(uint objectId)
    {
        if (_send == null || objectId == 0) return false;
        _send(objectId);
        return true;
    }
}
