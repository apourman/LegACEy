using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's spew box (gmSpewBoxUI): the yellow line that drops in at the top of the screen and fades, such as "You can't move that!" or "The
/// chest is locked". It takes only lines of chat type 0x1A, which no chat window shows, queues them, and draws them later from its display
/// loop. This points the loop's call that sets a line's text at our callback, so the finished lines are queued for LegACEy's own toast and
/// <see cref="Poll"/> hands them out between frames; retail's line gets an empty text. While <see cref="Taking"/> is false, or a line can't
/// be read, the line goes on to retail's spew box as before.
/// </summary>
/// <remarks>
/// The text is taken at display and not when the line arrives: some lines ("The Trade Notes (250,000) can't be split") arrive as a
/// StringInfo with no text yet, which only the display loop's StringInfo::ToString fills in. The call is in gmSpewBoxUI's code, so it holds
/// for every spew box retail makes, at every login. <see cref="Dispose"/> puts retail's call back; one that could not keeps this object, its
/// callback and its queue alive, as retail may still call it.
/// </remarks>
internal sealed class RetailGameMessages : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xC60 in this region) for names; our objdump read of installed acclient.exe";
    // gmSpewBoxUI's display loop (0x004D6A50) builds each queued line with StringInfo::ToString, trims it, then sets it on its element here.
    private const uint SetTextCall = 0x004D6B06;
    // A PStringBase<wchar_t> holding retail's shared empty string.
    private const uint EmptyWide = 0x00818340;
    private const uint PageExecuteReadWrite = 0x40;
    private static readonly List<RetailGameMessages> Stranded = new();

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // Ends at the call's E8; its rel32 is read live, as Decal patches some call sites.
        Entry("gmSpewBoxUI's display loop sets the built line on its element (lea eax, [esp+0x10]; push eax; mov ecx, esi; call)", SetTextCall - 7,
            "8D 44 24 10 50 8B CE E8", "ThisCall (PStringBase<wchar_t>* text), ret 4; result unused"),
        Entry("gmSpewBoxUI's display loop sets the line's font colour next", SetTextCall + 5, "68 A0 C5 81 00 8B CE", "instruction reference"),
        Entry("retail's empty PStringBase<wchar_t>", EmptyWide, "90 83 81 00", "data reference (string buffer pointer)")
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetTextFn(IntPtr element, IntPtr text);

    private readonly SetTextFn _callback;
    private readonly int _retailRel32;
    private readonly SetTextFn _retail;
    private readonly IntPtr _empty = new(unchecked((int)EmptyWide));
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Action<string> _log;
    private bool _patched;

    /// <summary>Points the spew box's text call at our callback. Throws when the running client does not match, and nothing is changed.</summary>
    public RetailGameMessages(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        _log = log;
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
            throw new InvalidOperationException("the running client does not match its native entries.");
        _retailRel32 = Marshal.ReadInt32(At(SetTextCall + 1));
        _retail = Marshal.GetDelegateForFunctionPointer<SetTextFn>(IntPtr.Add(At(SetTextCall + 5), _retailRel32));
        _callback = OnSetText;
        WriteRel32(Marshal.GetFunctionPointerForDelegate(_callback).ToInt32() - (int)(SetTextCall + 5));
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
            WriteRel32(_retailRel32);
            _patched = false;
        }
        catch (Exception exception)
        {
            _log($"Retail's game messages could not be given back to its spew box: {exception.Message}");
            lock (Stranded) Stranded.Add(this);
        }
    }

    /// <summary>Retail's display loop calls this for every spew box line. Never throws into the client.</summary>
    private void OnSetText(IntPtr element, IntPtr text)
    {
        try
        {
            // ponytail: diagnostic for backlog 104; remove once the split line is found.
            try
            {
                var seen = Marshal.ReadIntPtr(text);
                var raw = seen == IntPtr.Zero ? "<null buffer>" : Marshal.PtrToStringUni(seen);
                _log($"[104] spew SetText: taking {Taking}, element 0x{element.ToInt32():X8}, text 0x{text.ToInt32():X8} -> 0x{seen.ToInt32():X8}, \"{raw}\" ({raw?.Length ?? 0} chars).");
            }
            catch (Exception exception) { _log($"[104] spew SetText could not be read: {exception.Message}"); }
            if (Taking)
            {
                var chars = Marshal.ReadIntPtr(text);
                // Retail's text escapes ("You're too busy\!") are drawn, as chat's are, without the backslash; a line break joins the band's one line.
                var line = chars == IntPtr.Zero ? null
                    : Marshal.PtrToStringUni(chars) is { } raw ? NativeRetailChatPort.RetailChatTypes.Unescape(raw).Replace('\n', ' ').Trim() : null;
                if (!string.IsNullOrEmpty(line))
                {
                    _pending.Enqueue(line);
                    _retail(element, _empty);
                    return;
                }
            }
            _retail(element, text);
        }
        catch
        {
            // A line that cannot be read or shown is dropped; the client keeps running.
        }
    }

    private static void WriteRel32(int rel32)
    {
        var operand = At(SetTextCall + 1);
        if (!VirtualProtect(operand, (UIntPtr)4, PageExecuteReadWrite, out var old))
            throw new InvalidOperationException($"Could not unprotect 0x{SetTextCall + 1:X8}: error {Marshal.GetLastWin32Error()}.");
        try { Marshal.WriteInt32(operand, rel32); }
        finally
        {
            VirtualProtect(operand, (UIntPtr)4, old, out _);
            FlushInstructionCache(GetCurrentProcess(), operand, (UIntPtr)4);
        }
    }

    private static IntPtr At(uint address) => new(unchecked((int)address));

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint oldProtection);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static NativeUiEntry Entry(string name, uint address, string bytes, string convention) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, convention);
}
