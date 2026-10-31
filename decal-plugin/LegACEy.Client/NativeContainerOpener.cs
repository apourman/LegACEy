using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's client-side pack switch, CM_UI::SendNotice_OpenContainedContainer (uint containerId), as CPlayerSystem::UsingItem sends it for a
/// carried pack (0x00563E07). gmInventoryUI hears it and opens the pack in its item lists, so a drag handed to retail from that pack finds
/// the item there. The server is not asked: it answers a use of a carried pack with only "use done". Called by address, so it is checked
/// against the running client once and only used when it matches.
/// </summary>
internal static class NativeContainerOpener
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; 0x00479DB0 +0x400 as SetPanelVisibility) for names; our objdump read of installed acclient.exe";
    private const uint SendNoticeOpenContainedContainer = 0x0047A1B0;

    // Past the first call (to the notice handler list): Decal patches call sites in the running client.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("CM_UI::SendNotice_OpenContainedContainer body", SendNoticeOpenContainedContainer + 5,
            NativeUiCatalogue.Bytes("8B 10 68 66 D2 4D 00 8B C8 FF 52 10 85 C0 74 33 56 8B 70 04"), Source, "cdecl (uint containerId) -> bool"),
        new NativeUiEntry("CM_UI::SendNotice_OpenContainedContainer calls RecvNotice_OpenContainedContainer (vtable +0x23C)",
            SendNoticeOpenContainedContainer + 0x3B, NativeUiCatalogue.Bytes("FF 92 3C 02 00 00"), Source,
            "virtual slot reference; gmInventoryUI's handler (0x004A6DF0) shows the panel, then ItemList_OpenContainer(id, 1)"),
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool SendFn(uint containerId);

    // Null until checked, and after a check the running client failed.
    private static SendFn? _send;
    private static bool _checked;

    /// <summary>
    /// Opens the pack in retail's inventory lists. Only while retail's panel is shown (parked, under the takeover): its handler shows the
    /// panel, which must not pop retail's inventory up on its own.
    /// </summary>
    public static void Open(uint containerId, Action<string> log)
    {
        if (!_checked)
        {
            if (!NativeUi.Ready) return;
            _checked = true;
            try
            {
                if (NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, "Native pack opener is off; retail's inventory lists keep their own pack."))
                    _send = Marshal.GetDelegateForFunctionPointer<SendFn>(new IntPtr(SendNoticeOpenContainedContainer));
            }
            catch (Exception exception) { log($"Native pack opener is off: its address could not be checked: {exception.Message}"); }
        }
        if (_send == null) return;
        var panel = NativeUi.GetElement(NativeUi.InventoryPanel);
        if (panel != IntPtr.Zero && NativeUi.IsVisible(panel)) _send(containerId);
    }
}
