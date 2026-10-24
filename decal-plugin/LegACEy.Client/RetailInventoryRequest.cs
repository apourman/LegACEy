using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's inventory request in flight. The client stores the kind of request it sent (move, use, wield…; 0 for none) at 0x00871ED0 and
/// clears it when the server answers; while it is set, a new move is refused with "You can only move or use one item at a time"
/// (0x0058DE80). Call on the game thread.
/// </summary>
internal sealed class RetailInventoryRequest
{
    private const string Source = "our objdump read of installed acclient.exe";
    private static readonly IntPtr Request = new(0x00871ED0);

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("the move/use gate refuses while a request is in flight", 0x0058DE93,
            NativeUiCatalogue.Bytes("A1 D0 1E 87 00 85 C0 74 76"), Source, "reads the request kind at 0x00871ED0; nonzero shows the one-at-a-time error"),
        new NativeUiEntry("the server's answer clears the request", 0x0058DAC3,
            NativeUiCatalogue.Bytes("C7 05 D4 1E 87 00 00 00 00 00 C7 05 D0 1E 87 00 00 00 00 00"), Source, "request object (0x00871ED4) and kind set to 0")
    };

    public RetailInventoryRequest(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
            throw new InvalidOperationException("the running client does not match its native entries");
    }

    public bool Pending => Marshal.ReadInt32(Request) != 0;
}
