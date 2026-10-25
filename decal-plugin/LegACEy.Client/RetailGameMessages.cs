using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's spew box (gmSpewBoxUI): the yellow line that drops in at the top of the screen and fades, such as "You can't move that!" or "The
/// chest is locked". It takes only lines of chat type 0x1A, which no chat window shows, from the DisplayFinalStringInfo notice (0x186B6).
/// This points the spew box's notice slot at our callback, so its lines are queued for LegACEy's own toast and <see cref="Poll"/> hands them
/// out between frames. While <see cref="Taking"/> is false, or a line can't be read, the line goes on to retail's spew box as before.
/// </summary>
/// <remarks>
/// The slot is in gmSpewBoxUI's notice handler vtable, so it holds for every spew box retail makes, at every login. <see cref="Dispose"/>
/// puts retail's handler back; one that could not keeps this object, its callback and its queue alive, as retail may still call it.
/// </remarks>
internal sealed class RetailGameMessages : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xC60 in this region) for names; our objdump read of installed acclient.exe";
    // gmSpewBoxUI's notice handler vtable (at element +0x5F8) and its DisplayFinalStringInfo slot (+0x38).
    private const uint NoticeVtable = 0x007BDE20, Slot = NoticeVtable + 0x38;
    // gmSpewBoxUI::RecvNotice_DisplayFinalStringInfo: queues the line when its type is SpewType.
    private const uint SpewHandler = 0x004D6D00;
    private const uint SpewType = 0x1A;
    // The notice's text is a StringInfo whose literal sits at +0x80.
    private const int StringLiteral = 0x80;
    private const uint PageReadWrite = 0x04;
    private static readonly List<RetailGameMessages> Stranded = new();

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        Entry("gmSpewBoxUI's constructor sets its notice handler vtable at +0x5F8", 0x004D66B6, "C7 86 F8 05 00 00 20 DE 7B 00", "instruction reference"),
        Entry("gmSpewBoxUI::PostInit registers +0x5F8 for DisplayFinalStringInfo (0x186B6)", 0x004D6816, "81 C6 F8 05 00 00 56 68 B6 86 01 00 8B C8 FF 52 04",
            "instruction reference (push handler; push 0x186B6; call [edx+4])"),
        Entry("gmSpewBoxUI notice vtable +0x38 is its DisplayFinalStringInfo", Slot, "00 6D 4D 00", "data reference (vtable slot)"),
        Entry("gmSpewBoxUI::RecvNotice_DisplayFinalStringInfo takes only type 0x1A", SpewHandler, "83 7C 24 04 1A 75 0D 8B 44 24 08 50 83 C1 08 E8 8C FF FF FF C2 10 00",
            "ThisCall (type, text, prefix, target), ret 0x10"),
        Entry("ECM_UI::SendNotice_DisplayFinalStringInfo calls handler slot +0x38 (type, text, prefix, target)", 0x006934D9,
            "8B 54 24 14 8B 01 57 53 55 52 FF 50 38", "instruction reference (the notice loop's call)"),
        Entry("StringInfo::SetLiteralValue stores the literal at +0x80", 0x0042CC34, "8B 03 89 86 80 00 00 00", "instruction reference (mov [esi+0x80], eax)")
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NoticeFn(IntPtr handler, uint chatType, IntPtr text, IntPtr timestampPrefix, uint targetWindow);

    private readonly NoticeFn _callback;
    private readonly NoticeFn _retail = Marshal.GetDelegateForFunctionPointer<NoticeFn>(new IntPtr(SpewHandler));
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Action<string> _log;
    private bool _patched;

    /// <summary>Points the spew box at our callback. Throws when the running client does not match, and nothing is changed.</summary>
    public RetailGameMessages(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        _log = log;
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
            throw new InvalidOperationException("the running client does not match its native entries.");
        _callback = OnNotice;
        WriteSlot(Marshal.GetFunctionPointerForDelegate(_callback));
        _patched = true;
    }

    /// <summary>Whether the lines are ours to show; while false they go to retail's spew box. Set on the game thread.</summary>
    public bool Taking { get; set; }

    /// <summary>The lines that came since the last call, oldest first. Call on the game thread.</summary>
    public IEnumerable<string> Poll()
    {
        while (_pending.TryDequeue(out var line)) yield return line;
    }

    public void Dispose()
    {
        if (!_patched) return;
        try
        {
            WriteSlot(new IntPtr(SpewHandler));
            _patched = false;
        }
        catch (Exception exception)
        {
            _log($"Retail's game messages could not be given back to its spew box: {exception.Message}");
            lock (Stranded) Stranded.Add(this);
        }
    }

    /// <summary>Retail's notice loop calls this for every DisplayFinalStringInfo. Never throws into the client.</summary>
    private void OnNotice(IntPtr handler, uint chatType, IntPtr text, IntPtr timestampPrefix, uint targetWindow)
    {
        try
        {
            if (chatType == SpewType && Taking)
            {
                var literal = Marshal.ReadIntPtr(text, StringLiteral);
                // Retail's text escapes ("You're too busy\!") are drawn, as chat's are, without the backslash.
                var line = literal == IntPtr.Zero ? null : Marshal.PtrToStringUni(literal) is { } raw ? NativeRetailChatPort.RetailChatTypes.Unescape(raw).Trim() : null;
                if (!string.IsNullOrEmpty(line))
                {
                    _pending.Enqueue(line);
                    return;
                }
            }
            _retail(handler, chatType, text, timestampPrefix, targetWindow);
        }
        catch
        {
            // A line that cannot be read or shown is dropped; the client keeps running.
        }
    }

    private static void WriteSlot(IntPtr target)
    {
        var slot = new IntPtr(unchecked((int)Slot));
        if (!VirtualProtect(slot, (UIntPtr)4, PageReadWrite, out var old))
            throw new InvalidOperationException($"Could not unprotect 0x{Slot:X8}: error {Marshal.GetLastWin32Error()}.");
        try { Marshal.WriteIntPtr(slot, target); }
        finally { VirtualProtect(slot, (UIntPtr)4, old, out _); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint oldProtection);

    private static NativeUiEntry Entry(string name, uint address, string bytes, string convention) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, convention);
}
