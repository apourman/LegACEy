using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LegACEy.Client.DecalPlugin;

/// <summary>A published plugin release: its version and the URLs of its zip and checksums.</summary>
internal sealed record PluginRelease(Version Version, string ZipUrl, string SumsUrl);

/// <summary>
/// What the updater has to tell the player. Ready is a newer release found, not yet downloaded; Downloaded is one the player chose to install,
/// downloaded with the updater waiting for the game to close. Wait covers a press too soon, a check already running and another client checking.
/// </summary>
internal enum UpdateNoticeKind { Checking, Ready, Downloaded, UpToDate, Wait, Failed }

/// <summary>
/// One thing the updater has to tell the player: its kind, its chat line (null for none), the version it is about, and for Ready whether
/// more than one game client is running, when closing this one installs nothing.
/// </summary>
internal sealed record UpdateNotice(UpdateNoticeKind Kind, string? Chat, Version? Version = null, bool SeveralClients = false);

/// <summary>
/// Offers an installed plugin its updates. At login and every 30 minutes, off the game thread, it looks for a newer plugin release on GitHub and
/// says so; nothing installs until the player chooses to. <see cref="Install"/> then stages it in <c>update\&lt;version&gt;\</c> beside the stub
/// and starts the updater copied into that stage, which applies it once every game client has exited. Only an install made by setup.exe updates (its <c>install.json</c> sits beside the stub); a
/// <c>no-auto-update</c> file beside the stub turns it off. Every failure is logged and ignored.
/// </summary>
internal sealed class PluginUpdates : IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/apourman/LegACEy/releases?per_page=100";
    private const string SumsName = "SHA256SUMS.txt";
    private const string UpdaterName = "LegACEy.Updater.exe";
    private const string UpdaterMutex = @"Local\LegACEy.Updater";
    // The Win32 errors (in an IOException's HResult) for a file another process holds open: here, the update-check stamp.
    private const int SharingViolation = 32, LockViolation = 33;
    // Linux's EWOULDBLOCK, the bare HResult .NET gives the same held file there, where the tests run.
    private const int WouldBlock = 11;
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    // Every client of the install shares one check time, so several clients logging in together ask GitHub once (60 unauthenticated requests
    // per hour per IP). Short, so a login a few minutes after the last check still asks: the toast is the only news of a release.
    private static readonly TimeSpan SharedGap = TimeSpan.FromMinutes(5);
    // A whole check, download included, gives up after this, so a stalled connection never keeps the next check from starting.
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);
    // The menu's Check for updates button, per client process. It skips the shared gap, so a player pressing it every minute uses
    // GitHub's whole hourly allowance for their IP.
    private static readonly TimeSpan PressGap = TimeSpan.FromMinutes(1);
    private static readonly Regex PluginTag = new(@"^decal-plugin-v(\d+\.\d+\.\d+)$", RegexOptions.Compiled);
    // GitHub's API refuses requests without a User-Agent.
    private static readonly HttpClient Http = new() { DefaultRequestHeaders = { { "User-Agent", "LegACEy-decal-plugin" } } };
    private static readonly UpdateNotice Failed = new(UpdateNoticeKind.Failed, "Couldn't check for updates: try again later.");

    private readonly string _install;
    private readonly Func<CancellationToken, Task<string>> _releases;
    private readonly ConcurrentQueue<UpdateNotice> _notices = new();
    private readonly HashSet<Version> _announced = new();
    // The releases checks found, by version, for Install to download.
    private readonly ConcurrentDictionary<Version, PluginRelease> _found = new();
    private Timer? _timer;
    private int _checking;
    // The check or download running, or the last one: a download asked for during a check starts once it ends.
    private Task _running = Task.CompletedTask;
    private DateTime _lastPress;
    // Set once a press is told to wait in chat, so pressing again while a check runs adds no more chat lines. Game thread only.
    private bool _toldToWait;

    /// <param name="releases">GitHub's release list; null for the real one.</param>
    public PluginUpdates(string install, Func<CancellationToken, Task<string>>? releases = null)
    {
        _install = install;
        _releases = releases ?? (cancel => Http.GetStringAsync(ReleasesUrl, cancel));
    }

    public static string ZipName(Version version) => $"LegACEy-decal-plugin-v{version}-windows-x86.zip";

    /// <summary>The GitHub page with a plugin release's notes.</summary>
    public static string ReleasePage(Version version) => $"https://github.com/apourman/LegACEy/releases/tag/decal-plugin-v{version}";

    /// <summary>
    /// The highest published plugin release in GitHub's release list, or null when none is newer than <paramref name="current"/>.
    /// The server's releases share the repository, so only <c>decal-plugin-vX.Y.Z</c> tags count; drafts and prereleases never do.
    /// </summary>
    public static PluginRelease? Newest(string releasesJson, Version current)
    {
        using var releases = JsonDocument.Parse(releasesJson);
        var newest = releases.RootElement.EnumerateArray()
            .Where(release => !release.GetProperty("draft").GetBoolean() && !release.GetProperty("prerelease").GetBoolean())
            .Select(release => (release, match: PluginTag.Match(release.GetProperty("tag_name").GetString() ?? "")))
            .Where(tagged => tagged.match.Success)
            .Select(tagged => (tagged.release, version: Version.Parse(tagged.match.Groups[1].Value)))
            .OrderByDescending(tagged => tagged.version)
            .FirstOrDefault();
        if (newest.version is not { } version || version <= Plain(current)) return null;
        string? Asset(string name) => newest.release.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(asset => asset.GetProperty("name").GetString() == name) is { ValueKind: JsonValueKind.Object } asset
            ? asset.GetProperty("browser_download_url").GetString() : null;
        return Asset(ZipName(version)) is { } zip && Asset(SumsName) is { } sums ? new PluginRelease(version, zip, sums) : null;
    }

    /// <summary>
    /// Stages the release zip downloaded into <paramref name="folder"/> with its <c>SHA256SUMS.txt</c>: only a zip whose SHA-256 matches is
    /// unpacked to <c>files\</c>, and <c>ready</c> is written last. False, with nothing staged, when the checksum does not match.
    /// </summary>
    public static bool Stage(string folder, Version version)
    {
        var zip = Path.Combine(folder, ZipName(version));
        var expected = File.ReadLines(Path.Combine(folder, SumsName))
            .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(sum => sum.Length == 2 && sum[1].TrimStart('*') == ZipName(version))?[0];
        string actual;
        using (var stream = File.OpenRead(zip)) actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        var files = Path.Combine(folder, "files");
        if (Directory.Exists(files)) Directory.Delete(files, true);
        ZipFile.ExtractToDirectory(zip, files);
        File.WriteAllText(Path.Combine(folder, "ready"), "");
        return true;
    }

    /// <summary>The installed plugin's version, X.Y.Z.</summary>
    public Version Installed => Plain(typeof(PluginUpdates).Assembly.GetName().Version ?? new Version());

    /// <summary>Why this install never offers updates (a checkout build, or <c>no-auto-update</c>), or null when it does.</summary>
    public string? Off =>
        !File.Exists(Path.Combine(_install, "install.json")) ? "checkout build"
        : File.Exists(Path.Combine(_install, "no-auto-update")) ? "updates off"
        : null;

    /// <summary>
    /// Starts a check off the game thread unless one is running, and from the first call on, one every 30 minutes. The task ends with the
    /// check and never fails.
    /// </summary>
    public Task Check()
    {
        _timer ??= new Timer(_ => Check(), null, Every, Every);
        return Start(force: false) ?? Task.CompletedTask;
    }

    /// <summary>
    /// The menu's Check for updates: checks now, past the shared gap, at most once a minute, and says each step: Checking, then the outcome.
    /// A press too soon is told to wait every time, in chat only the first time. Nothing installs unless the player then chooses to.
    /// </summary>
    public Task CheckNow()
    {
        if (Off != null) return Task.CompletedTask;
        Task? check = null;
        var wait = DateTime.UtcNow - _lastPress < PressGap ? "LegACEy checked for updates less than a minute ago: try again shortly."
            : (check = Start(force: true)) == null ? "LegACEy is already checking for updates."
            : null;
        if (wait != null)
        {
            _notices.Enqueue(new UpdateNotice(UpdateNoticeKind.Wait, _toldToWait ? null : wait));
            _toldToWait = true;
            return Task.CompletedTask;
        }
        _lastPress = DateTime.UtcNow;
        _toldToWait = false;
        return check!;
    }

    /// <summary>
    /// The player chose to install <paramref name="version"/>, a release a check found: downloads and verifies it off the game thread, starts
    /// the updater, and says Downloaded, or Failed with the version when it can't. Asked during a check or another download, it starts once
    /// that ends.
    /// </summary>
    public Task Install(Version version)
    {
        var failed = new UpdateNotice(UpdateNoticeKind.Failed, null, version);
        if (Off != null || !_found.TryGetValue(version, out var release))
        {
            _notices.Enqueue(failed);
            return Task.CompletedTask;
        }
        return Begin(cancel => Fetch(release, cancel), failed) ?? Volatile.Read(ref _running).ContinueWith(_ => Install(version)).Unwrap();
    }

    private async Task Fetch(PluginRelease release, CancellationToken cancel)
    {
        var update = Path.Combine(_install, "update");
        if (!await Download(release, update, cancel)) throw new InvalidDataException($"{ZipName(release.Version)} does not match {SumsName}.");
        var updaterRunning = Mutex.TryOpenExisting(UpdaterMutex, out var held);
        held?.Dispose();
        if (!updaterRunning) StartUpdater(Path.Combine(update, release.Version.ToString()));
        _notices.Enqueue(new UpdateNotice(UpdateNoticeKind.Downloaded, $"LegACEy {release.Version} is downloaded and installs when the game closes.",
            release.Version, SeveralClients()));
    }

    /// <summary>Runs one check off the game thread; null when one is already running. A forced check says Checking first.</summary>
    private Task? Start(bool force)
    {
        return Begin(cancel => Run(force, cancel), force ? Failed : null, force ? new UpdateNotice(UpdateNoticeKind.Checking, null) : null);
    }

    /// <summary>
    /// Runs a check or a download off the game thread, one at a time; null when one is already running. <paramref name="started"/> is said
    /// before it starts, so no outcome can come ahead of it; a failure says <paramref name="failed"/>.
    /// </summary>
    private Task? Begin(Func<CancellationToken, Task> work, UpdateNotice? failed, UpdateNotice? started = null)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return null;
        if (started != null) _notices.Enqueue(started);
        var running = Task.Run(async () =>
        {
            try
            {
                using var deadline = new CancellationTokenSource(Deadline);
                await work(deadline.Token);
            }
            catch (Exception exception)
            {
                ClientUiRuntime.Log($"Plugin update failed: {exception.Message}");
                if (failed != null) _notices.Enqueue(failed);
            }
            finally { Volatile.Write(ref _checking, 0); }
        });
        Volatile.Write(ref _running, running);
        return running;
    }

    /// <summary>
    /// Hands each notice, oldest first, to <paramref name="post"/>; one it throws on stays queued for the next frame. Game thread only, while
    /// a character is in the world.
    /// </summary>
    public void PostNotices(Action<UpdateNotice> post)
    {
        while (_notices.TryPeek(out var notice))
        {
            post(notice);
            _notices.TryDequeue(out _);
        }
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>One check. Forced, from the menu, it skips the shared gap and always says the outcome. It downloads nothing.</summary>
    private async Task Run(bool force, CancellationToken cancel)
    {
        if (Off != null) return;
        var current = AssemblyName.GetAssemblyName(Path.Combine(_install, "LegACEy.Client.DecalPlugin.dll")).Version ?? new Version();
        var update = Path.Combine(_install, "update");
        var updaterRunning = Mutex.TryOpenExisting(UpdaterMutex, out var held);
        held?.Dispose();

        // The stamp holds the last check's time and the release it found, so every client on the install offers it, not only the one that
        // asked GitHub. Held open unshared, it makes this the one client that checks and cleans up; while another client holds it, a
        // background check here says nothing.
        FileStream? slot = null;
        PluginRelease? release = null;
        try { slot = new FileStream(Path.Combine(_install, "update-check"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is SharingViolation or LockViolation || exception.HResult == WouldBlock) { }
        if (slot == null && force)
        {
            _notices.Enqueue(new UpdateNotice(UpdateNoticeKind.Wait, "Another LegACEy client on this PC is checking for updates right now."));
            return;
        }
        if (slot != null)
            await using (slot)
            {
                var stamp = new StreamReader(slot, leaveOpen: true).ReadToEnd().Split('\n');
                var last = DateTime.TryParse(stamp[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : DateTime.MinValue;
                if (force || DateTime.UtcNow - last >= SharedGap)
                {
                    if (!updaterRunning) RemoveStale(update, current);
                    release = Newest(await _releases(cancel), current);
                    slot.SetLength(0);
                    slot.Write(Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O") +
                        (release == null ? "" : $"\n{release.Version} {release.ZipUrl} {release.SumsUrl}")));
                    slot.Flush();
                }
                else if (stamp.Length > 1 && stamp[1].Split(' ') is [var version, var zip, var sums] && Version.TryParse(version, out var found)
                         && found > Plain(current))
                    release = new PluginRelease(found, zip, sums);
            }

        if (release == null)
        {
            if (force) _notices.Enqueue(new UpdateNotice(UpdateNoticeKind.UpToDate, $"LegACEy {Plain(current)} is up to date.", Plain(current)));
            return;
        }
        _found[release.Version] = release;
        if (_announced.Add(release.Version) || force)
            _notices.Enqueue(new UpdateNotice(UpdateNoticeKind.Ready,
                $"LegACEy {release.Version} is available. Check for updates in Settings offers it again.", release.Version, SeveralClients()));
    }

    /// <summary>Whether more than one game client is running, counted as the updater counts them: it installs once none is.</summary>
    private static bool SeveralClients()
    {
        var clients = Process.GetProcessesByName("acclient");
        foreach (var client in clients) client.Dispose();
        return clients.Length > 1;
    }

    /// <summary>Downloads and stages the release; false when its zip does not match its checksum.</summary>
    private static async Task<bool> Download(PluginRelease release, string update, CancellationToken cancel)
    {
        var folder = Path.Combine(update, release.Version.ToString());
        if (File.Exists(Path.Combine(folder, "ready"))) return true;
        Directory.CreateDirectory(folder);
        await Save(release.ZipUrl, Path.Combine(folder, ZipName(release.Version)), cancel);
        await Save(release.SumsUrl, Path.Combine(folder, SumsName), cancel);
        var staged = Stage(folder, release.Version);
        ClientUiRuntime.Log(staged
            ? $"Plugin update {release.Version} staged."
            : $"Plugin update {release.Version} not staged: the zip does not match {SumsName}.");
        return staged;
    }

    private static async Task Save(string url, string path, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, cancel);
    }

    /// <summary>
    /// With no updater running, removes every version folder that is not a ready stage newer than the running stub: what an updater left
    /// after applying (the exe), a download that failed or did not verify, and a stage the install already has. A folder changed within a
    /// download's deadline is left alone: another client may be downloading into it.
    /// </summary>
    private static void RemoveStale(string update, Version current)
    {
        if (!Directory.Exists(update)) return;
        foreach (var folder in Directory.GetDirectories(update))
        {
            if (File.Exists(Path.Combine(folder, "ready")) && Version.TryParse(Path.GetFileName(folder), out var version) && version > Plain(current))
                continue;
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < Deadline) continue;
            try { Directory.Delete(folder, true); }
            catch (Exception exception) { ClientUiRuntime.Log($"Could not remove the old update folder {folder}: {exception.Message}"); }
        }
    }

    /// <summary>
    /// Starts a copy of the install's updater from the stage, detached, so it never replaces itself. It waits for every game client to
    /// exit, then applies the stage.
    /// </summary>
    private void StartUpdater(string folder)
    {
        var updater = Path.Combine(folder, UpdaterName);
        File.Copy(Path.Combine(_install, UpdaterName), updater, overwrite: true);
        var start = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        start.ArgumentList.Add(_install);
        start.ArgumentList.Add(Path.Combine(folder, "files"));
        Process.Start(start)?.Dispose();
        ClientUiRuntime.Log($"Started the updater for {folder}.");
    }

    // The stub's assembly version is X.Y.Z.0; a tag's is X.Y.Z, which Version orders below X.Y.Z.0.
    private static Version Plain(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
