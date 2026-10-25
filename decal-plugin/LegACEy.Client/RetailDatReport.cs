using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Sends the client's dat report (DDD_InterrogationResponse) to the server again, so a world switched on at character select is reported
/// and the server lets the character in. <c>CLCache::OnServerInterrogation</c> builds the report at send time, asking each dat controller
/// for its iteration list (<c>DiskController::LoadIterationList</c>, which reads the edition record <c>0xFFFF0001</c> through
/// <c>LoadDataEx</c>, where the overlay answers), then sends it. It's called again with an interrogation rebuilt from what the first one
/// left in the cache (region, name-rule language) and without the high-res flag, so the high-res dat isn't loaded twice. The DDD state it
/// resets is put back afterwards: the server doesn't end the exchange again. Called on the game thread.
/// </summary>
internal sealed class RetailDatReport
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xBA0 at 0x004Fxxxx) for names; our objdump read of installed acclient.exe";
    private const uint OnServerInterrogationAddress = 0x004FAF80;
    // The client's DBCache, a CLCache subclass (DBCache's constructor stores itself there), and that subclass's vtable.
    private const uint CacheGlobal = 0x00837BAC, CacheVtable = 0x007E4300;
    // CLCache: the DDD state (1 interrogated, 4 ended), two transfer counters, the region and the name-rule language.
    private const int State = 0x1F4, CounterA = 0x2F4, CounterB = 0x2F8, Region = 0x1F0, NameRuleLanguage = 0x2FC;
    // DDD_InterrogationMessage: vtable, opcode, region, name-rule language, product id (4 = high-res), the supported languages (a SmartArray).
    private const uint InterrogationVtable = 0x007C6850, InterrogationOpcode = 0xF7E5, Product = 1;
    private const int MessageSize = 0x30;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("CLCache::OnServerInterrogation", OnServerInterrogationAddress,
            NativeUiCatalogue.Bytes("83 EC 30 53 55 8B 6C 24 3C 56 8B F1 57 33 FF C7 86 F4 01 00 00 01 00 00 00"), Source,
            "ThisCall (CLCache*, const DDD_InterrogationMessage*), ret 4; reads message +8 region, +0xC name-rule language, +0x10 product; called from 0x004FCDA6"),
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void OnServerInterrogationFn(IntPtr cache, IntPtr message);

    private static readonly OnServerInterrogationFn OnServerInterrogation =
        Marshal.GetDelegateForFunctionPointer<OnServerInterrogationFn>(new IntPtr(OnServerInterrogationAddress));

    private readonly Action<string> _log;

    private RetailDatReport(Action<string> log) => _log = log;

    /// <summary>The re-send, when its entry matches the running client; null otherwise, and a first visit then needs a relaunch.</summary>
    public static RetailDatReport? Create(Func<uint, int, byte[]> readMemory, Action<string> log) =>
        NativeUiCatalogue.Validate(readMemory, log, Entries, "The dat report can't be sent again: the running client does not match its entry.")
            ? new RetailDatReport(log)
            : null;

    /// <summary>Sends the report again; false, and logged, when the client's cache isn't the one expected.</summary>
    public bool Resend()
    {
        try
        {
            var cache = Marshal.ReadIntPtr(new IntPtr(CacheGlobal));
            if (cache == IntPtr.Zero || (uint)Marshal.ReadInt32(cache) != CacheVtable)
            {
                _log("The dat report can't be sent again: the client's dat cache isn't the one expected.");
                return false;
            }
            int state = Marshal.ReadInt32(cache, State), a = Marshal.ReadInt32(cache, CounterA), b = Marshal.ReadInt32(cache, CounterB);
            var message = Marshal.AllocHGlobal(MessageSize);
            try
            {
                for (var i = 0; i < MessageSize; i += 4) Marshal.WriteInt32(message, i, 0);
                Marshal.WriteInt32(message, 0, unchecked((int)InterrogationVtable));
                Marshal.WriteInt32(message, 4, unchecked((int)InterrogationOpcode));
                Marshal.WriteInt32(message, 8, Marshal.ReadInt32(cache, Region));
                Marshal.WriteInt32(message, 0xC, Marshal.ReadInt32(cache, NameRuleLanguage));
                Marshal.WriteInt32(message, 0x10, unchecked((int)Product));
                OnServerInterrogation(cache, message);
            }
            finally { Marshal.FreeHGlobal(message); }
            Marshal.WriteInt32(cache, State, state);
            Marshal.WriteInt32(cache, CounterA, a);
            Marshal.WriteInt32(cache, CounterB, b);
            _log($"Sent the dat report again (DDD state {state} kept).");
            return true;
        }
        catch (Exception exception)
        {
            _log($"The dat report couldn't be sent again: {exception.Message}");
            return false;
        }
    }
}
