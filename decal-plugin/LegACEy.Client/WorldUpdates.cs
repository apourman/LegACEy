using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LegACEy.Updater;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Where a world update stands. Checking, Downloading, Ready and Closing hold Enter at character select; Off (no manifest address),
/// UpToDate, Unavailable (the manifest or patch couldn't be had, or the updater couldn't start), InstallFailed (this patch failed to apply
/// before) and HandedOff (the updater waits, but the game isn't closing for it) leave it alone.
/// </summary>
internal enum WorldState { Off, Checking, UpToDate, Downloading, Ready, Closing, HandedOff, Unavailable, InstallFailed }

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
/// Keeps the game folder's dats at the world version the manifest asks for. Started once, when the plugin starts at character select: off
/// the game thread it reads the manifest, and when the folder is behind it downloads the patch into <c>world-update\&lt;version&gt;\</c> and
/// checks its size and SHA-256 (a bad download is thrown away and tried once more), then says Ready. <see cref="HandOff"/> then starts the
/// updater, which applies the patch once every game client has exited. Any failure is logged and ends in Unavailable (or InstallFailed), which never holds
/// Enter: a down update host locks nobody out. The manifest address is the first line of <c>world-manifest-url</c> beside the stub; with no
/// such file world updates are off.
/// </summary>
internal sealed class WorldUpdates
{
    public const string ManifestUrlFile = "world-manifest-url";
    private const string UpdaterName = "LegACEy.Updater.exe";
    // Short: Enter is held while the manifest is fetched.
    private static readonly TimeSpan ManifestDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadDeadline = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestHeaders = { { "User-Agent", "LegACEy-decal-plugin" } } };

    private readonly string _game;
    private readonly string? _manifestUrl;
    private readonly Func<string, CancellationToken, Task<string>> _getString;
    private readonly Func<string, Stream, Action<long>, CancellationToken, Task> _download;
    private int _state;
    private int _percent;
    private Task _running = Task.CompletedTask;

    /// <param name="getString">Fetches the manifest; null for HTTP.</param>
    /// <param name="download">Writes the patch at an address into a stream, reporting the bytes written so far; null for HTTP.</param>
    public WorldUpdates(string game, string? manifestUrl, Func<string, CancellationToken, Task<string>>? getString = null,
        Func<string, Stream, Action<long>, CancellationToken, Task>? download = null)
    {
        _game = game;
        _manifestUrl = manifestUrl;
        _getString = getString ?? ((url, cancel) => Http.GetStringAsync(url, cancel));
        _download = download ?? Fetch;
    }

    public WorldState State => (WorldState)Volatile.Read(ref _state);

    /// <summary>The download's progress, 0–100.</summary>
    public int Percent => Volatile.Read(ref _percent);

    /// <summary>The manifest of the update being downloaded or ready; null until the check finds the folder behind.</summary>
    public WorldManifest? Target { get; private set; }

    /// <summary>Whether entering the world waits: while the check, download or hand-off runs.</summary>
    public bool HoldsEnter => State is WorldState.Checking or WorldState.Downloading or WorldState.Ready or WorldState.Closing;

    /// <summary>The manifest address in <c>world-manifest-url</c> beside the stub, or null when world updates are off.</summary>
    public static string? ManifestUrl(string install)
    {
        try
        {
            var path = Path.Combine(install, ManifestUrlFile);
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
                if (line.Trim() is { Length: > 0 } url) return url;
        }
        catch (Exception exception) { ClientUiRuntime.Log($"Could not read {ManifestUrlFile}: {exception.Message}"); }
        return null;
    }

    /// <summary>Starts the check once; later calls return the same task. The task never fails.</summary>
    public Task Start()
    {
        if (_manifestUrl == null || Interlocked.CompareExchange(ref _state, (int)WorldState.Checking, (int)WorldState.Off) != (int)WorldState.Off)
            return _running;
        return _running = Task.Run(Run);
    }

    private async Task Run()
    {
        try
        {
            WorldManifest manifest;
            using (var deadline = new CancellationTokenSource(ManifestDeadline))
                manifest = WorldManifest.Parse(await _getString(_manifestUrl!, deadline.Token));
            var local = WorldFiles.ReadVersion(_game);
            ClientUiRuntime.Log($"World update check: this game is at world {local}; {_manifestUrl} has world {manifest.Version}.");
            if (manifest.Version <= local)
            {
                RemoveOldStages(local);
                Set(WorldState.UpToDate);
                return;
            }
            Target = manifest;
            var stage = WorldFiles.Stage(_game, manifest.Version);
            var failed = Path.Combine(stage, WorldFiles.FailedFile);
            if (File.Exists(failed))
            {
                // The same patch would fail again and close the game for nothing; a new one (another SHA-256) is downloaded.
                if (SameSha(Path.Combine(stage, WorldFiles.ReadyFile), manifest))
                {
                    ClientUiRuntime.Log($"World update {manifest.Version} failed to apply: {File.ReadAllText(failed)}. Enter is open; it isn't tried again "
                        + "until the manifest names a different patch.");
                    Set(WorldState.InstallFailed);
                    return;
                }
                ClientUiRuntime.Log($"World update {manifest.Version} failed to apply before; the manifest now names a different patch, so it is downloaded.");
                Directory.Delete(stage, true);
            }
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

    /// <summary>Whether the <c>ready</c> file at <paramref name="ready"/> holds the manifest's SHA-256.</summary>
    private static bool SameSha(string ready, WorldManifest manifest)
    {
        try { return File.Exists(ready) && File.ReadAllText(ready).Trim().Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
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
