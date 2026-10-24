using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LegACEy.Updater;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Spike: serves a world patch's records to the client without touching the dat files. Every record the client reads, every "does this id
/// exist" check and every edition read goes through <c>DiskController::LoadDataEx</c> (the threaded controller locks and calls it too), so
/// one hook there answers from the patch when it holds the id for that dat, and lets everything else through to the retail file.
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

    // Dat file name → id → record. Read-only once built: LoadDataEx runs on the game thread and the loader threads.
    private readonly Dictionary<string, Dictionary<uint, WorldPatchRecord>> _records = new(StringComparer.OrdinalIgnoreCase);
    // Controller → its dat's records, or null for a dat the patch doesn't touch. Written under the lock.
    private readonly Dictionary<IntPtr, Dictionary<uint, WorldPatchRecord>?> _controllers = new();
    private readonly HashSet<(IntPtr, uint)> _logged = new();
    private readonly object _lock = new();
    private readonly Action<string> _log;
    private IHook<LoadDataExFn>? _hook;
    private GCHandle _root;
    private volatile bool _off;
    private int _served, _answered;

    public int Version { get; }

    private DatOverlay(WorldPatch patch, Action<string> log)
    {
        Version = patch.Version;
        _log = log;
        foreach (var record in patch.Records)
        {
            if (!_records.TryGetValue(record.Dat, out var ids)) _records[record.Dat] = ids = new Dictionary<uint, WorldPatchRecord>();
            ids[record.Id] = record;
        }
        _root = GCHandle.Alloc(this);
    }

    /// <summary>The overlay for the patch at <paramref name="path"/>, hooked into the running client; null when there's no patch or it can't be placed.</summary>
    public static DatOverlay? Create(string path, Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!File.Exists(path)) return null;
        WorldPatch patch;
        try
        {
            using var stream = File.OpenRead(path);
            patch = WorldPatch.Read(stream);
        }
        catch (Exception exception)
        {
            log($"Dat overlay off: can't read {path}: {exception.Message}");
            return null;
        }
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries, "Dat overlay off: the running client does not match its entries.")) return null;
        var overlay = new DatOverlay(patch, log);
        try
        {
            overlay._hook = ReloadedHooks.Instance.CreateHook<LoadDataExFn>(overlay.OnLoadDataEx, LoadDataExAddress);
            overlay._hook.Activate();
        }
        catch (Exception exception)
        {
            log($"Dat overlay off: {exception.Message}");
            overlay._root.Free();
            return null;
        }
        foreach (var dat in overlay._records) log($"Dat overlay on: world {patch.Version}, {dat.Value.Count} records for {dat.Key} from {path}");
        return overlay;
    }

    private byte OnLoadDataEx(IntPtr controller, uint id, IntPtr pack, IntPtr entry, uint flags)
    {
        WorldPatchRecord? record = null;
        try
        {
            if (!_off) Records(controller)?.TryGetValue(id, out record);
        }
        catch (Exception exception) { Report($"Dat overlay lookup failed for 0x{id:X8}; reading the dat: {exception.Message}"); }
        if (record == null) return _hook!.OriginalFunction(controller, id, pack, entry, flags);
        try
        {
            Serve(controller, id, pack, entry, flags, record);
            return 1;
        }
        catch (Exception exception)
        {
            Report($"Dat overlay couldn't serve 0x{id:X8}; reading the dat: {exception.Message}");
            return _hook!.OriginalFunction(controller, id, pack, entry, flags);
        }
    }

    /// <summary>Fills the entry, and unless only looked up the pack, as LoadDataEx would for an uncompressed record of the patch's bytes.</summary>
    private void Serve(IntPtr controller, uint id, IntPtr pack, IntPtr entry, uint flags, WorldPatchRecord record)
    {
        // Retail's entry when the dat has the id keeps its version word and date; a new id starts from zeros.
        if (_hook!.OriginalFunction(controller, id, pack, entry, LookUpOnly) == 0)
            for (var i = 0; i < EntryLength; i += 4) Marshal.WriteInt32(entry, i, 0);
        var head = Marshal.ReadInt32(entry);
        Marshal.WriteInt32(entry, head & ~1);
        Marshal.WriteInt32(entry, EntryId, unchecked((int)id));
        Marshal.WriteInt32(entry, EntrySize, record.Data.Length);
        Marshal.WriteInt32(entry, EntryIteration, record.Iteration);
        var lookUp = (flags & LookUpOnly) != 0;
        if (!lookUp)
        {
            Marshal.WriteInt32(pack, PackVersion, (head >> 16) & 0xFFFF);
            var buffer = IntPtr.Add(pack, PackBuffer);
            var size = record.Data.Length + BufferHeader;
            Grow(buffer, (uint)size, 1);
            // Grow only ever enlarges; the record's size is what GetDataSize must report.
            Marshal.WriteInt32(buffer, BufferSize, size);
            var bytes = GetBuffer(buffer);
            Marshal.WriteInt32(bytes, 0);
            Marshal.Copy(record.Data, 0, IntPtr.Add(bytes, BufferHeader), record.Data.Length);
        }
        bool first;
        lock (_lock) first = _logged.Add((controller, id));
        if (lookUp) _answered++;
        else _served++;
        if (first || id == 0xFFFF0001)
            Report($"Dat overlay {(lookUp ? "answered" : "served")} {record.Dat} 0x{id:X8} ({record.Data.Length} bytes, iteration {record.Iteration}, flags 0x{flags:X}); {_served} served, {_answered} looked up so far");
    }

    /// <summary>The patch's records for the controller's dat, found by its file name the first time the controller is seen.</summary>
    private Dictionary<uint, WorldPatchRecord>? Records(IntPtr controller)
    {
        lock (_lock)
        {
            if (_controllers.TryGetValue(controller, out var known)) return known;
            var path = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(controller, FilenameOffset)) ?? "";
            var name = Path.GetFileName(path);
            _records.TryGetValue(name, out var records);
            _controllers[controller] = records;
            Report($"Dat overlay saw {path}: {(records == null ? "not in the patch" : $"{records.Count} records")}");
            return records;
        }
    }

    /// <summary>Reporting runs inside the native callback, so it must never throw.</summary>
    private void Report(string message)
    {
        try { _log(message); }
        catch { /* Reporting must never escape the native callback. */ }
    }

    public void Dispose()
    {
        _off = true;
        try { _hook?.Disable(); }
        catch (Exception exception)
        {
            // Still callable: stays rooted and reads the dats from now on.
            Report($"Could not remove the dat overlay; it reads the dats from now on: {exception.Message}");
            return;
        }
        if (_root.IsAllocated) _root.Free();
    }
}
