using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Serves a world patch's records to the client without touching the dat files. Every record the client reads, every "does this id exist"
/// check and every edition read goes through <c>DiskController::LoadDataEx</c> (the threaded controller locks and calls it too), so one
/// hook there answers from the patch when it holds the id for that dat, and lets everything else through to the retail file. The hook is
/// placed once; <see cref="Use"/> switches the patch it serves at any time. Records the client already holds in its caches stay as they are.
/// </summary>
internal sealed class DatOverlay : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xFA0 at 0x0067xxxx, +0x360 at 0x0040xxxx) for names; our objdump read of installed acclient.exe";
    private const uint LoadDataExAddress = 0x00671B60, GrowAddress = 0x004074B0, BufferAddress = 0x00406D80;
    // DiskController: the dat's file name (PStringBase<char>, one char pointer).
    private const int FilenameOffset = 0x6FC;
    // Cache_Pack_t: +4 the entry's version word, +8 its SmartBuffer (+4 size). The record starts 4 bytes into the buffer (GetPackBufferPtr).
    private const int PackVersion = 4, PackBuffer = 8, BufferSize = 4, BufferHeader = 4;
    // BTEntry, six dwords: flags (bit 0 compressed, high word version), id, file offset, size, date, iteration.
    private const int EntryLength = 24, EntryId = 4, EntrySize = 12, EntryIteration = 20;
    // LoadDataEx flags: 2 looks the id up only, without reading it.
    private const uint LookUpOnly = 2;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("DiskController::LoadDataEx", LoadDataExAddress,
            NativeUiCatalogue.Bytes("83 EC 20 8B 54 24 24 8D 44 24 00 50 89 4C 24 0C 83 C1 1C 52"), Source,
            "ThisCall (DiskController*, id, Cache_Pack_t&, BTEntry&, flags) -> bool, ret 0x10; vtable +0x74, also called by CThreadsafeDiskController::LoadDataEx 0x004F8B2C"),
        new NativeUiEntry("SmartBuffer grow (LoadDataEx sizes the pack with it)", GrowAddress,
            NativeUiCatalogue.Bytes("8A 44 24 08 A8 08 53 55 56 8B F1 BB 01 00 00 00"), Source, "ThisCall (SmartBuffer*, size, flags), ret 8"),
        new NativeUiEntry("SmartBuffer::GetBuffer", BufferAddress,
            NativeUiCatalogue.Bytes("8B 41 08 85 C0 74 06 8B 40 08 03 01 C3"), Source, "ThisCall (SmartBuffer*) -> byte*"),
    };

    // A byte, not bool: retail returns in AL.
    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate byte LoadDataExFn(IntPtr controller, uint id, IntPtr pack, IntPtr entry, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GrowFn(IntPtr buffer, uint size, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr GetBufferFn(IntPtr buffer);

    private static readonly GrowFn Grow = Marshal.GetDelegateForFunctionPointer<GrowFn>(new IntPtr(GrowAddress));
    private static readonly GetBufferFn GetBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferFn>(new IntPtr(BufferAddress));

    // Controller → its dat's file name, found the first time it's seen with one. Read without a lock: LoadDataEx runs on the game thread and the loader threads.
    private readonly ConcurrentDictionary<IntPtr, string> _dats = new();
    private readonly Action<string> _log;
    private IHook<LoadDataExFn>? _hook;
    private GCHandle _root;
    private volatile WorldPatchIndex? _patch;
    private volatile bool _off;
    // Since the last summary: calls while a patch is on, records served, look-ups answered, time inside the hook (Stopwatch ticks), and
    // failures. The hook never logs; the game thread reads these.
    private long _calls, _served, _answered, _ticks, _worst, _failures;
    private volatile string? _failure;
    // Diagnostic (portal-space hang, 24 Oct): the first ask for each id in a landblock the patch touches, written by the game thread.
    private readonly ConcurrentDictionary<(string, uint), byte> _asked = new();
    private readonly ConcurrentQueue<string> _askedLog = new();

    private DatOverlay(Action<string> log)
    {
        _log = log;
        _root = GCHandle.Alloc(this);
    }

    /// <summary>The patch served now; null when the client reads the retail dats.</summary>
    public WorldPatchIndex? Patch => _patch;

    /// <summary>The overlay hooked into the running client, serving nothing yet; null when its entries don't match or it can't be placed.</summary>
    public static DatOverlay? Create(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries, "Worlds are off: the running client does not match the dat overlay's entries.")) return null;
        var overlay = new DatOverlay(log);
        try
        {
            overlay._hook = ReloadedHooks.Instance.CreateHook<LoadDataExFn>(overlay.OnLoadDataEx, LoadDataExAddress);
            overlay._hook.Activate();
            return overlay;
        }
        catch (Exception exception)
        {
            log($"Worlds are off: the dat overlay couldn't be placed: {exception.Message}");
            overlay._root.Free();
            return null;
        }
    }

    /// <summary>Serves <paramref name="patch"/> from the next dat read on, or the retail dats for null. Safe while loader threads read.</summary>
    public void Use(WorldPatchIndex? patch)
    {
        // The old patch's file stays open: a loader thread may be reading it right now. It closes when collected.
        var old = Interlocked.Exchange(ref _patch, patch);
        if (old == patch) return;
        _log(patch == null ? "Dat overlay off: the client reads the retail dats." : $"Dat overlay on: world {patch.Version}, {patch.Count} records from {patch.Path}.");
    }

    /// <summary>What the hook did since the last summary, then starts counting again; null when it saw nothing.</summary>
    public string? TakeSummary()
    {
        var calls = Interlocked.Exchange(ref _calls, 0);
        var failures = Interlocked.Exchange(ref _failures, 0);
        if (calls == 0 && failures == 0) return null;
        var served = Interlocked.Exchange(ref _served, 0);
        var answered = Interlocked.Exchange(ref _answered, 0);
        var ticks = Interlocked.Exchange(ref _ticks, 0);
        var worst = Interlocked.Exchange(ref _worst, 0);
        var summary = $"Dat overlay: world {_patch?.Version.ToString() ?? "off"}, {calls} calls, {served} served, {answered} looked up, "
            + $"{ticks * 1000.0 / Stopwatch.Frequency:0.0} ms inside the hook, worst {worst * 1_000_000.0 / Stopwatch.Frequency:0} µs";
        return failures == 0 ? summary + "." : summary + $", {failures} failed and read from the dat (last: {_failure}).";
    }

    private byte OnLoadDataEx(IntPtr controller, uint id, IntPtr pack, IntPtr entry, uint flags)
    {
        var patch = _patch;
        if (patch == null || _off) return _hook!.OriginalFunction(controller, id, pack, entry, flags);
        var start = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _calls);
        WorldPatchEntry record = default;
        var found = false;
        try { found = Dat(controller) is { } dat && patch.Records(dat)?.TryGetValue(id, out record) == true; }
        catch (Exception exception) { Fail(id, exception); }
        Note(controller, id, flags, found, patch);
        if (found)
        {
            try
            {
                Serve(controller, id, pack, entry, flags, patch, record);
                Time(start);
                return 1;
            }
            catch (Exception exception) { Fail(id, exception); }
        }
        Time(start);
        return _hook!.OriginalFunction(controller, id, pack, entry, flags);
    }

    /// <summary>Fills the entry, and unless only looked up the pack, as LoadDataEx would for an uncompressed record of the patch's bytes.</summary>
    private void Serve(IntPtr controller, uint id, IntPtr pack, IntPtr entry, uint flags, WorldPatchIndex patch, WorldPatchEntry record)
    {
        var lookUp = (flags & LookUpOnly) != 0;
        // Read first: a failed read leaves the entry and pack untouched for the retail read.
        var data = lookUp ? null : patch.Read(record);
        // Retail's entry when the dat has the id keeps its version word and date; a new id starts from zeros.
        if (_hook!.OriginalFunction(controller, id, pack, entry, LookUpOnly) == 0)
            for (var i = 0; i < EntryLength; i += 4) Marshal.WriteInt32(entry, i, 0);
        var head = Marshal.ReadInt32(entry);
        Marshal.WriteInt32(entry, head & ~1);
        Marshal.WriteInt32(entry, EntryId, unchecked((int)id));
        Marshal.WriteInt32(entry, EntrySize, record.Length);
        Marshal.WriteInt32(entry, EntryIteration, record.Iteration);
        if (data == null)
        {
            Interlocked.Increment(ref _answered);
            return;
        }
        Marshal.WriteInt32(pack, PackVersion, (head >> 16) & 0xFFFF);
        var buffer = IntPtr.Add(pack, PackBuffer);
        var size = data.Length + BufferHeader;
        Grow(buffer, (uint)size, 1);
        // Grow only ever enlarges; the record's size is what GetDataSize must report.
        Marshal.WriteInt32(buffer, BufferSize, size);
        var bytes = GetBuffer(buffer);
        Marshal.WriteInt32(bytes, 0);
        Marshal.Copy(data, 0, IntPtr.Add(bytes, BufferHeader), data.Length);
        Interlocked.Increment(ref _served);
    }

    /// <summary>The controller's dat file name; null while the controller has none yet, so it is looked up again on its next call.</summary>
    private string? Dat(IntPtr controller)
    {
        if (_dats.TryGetValue(controller, out var known)) return known;
        var name = Path.GetFileName(Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(controller, FilenameOffset)) ?? "");
        if (name.Length == 0) return null;
        _dats[controller] = name;
        return name;
    }

    private void Note(IntPtr controller, uint id, uint flags, bool found, WorldPatchIndex patch)
    {
        try
        {
            if (_asked.Count > 2000 || Dat(controller) is not { } dat || patch.Records(dat) is not { } records) return;
            if (!found && !records.ContainsKey((id & 0xFFFF0000) | 0xFFFF) && !records.ContainsKey((id & 0xFFFF0000) | 0xFFFE)) return;
            if (_asked.TryAdd((dat, id), 0))
                _askedLog.Enqueue($"{dat} 0x{id:X8} flags 0x{flags:X} {(found ? "from the patch" : "NOT in the patch")} thread {Environment.CurrentManagedThreadId}");
        }
        catch (Exception) { }
    }

    /// <summary>Diagnostic: the asks noted since the last call.</summary>
    public IEnumerable<string> TakeAsked()
    {
        while (_askedLog.TryDequeue(out var line)) yield return line;
    }

    private void Time(long start)
    {
        var spent = Stopwatch.GetTimestamp() - start;
        Interlocked.Add(ref _ticks, spent);
        var worst = Interlocked.Read(ref _worst);
        while (spent > worst)
        {
            var seen = Interlocked.CompareExchange(ref _worst, spent, worst);
            if (seen == worst) break;
            worst = seen;
        }
    }

    private void Fail(uint id, Exception exception)
    {
        Interlocked.Increment(ref _failures);
        _failure = $"0x{id:X8}: {exception.Message}";
    }

    public void Dispose()
    {
        _off = true;
        try { _hook?.Disable(); }
        catch (Exception exception)
        {
            // Still callable: stays rooted and reads the dats from now on.
            _log($"Could not remove the dat overlay; it reads the dats from now on: {exception.Message}");
            return;
        }
        if (_root.IsAllocated) _root.Free();
    }
}
