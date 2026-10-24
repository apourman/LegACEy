using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LegACEy.Client.Demo;
using LegACEy.Updater;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Where a world update stands. Waiting (for the server's note), Checking, Downloading, Ready, Closing and OtherWorld hold Enter at
/// character select; Off (a plain server: no note), UpToDate, Unavailable (the update list or patch couldn't be had, or the updater
/// couldn't start), InstallFailed (this patch failed to apply before) and HandedOff (the updater waits, but the game isn't closing for it)
/// leave it alone.
/// </summary>
internal enum WorldState { Waiting, Off, Checking, UpToDate, Downloading, Ready, Closing, HandedOff, Unavailable, OtherWorld, InstallFailed }

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
/// Keeps the game folder's dats at the world of the server logged into. Enter waits from the plugin's start at character select until the
/// server's note (<see cref="Heard"/>) or the end of the DDD exchange without one (<see cref="NoNote"/>, a plain server: the folder is
/// never touched). With a note, off the game thread: a folder holding another world's address is left alone and Enter stays held; one at
/// the note's edition is up to date; otherwise it reads the note's update list and downloads the patch into
/// <c>world-update\&lt;version&gt;\</c>, checks its size and SHA-256 (a bad download is thrown away and tried once more), then says Ready.
/// <see cref="HandOff"/> then starts the updater, which applies the patch once every game client has exited. Any failure is logged and
/// ends in Unavailable, or InstallFailed for a patch that failed to apply; neither holds Enter: the server decides then.
/// </summary>
internal sealed class WorldUpdates
{
    private const string UpdaterName = "LegACEy.Updater.exe";
    // Short: Enter is held while the manifest is fetched.
    private static readonly TimeSpan ManifestDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadDeadline = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestHeaders = { { "User-Agent", "LegACEy-decal-plugin" } } };

    private readonly string _game;
    private readonly Func<string, CancellationToken, Task<string>> _getString;
    private readonly Func<string, Stream, Action<long>, CancellationToken, Task> _download;
    private int _state;
    private int _percent;
    private Task _running = Task.CompletedTask;

    /// <param name="getString">Fetches the manifest; null for HTTP.</param>
    /// <param name="download">Writes the patch at an address into a stream, reporting the bytes written so far; null for HTTP.</param>
    public WorldUpdates(string game, Func<string, CancellationToken, Task<string>>? getString = null,
        Func<string, Stream, Action<long>, CancellationToken, Task>? download = null)
    {
        _game = game;
        _getString = getString ?? ((url, cancel) => Http.GetStringAsync(url, cancel));
        _download = download ?? Fetch;
    }

    public WorldState State => (WorldState)Volatile.Read(ref _state);

    /// <summary>The download's progress, 0–100.</summary>
    public int Percent => Volatile.Read(ref _percent);

    /// <summary>The manifest of the update being downloaded, ready or that failed to install; null until the check finds the folder behind.</summary>
    public WorldManifest? Target { get; private set; }

    /// <summary>The server's world, once its note came.</summary>
    public WorldNote? Server { get; private set; }

    /// <summary>The world this folder holds; read when the note comes.</summary>
    public WorldFolder? Folder { get; private set; }

    /// <summary>Whether entering the world waits: for the note, while the check, download or hand-off runs, and for good when the folder
    /// holds another world.</summary>
    public bool HoldsEnter => State is WorldState.Waiting or WorldState.Checking or WorldState.Downloading or WorldState.Ready or WorldState.Closing
        or WorldState.OtherWorld;

    /// <summary>The server's note: starts the check once; later calls return the same task. The task never fails.</summary>
    public Task Heard(WorldNote note)
    {
        if (Interlocked.CompareExchange(ref _state, (int)WorldState.Checking, (int)WorldState.Waiting) != (int)WorldState.Waiting)
            return _running;
        Server = note;
        ClientUiRuntime.Log($"World update: the server runs {note.Name}, edition {note.Edition}, update list {note.UpdateList}.");
        return _running = Task.Run(() => Run(note));
    }

    /// <summary>The DDD exchange ended without a note: a plain server, so world updates are off and the folder is left alone.</summary>
    public void NoNote()
    {
        if (Interlocked.CompareExchange(ref _state, (int)WorldState.Off, (int)WorldState.Waiting) == (int)WorldState.Waiting)
            ClientUiRuntime.Log("World update: Off. The server sent no world note, so this game folder is left alone.");
    }

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
        ClientUiRuntime.Log($"World update address {address} refused: only https, or http to this machine or a private network.");
        Set(WorldState.Unavailable);
        return true;
    }

    private async Task Run(WorldNote note)
    {
        try
        {
            var folder = Folder = WorldFiles.ReadWorld(_game);
            if (folder.Address != null && folder.Address != note.UpdateList)
            {
                ClientUiRuntime.Log($"World update: this game folder holds {folder.Name ?? folder.Address} ({folder.Address}), not {note.Name}. "
                    + "Enter stays held; the folder is left alone.");
                Set(WorldState.OtherWorld);
                return;
            }
            if (folder.Address != null && folder.Version >= note.Edition)
            {
                RemoveOldStages(folder.Version);
                Set(WorldState.UpToDate);
                return;
            }
            if (Refused(note.UpdateList)) return;
            WorldManifest manifest;
            using (var deadline = new CancellationTokenSource(ManifestDeadline))
                manifest = WorldManifest.Parse(await _getString(note.UpdateList, deadline.Token));
            ClientUiRuntime.Log($"World update check: this game is at world {folder.Version}; {note.UpdateList} has world {manifest.Version}.");
            if (manifest.Version < note.Edition)
                ClientUiRuntime.Log($"The update list's world {manifest.Version} is behind the server's edition {note.Edition}; the server may still refuse Enter.");
            if (manifest.Version <= folder.Version)
            {
                RemoveOldStages(folder.Version);
                Set(WorldState.UpToDate);
                return;
            }
            if (Refused(manifest.Patch)) return;
            Target = manifest;
            var stage = WorldFiles.Stage(_game, manifest.Version);
            var failed = Path.Combine(stage, WorldFiles.FailedFile);
            if (File.Exists(failed))
            {
                // The same patch would fail again and close the game for nothing; a new one (another SHA-256) or a damaged copy is downloaded.
                if (Staged(stage, manifest))
                {
                    ClientUiRuntime.Log($"World update {manifest.Version} failed to apply: {File.ReadAllText(failed)}. Enter is open; it isn't tried again "
                        + "until the manifest names a different patch.");
                    Set(WorldState.InstallFailed);
                    return;
                }
                ClientUiRuntime.Log($"World update {manifest.Version} failed to apply before; the manifest names a different patch or the copy here is damaged, so it is downloaded.");
                Directory.Delete(stage, true);
            }
            // Names the world the patch belongs to, for the updater to record beside the dats.
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, WorldFiles.VersionFile), WorldFiles.WorldJson(manifest.Version, note.UpdateList, note.Name));
            if (Staged(stage, manifest))
            {
                ClientUiRuntime.Log($"World update {manifest.Version} is already downloaded.");
                Volatile.Write(ref _percent, 100);
                Set(WorldState.Ready);
                return;
            }
            Set(WorldState.Downloading);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (await Download(manifest, stage))
                {
                    Set(WorldState.Ready);
                    return;
                }
            }
            ClientUiRuntime.Log($"World update {manifest.Version} not downloaded: two tries failed. Enter is open; the next launch tries again.");
        }
        catch (Exception exception) { ClientUiRuntime.Log($"World update check failed; Enter is open: {exception.Message}"); }
        Set(WorldState.Unavailable);
    }

    /// <summary>
    /// One try: downloads into a file of this client's own, so two clients never write the same one, checks its size and SHA-256, and only
    /// then moves it into place and writes <c>ready</c> with the checksum. False, with the download deleted, when it fails or doesn't match.
    /// </summary>
    private async Task<bool> Download(WorldManifest manifest, string stage)
    {
        var part = Path.Combine(stage, $"{WorldFiles.PatchFile}.{Environment.ProcessId}.part");
        try
        {
            Directory.CreateDirectory(stage);
            Volatile.Write(ref _percent, 0);
            using (var deadline = new CancellationTokenSource(DownloadDeadline))
            await using (var file = File.Create(part))
                await _download(manifest.Patch, file,
                    bytes => Volatile.Write(ref _percent, manifest.Size > 0 ? (int)Math.Min(99, bytes * 100 / manifest.Size) : 0), deadline.Token);
            var size = new FileInfo(part).Length;
            var sha = WorldFiles.Sha256(part);
            if (size == manifest.Size && sha.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(part, Path.Combine(stage, WorldFiles.PatchFile), overwrite: true);
                File.WriteAllText(Path.Combine(stage, WorldFiles.ReadyFile), sha);
                Volatile.Write(ref _percent, 100);
                ClientUiRuntime.Log($"World update {manifest.Version} downloaded and verified ({size} bytes).");
                return true;
            }
            ClientUiRuntime.Log($"World update {manifest.Version} download thrown away: {size} bytes, sha256 {sha}; the manifest says {manifest.Size}, {manifest.Sha256}.");
        }
        catch (Exception exception) { ClientUiRuntime.Log($"World update {manifest.Version} download failed: {exception.Message}"); }
        try { File.Delete(part); }
        catch (Exception) { }
        return false;
    }

    /// <summary>Whether a client has already downloaded and verified this update.</summary>
    private static bool Staged(string stage, WorldManifest manifest)
    {
        var ready = Path.Combine(stage, WorldFiles.ReadyFile);
        var patch = Path.Combine(stage, WorldFiles.PatchFile);
        try { return File.Exists(ready) && File.Exists(patch) && WorldFiles.Sha256(patch).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Ready: starts a copy of the install's updater from the stage, detached, to apply the patch once every game client has exited, and says
    /// Closing. False, and Unavailable so Enter opens, when the updater can't be started. A second client's updater finds the first one waiting
    /// and leaves the install to it.
    /// </summary>
    public bool HandOff(string install)
    {
        if (State != WorldState.Ready || Target is not { } target) return false;
        var stage = WorldFiles.Stage(_game, target.Version);
        try
        {
            var waiting = Mutex.TryOpenExisting(WorldFiles.UpdaterMutex, out var held);
            held?.Dispose();
            if (waiting)
            {
                ClientUiRuntime.Log("Another client's world updater is already waiting to apply the world update.");
                Set(WorldState.Closing);
                return true;
            }
            var updater = Path.Combine(stage, UpdaterName);
            File.Copy(Path.Combine(install, UpdaterName), updater, overwrite: true);
            var start = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
            foreach (var argument in new[] { "world", install, _game, stage }) start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
            ClientUiRuntime.Log($"Started the world updater for {stage}.");
            Set(WorldState.Closing);
            return true;
        }
        catch (Exception exception)
        {
            ClientUiRuntime.Log($"Could not start the world updater; Enter is open: {exception.Message}");
            Set(WorldState.Unavailable);
            return false;
        }
    }

    /// <summary>The game won't close for the handed-off update (the character is in the world, or the close failed): Enter opens, and it
    /// installs whenever the game does close.</summary>
    public void StayOpen()
    {
        if (State == WorldState.Closing) Set(WorldState.HandedOff);
    }

    /// <summary>Removes the stages of updates this folder already has: what an updater left after applying (its exe).</summary>
    private void RemoveOldStages(int local)
    {
        var folder = Path.Combine(_game, WorldFiles.UpdateFolder);
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var stage in Directory.GetDirectories(folder))
                if (int.TryParse(Path.GetFileName(stage), out var version) && version <= local) Directory.Delete(stage, true);
        }
        catch (Exception exception) { ClientUiRuntime.Log($"Could not remove an old world update folder: {exception.Message}"); }
    }

    private void Set(WorldState state)
    {
        Volatile.Write(ref _state, (int)state);
        ClientUiRuntime.Log($"World update: {state}.");
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
