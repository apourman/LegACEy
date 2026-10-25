using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Where the server's world stands. Waiting (for the server's note), Checking, Downloading and Switching (a patch ready to switch on and
/// report) hold Enter at character select; Off (a plain server: no note), UpToDate, On (switched on and reported), Relaunch (switched on
/// but not reported: the next launch reports it) and Unavailable (the update list or patch couldn't be had) leave it to the server.
/// </summary>
internal enum WorldState { Waiting, Off, Checking, UpToDate, Downloading, Switching, On, Relaunch, Unavailable }

/// <summary>The world manifest: the world version it brings a game to, and the patch's address, size in bytes and SHA-256.</summary>
internal sealed record WorldManifest(int Version, string Patch, long Size, string Sha256)
{
    public static WorldManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new WorldManifest(root.GetProperty("version").GetInt32(), root.GetProperty("patch").GetString()!, root.GetProperty("size").GetInt64(),
            root.GetProperty("sha256").GetString()!);
    }
}


/// <summary>
/// Lays the server's world over the retail dats. At startup, the world cached for the server the game was launched for is switched on
/// (<see cref="AtStartup"/>), before the client opens its dats, so it reports that world's edition. At character select Enter waits until
/// the server's note (<see cref="Heard"/>) or the end of the DDD exchange without one (<see cref="NoNote"/>: a plain server, the overlay
/// goes off). With a note, off the game thread: the world served already at the note's edition is up to date; one cached at it is ready to
/// switch on; otherwise the note's update list is read and the patch downloaded into the cache, its size and SHA-256 checked (a bad
/// download is thrown away and tried once more). A patch to switch on waits in Switching, holding Enter, until the game thread switches it
/// on and sends the dat report again (<see cref="SwitchedOn"/>). Any failure is logged and ends in Unavailable, which leaves Enter to the
/// server. The game folder's dats are never touched.
/// </summary>
internal sealed class WorldUpdates
{
    // Short: Enter is held while the manifest is fetched.
    private static readonly TimeSpan ManifestDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadDeadline = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestHeaders = { { "User-Agent", "LegACEy-decal-plugin" } } };

    private readonly WorldCache _cache;
    private readonly string? _server;
    private readonly Func<string, CancellationToken, Task<string>> _getString;
    private readonly Func<string, Stream, Action<long>, CancellationToken, Task> _download;
    private int _state;
    private int _percent;
    private Task _running = Task.CompletedTask;
    // What Switching waits to switch on.
    private (WorldPatchIndex Patch, CachedWorld World)? _pending;

    /// <param name="server">The server the game was launched for, <c>host:port</c>; null when unknown, and no world is remembered for it.</param>
    /// <param name="getString">Fetches the manifest; null for HTTP.</param>
    /// <param name="download">Writes the patch at an address into a stream, reporting the bytes written so far; null for HTTP.</param>
    public WorldUpdates(WorldCache cache, string? server, Func<string, CancellationToken, Task<string>>? getString = null,
        Func<string, Stream, Action<long>, CancellationToken, Task>? download = null)
    {
        _cache = cache;
        _server = server;
        _getString = getString ?? ((url, cancel) => Http.GetStringAsync(url, cancel));
        _download = download ?? Fetch;
    }

    public WorldState State => (WorldState)Volatile.Read(ref _state);

    /// <summary>The download's progress, 0–100.</summary>
    public int Percent => Volatile.Read(ref _percent);

    /// <summary>The server's world, once its note came.</summary>
    public WorldNote? Server { get; private set; }

    /// <summary>The world the overlay serves, and its patch; null for the retail dats.</summary>
    public CachedWorld? Active { get; private set; }
    public WorldPatchIndex? ActivePatch { get; private set; }

    /// <summary>The patch Switching waits to switch on.</summary>
    public WorldPatchIndex? Pending => _pending?.Patch;

    /// <summary>The patch switched on last changes records the client already held at switch-on (<see cref="WorldPatch.HeldAtSwitchOn"/>)
    /// that the one before it didn't: those show after a relaunch.</summary>
    public bool PortalNeedsRelaunch { get; private set; }

    /// <summary>Whether entering the world waits: for the note, and while the check, download or switch runs.</summary>
    public bool HoldsEnter => State is WorldState.Waiting or WorldState.Checking or WorldState.Downloading or WorldState.Switching;

    /// <summary>The world cached for the server the game was launched for, checked and indexed, to switch on before the client opens its
    /// dats; null when there is none or it doesn't check out.</summary>
    public WorldPatchIndex? AtStartup()
    {
        if (_server == null)
        {
            ClientUiRuntime.Log("World: the launch line names no server (-h), so no world is switched on at startup.");
            return null;
        }
        if (_cache.ForServer(_server) is not { } world) return null;
        if (_cache.IsUnconfirmed(_server))
        {
            // The last start with it on never heard the server's note: most likely kicked at connect for a world the server dropped, or
            // changed. The mark stays until a note clears it, so the game keeps starting retail for this server until then.
            ClientUiRuntime.Log($"World: {world.Name} isn't switched on at startup: the last game started with it never heard this server's note. "
                + "A note at character select switches it on again.");
            return null;
        }
        if (_cache.Open(world, ClientUiRuntime.Log) is not { } patch) return null;
        _cache.Unconfirmed(_server, true);
        (Active, ActivePatch) = (world, patch);
        ClientUiRuntime.Log($"World: {world.Name} (world {world.Edition}) is cached for this server; it is switched on at startup.");
        return patch;
    }

    /// <summary>
    /// The server's note: starts the check once; later calls return the same task. The task never fails. A note that comes after the end of
    /// the DDD exchange (it travels on another queue, so it can) still counts: it overrides the Off that end left.
    /// </summary>
    public Task Heard(WorldNote note)
    {
        var state = State;
        if (Server != null || state is not (WorldState.Waiting or WorldState.Off)
            || Interlocked.CompareExchange(ref _state, (int)WorldState.Checking, (int)state) != (int)state)
            return _running;
        Server = note;
        if (state == WorldState.Off) ClientUiRuntime.Log("World: the server's note came after the end of the DDD exchange; it counts.");
        if (_server != null) _cache.Unconfirmed(_server, false);
        ClientUiRuntime.Log($"World: the server runs {note.Name}, edition {note.Edition}, update list {note.UpdateList}.");
        return _running = Task.Run(() => Run(note));
    }

    /// <summary>
    /// The DDD exchange ended without a note: a plain server, so the overlay goes off (<see cref="Active"/> null), unless a late note still
    /// comes (<see cref="Heard"/>). A world switched on at startup keeps its unconfirmed mark, so the next start is retail for this server
    /// until a note clears it. Only the first end of a connection counts: after a note, or after this, it's ignored.
    /// </summary>
    public void NoNote()
    {
        if (Interlocked.CompareExchange(ref _state, (int)WorldState.Off, (int)WorldState.Waiting) != (int)WorldState.Waiting) return;
        ClientUiRuntime.Log(Active == null ? "World: Off. The server sent no world note; the game reads its retail dats."
            : $"World: Off. The server sent no world note, so {Active.Name} is switched off.");
        (Active, ActivePatch) = (null, null);
    }

    /// <summary>
    /// The game thread switched the pending patch on, and sent the dat report again (<paramref name="reported"/>) or couldn't, so the server
    /// sees the world at the next launch.
    /// </summary>
    public void SwitchedOn(bool reported)
    {
        if (State != WorldState.Switching || _pending is not { } pending) return;
        _pending = null;
        PortalNeedsRelaunch = HeldChanged(ActivePatch, pending.Patch);
        (Active, ActivePatch) = (pending.World, pending.Patch);
        Set(reported ? WorldState.On : WorldState.Relaunch);
    }

    /// <summary>Whether a held record differs between <paramref name="old"/> and <paramref name="next"/>: added, changed, or dropped back to
    /// retail.</summary>
    private static bool HeldChanged(WorldPatchIndex? old, WorldPatchIndex next) =>
        next.Dats.Union(old?.Dats ?? []).Any(dat =>
        {
            var (before, after) = (old?.Records(dat), next.Records(dat));
            return (before?.Keys ?? []).Union(after?.Keys ?? []).Any(id => WorldPatch.HeldAtSwitchOn(dat, id)
                && (before == null || after == null || !before.TryGetValue(id, out var was) || !after.TryGetValue(id, out var now)
                    || !old!.Read(was).AsSpan().SequenceEqual(next.Read(now))));
        });

    /// <summary>https anywhere; plain http only to this machine or a private network, for local testing.</summary>
    public static bool Allowed(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || Private(uri))));

    private static bool Private(Uri uri)
    {
        if (!IPAddress.TryParse(uri.Host, out var ip)) return false;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>An address <see cref="Allowed"/> refuses ends the check in Unavailable.</summary>
    private bool Refused(string address)
    {
        if (Allowed(address)) return false;
        ClientUiRuntime.Log($"World address {address} refused: only https, or http to this machine or a private network.");
        Set(WorldState.Unavailable);
        return true;
    }

    private async Task Run(WorldNote note)
    {
        try
        {
            // Only the server's own edition: a newer world (the server rolled its world back) would be kicked as newer dats.
            if (Active?.Address == note.UpdateList && Active.Edition == note.Edition)
            {
                if (_server != null) _cache.Remember(_server, note.UpdateList);
                Set(WorldState.UpToDate);
                return;
            }
            var cached = _cache.Find(note.UpdateList);
            if (cached != null && cached.Edition == note.Edition && Switch(cached, "is cached")) return;
            if (Refused(note.UpdateList)) return;
            WorldManifest manifest;
            using (var deadline = new CancellationTokenSource(ManifestDeadline))
                manifest = WorldManifest.Parse(await _getString(note.UpdateList, deadline.Token));
            ClientUiRuntime.Log($"World check: cached {(cached == null ? "none" : $"world {cached.Edition}")}; {note.UpdateList} has world {manifest.Version}.");
            if (manifest.Version < note.Edition)
                ClientUiRuntime.Log($"The update list's world {manifest.Version} is behind the server's edition {note.Edition}; the server may still refuse Enter.");
            if (cached != null && manifest.Version == cached.Edition && Switch(cached, "is the newest there is")) return;
            if (Refused(manifest.Patch)) return;
            Set(WorldState.Downloading);
            for (var attempt = 1; attempt <= 2; attempt++)
                if (await Download(note, manifest) is { } world && Switch(world, "was downloaded"))
                    return;
            ClientUiRuntime.Log($"World {manifest.Version} not downloaded: two tries failed. Enter is left to the server; the next launch tries again.");
        }
        catch (Exception exception) { ClientUiRuntime.Log($"World check failed; Enter is left to the server: {exception.Message}"); }
        Set(WorldState.Unavailable);
    }

    /// <summary>Opens a cached world's patch and waits in Switching for the game thread to switch it on; false when it doesn't check out.</summary>
    private bool Switch(CachedWorld world, string why)
    {
        if (_cache.Open(world, ClientUiRuntime.Log) is not { } patch) return false;
        if (_server != null) _cache.Remember(_server, world.Address);
        _pending = (patch, world);
        ClientUiRuntime.Log($"World {world.Name} (world {world.Edition}) {why}; switching it on.");
        Set(WorldState.Switching);
        return true;
    }

    /// <summary>
    /// One try: downloads into a file of this client's own, checks its size and SHA-256, and only then stores it in the cache. Null, with the
    /// download deleted, when it fails or doesn't match.
    /// </summary>
    private async Task<CachedWorld?> Download(WorldNote note, WorldManifest manifest)
    {
        var part = _cache.PartFile(note.UpdateList);
        try
        {
            Volatile.Write(ref _percent, 0);
            using (var deadline = new CancellationTokenSource(DownloadDeadline))
            await using (var file = File.Create(part))
                await _download(manifest.Patch, file,
                    bytes => Volatile.Write(ref _percent, manifest.Size > 0 ? (int)Math.Min(99, bytes * 100 / manifest.Size) : 0), deadline.Token);
            var size = new FileInfo(part).Length;
            var sha = WorldPatch.Sha256(part);
            if (size == manifest.Size && sha.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var world = _cache.Store(note.UpdateList, note.Name, manifest.Version, sha, part, _server);
                Volatile.Write(ref _percent, 100);
                ClientUiRuntime.Log($"World {manifest.Version} downloaded and verified ({size} bytes).");
                return world;
            }
            ClientUiRuntime.Log($"World {manifest.Version} download thrown away: {size} bytes, sha256 {sha}; the manifest says {manifest.Size}, {manifest.Sha256}.");
        }
        catch (Exception exception) { ClientUiRuntime.Log($"World {manifest.Version} download failed: {exception.Message}"); }
        try { File.Delete(part); }
        catch (Exception) { }
        return null;
    }

    private void Set(WorldState state)
    {
        Volatile.Write(ref _state, (int)state);
        ClientUiRuntime.Log($"World: {state}.");
    }

    private static async Task Fetch(string url, Stream file, Action<long> progress, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, cancel)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancel);
            progress(total += read);
        }
    }
}
