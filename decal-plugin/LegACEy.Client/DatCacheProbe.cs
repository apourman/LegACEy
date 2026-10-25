using System;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Logs which dat records the client holds in memory, per object cache, at the moment the dat overlay switches on. The overlay only changes
/// records read after that, so these are the ones a world can't replace without a relaunch. The client registers one <c>DBOCache</c> per
/// object type in a global hash table (type → cache); each cache keeps every object it holds, in use or on its free list, in one hash
/// table (id → object), and an object with a reference count of 1 is held only by the cache, on the free list. Every read goes through
/// <c>ReadProcessMemory</c>, so a table a loader thread is changing gives a wrong count, never a crash.
/// </summary>
// ponytail: portal-overlay spike diagnostic, on only when the game starts with a legacey-cache-probe file beside acclient.exe; remove when the spike's spec replaces it.
internal static class DatCacheProbe
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build) for names; our objdump read of installed acclient.exe";
    // The type → DBOCache registry (IntrusiveHashTable at 0x0081860C): bucket array pointer and bucket count. A node: key, next, value.
    private const uint RegistryBuckets = 0x0081866C, RegistryCount = 0x00818674;
    // DBOCache: its object table's bucket array and bucket count, and the free list's length. An object node: id, next, DBObj*.
    private const int CacheBuckets = 0x68, CacheCount = 0x70, FreeCount = 0x108;
    // DBObj: reference count (1 = held by the cache only).
    private const int ObjectReferences = 0x24;
    private const int MaxNodes = 500_000, IdsPerLine = 40;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("DBCache::GetDBOCache (static, by type)", 0x004144E0,
            NativeUiCatalogue.Bytes("8B 4C 24 04 83 EC 0C 85 C9 74 47 33 D2 8B C1 F7 35 74 86 81 00 A1 6C 86 81 00"), Source,
            "the registry: buckets 0x0081866C, bucket count 0x00818674; node +0 type, +4 next, +8 DBOCache*"),
        new NativeUiEntry("DBOCache::GetIfInMemory (past the prologue)", 0x00416EB5,
            NativeUiCatalogue.Bytes("33 D2 8B C6 F7 71 70 8B 41 68"), Source, "object table: buckets +0x68, bucket count +0x70; node +0 id, +4 next, +8 DBObj*"),
        new NativeUiEntry("DBOCache::GetFreeObj", 0x00416A70,
            NativeUiCatalogue.Bytes("56 8B F1 8A 86 F4 00 00 00 84 C0 57 74 2F 8B 86 08 01 00 00"), Source, "free list length +0x108"),
        new NativeUiEntry("DBOCache release (reads DBObj id and references)", 0x00416C80,
            NativeUiCatalogue.Bytes("56 57 8B 7C 24 0C 8B 77 28"), Source, "DBObj +0x24 references, +0x28 id"),
    };

    // Id high bytes of the portal records a world's art touches; these caches get their full id list.
    private static readonly Dictionary<uint, string> ArtTypes = new()
    {
        [0x01] = "GfxObj", [0x02] = "Setup", [0x04] = "Palette", [0x05] = "SurfaceTexture", [0x06] = "RenderSurface", [0x08] = "Surface",
        [0x0D] = "Environment",
    };

    /// <summary>Logs what each object cache holds, when the probe's file is there; logs why and skips when the client doesn't match.</summary>
    public static void Log(string moment, Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        try
        {
            if (!NativeUiCatalogue.Validate(readMemory, log, Entries, "The dat cache probe is off: the running client does not match its entries.")) return;
            uint Read(uint address) => BitConverter.ToUInt32(readMemory(address, 4), 0);
            var caches = Nodes(Read, Read(RegistryBuckets), Read(RegistryCount)).OrderBy(node => node.Key).ToList();
            var lines = new List<string> { $"Dat cache probe at {moment}: {caches.Count} object caches." };
            foreach (var (type, cache) in caches)
            {
                var objects = Nodes(Read, Read(cache + CacheBuckets), Read(cache + CacheCount)).ToList();
                if (objects.Count == 0) continue;
                var free = objects.Count(node => Read(node.Value + ObjectReferences) <= 1);
                var kinds = objects.GroupBy(node => node.Key >> 24).OrderBy(group => group.Key)
                    .Select(group => $"{group.Count()}×0x{group.Key:X2}");
                lines.Add($"  type 0x{type:X2}: {objects.Count} held, {free} free (list {Read(cache + FreeCount)}); ids {string.Join(", ", kinds)}");
                foreach (var group in objects.GroupBy(node => node.Key >> 24).Where(group => ArtTypes.ContainsKey(group.Key)))
                {
                    var ids = group.Select(node => node.Key).OrderBy(id => id).ToList();
                    for (var i = 0; i < ids.Count; i += IdsPerLine)
                        lines.Add($"    {ArtTypes[group.Key]}: {string.Join(" ", ids.Skip(i).Take(IdsPerLine).Select(id => id.ToString("X8")))}");
                }
            }
            log(string.Join(Environment.NewLine, lines));
        }
        catch (Exception exception) { log($"The dat cache probe at {moment} stopped: {exception.Message}"); }
    }

    /// <summary>A client hash table's nodes as (key, value); stops after <see cref="MaxNodes"/>, in case a table changed under the walk.</summary>
    private static IEnumerable<KeyValuePair<uint, uint>> Nodes(Func<uint, uint> read, uint buckets, uint count)
    {
        var seen = 0;
        for (uint bucket = 0; bucket < count; bucket++)
            for (var node = read(buckets + bucket * 4); node != 0 && seen < MaxNodes; node = read(node + 4), seen++)
                yield return new(read(node), read(node + 8));
    }
}
