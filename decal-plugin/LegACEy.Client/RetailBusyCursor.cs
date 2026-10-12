using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's busy cursor while LegACEy waits for an appraisal. Retail's examination UI raises <c>ClientUISystem</c>'s busy count (+0x14) when
/// it sends an appraisal (0x004ABB4A) and lowers it when the answer comes (0x004B754C); our requests go through Decal, which does neither.
/// <see cref="Show"/> holds one count of our own, so the two always balance. Call on the game thread.
/// </summary>
internal sealed class RetailBusyCursor
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xDA0 in this region) for names; our objdump read of installed acclient.exe";
    private const uint IncrementAddress = 0x00565610, DecrementAddress = 0x00565630;
    private static readonly IntPtr UiSystemInstance = new(0x00871354);

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("ClientUISystem::IncrementBusyCount", IncrementAddress, NativeUiCatalogue.Bytes("8B 51 14 42 8B C2 83 F8 01 89 51 14 75 05 E9 AD FD FF FF C3"),
            Source, "ThisCall (): +0x14 up; the first count updates the cursor"),
        new NativeUiEntry("ClientUISystem::DecrementBusyCount", DecrementAddress, NativeUiCatalogue.Bytes("FF 49 14 75 05 E9 96 FD FF FF C3"),
            Source, "ThisCall (): +0x14 down; the last count updates the cursor"),
        // Ends before the Event_Appraise call (E8 48 D9 1F 00 on disk), which Decal patches in the running client.
        new NativeUiEntry("an examination request raises the busy count before Event_Appraise", 0x004ABB43,
            NativeUiCatalogue.Bytes("E8 E8 91 0B 00 8B C8 E8 C1 9A 0B 00 57 89 7E 1C"), Source, "the retail pattern followed here")
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void CountFn(IntPtr uiSystem);

    private readonly CountFn? _increment;
    private readonly CountFn? _decrement;
    // The ClientUISystem our count was added to, or zero when we hold none.
    private IntPtr _held;

    public RetailBusyCursor(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
        {
            log("The busy cursor while an appraisal loads is off: the running client does not match its native entries.");
            return;
        }
        _increment = Marshal.GetDelegateForFunctionPointer<CountFn>(new IntPtr(IncrementAddress));
        _decrement = Marshal.GetDelegateForFunctionPointer<CountFn>(new IntPtr(DecrementAddress));
    }

    /// <summary>Holds our busy count while <paramref name="busy"/> is true, and gives it back when it turns false. Call each frame.</summary>
    public void Show(bool busy)
    {
        if (_increment == null || _decrement == null) return;
        if (busy && _held == IntPtr.Zero)
        {
            var ui = Marshal.ReadIntPtr(UiSystemInstance);
            if (ui == IntPtr.Zero) return;
            _increment(ui);
            _held = ui;
        }
        else if (!busy && _held != IntPtr.Zero)
        {
            // Given back to the same ClientUISystem it was taken from; one replaced since (a relog) never had it.
            if (Marshal.ReadIntPtr(UiSystemInstance) == _held) _decrement(_held);
            _held = IntPtr.Zero;
        }
    }
}
