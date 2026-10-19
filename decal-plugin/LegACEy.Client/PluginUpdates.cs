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
/// Keeps an installed plugin current. At login and every 6 hours, off the game thread, it looks for a newer plugin release on GitHub,
/// stages it in <c>update\&lt;version&gt;\</c> beside the stub, and starts the updater copied into that stage, which applies it once every
/// game client has exited. Only an install made by setup.exe updates (its <c>install.json</c> sits beside the stub); a
/// <c>no-auto-update</c> file beside the stub turns it off. Every failure is logged and ignored.
/// </summary>
internal sealed class PluginUpdates : IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/apourman/LegACEy/releases?per_page=100";
    private const string SumsName = "SHA256SUMS.txt";
    private const string UpdaterName = "LegACEy.Updater.exe";
    private const string UpdaterMutex = @"Local\LegACEy.Updater";
    // System, as Decal's chat colour: the chat window shows the line with the game's own notices.
    private const int SystemChat = 5;
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);
    // Every client of the install shares one check time, so several clients ask GitHub once (60 unauthenticated requests per hour per IP).
    private static readonly TimeSpan SharedGap = TimeSpan.FromHours(1);
    // A whole check, download included, gives up after this, so a stalled connection never keeps the next check from starting.
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);
    private static readonly Regex PluginTag = new(@"^decal-plugin-v(\d+\.\d+\.\d+)$", RegexOptions.Compiled);
    // GitHub's API refuses requests without a User-Agent.
    private static readonly HttpClient Http = new() { DefaultRequestHeaders = { { "User-Agent", "LegACEy-decal-plugin" } } };

    private readonly string _install;
    private readonly ConcurrentQueue<string> _notices = new();
    private readonly HashSet<Version> _announced = new();
    private Timer? _timer;
    private int _checking;

    public PluginUpdates(string install) => _install = install;

    public static string ZipName(Version version) => $"LegACEy-decal-plugin-v{version}-windows-x86.zip";

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

    /// <summary>Starts a check off the game thread unless one is running, and from the first call on, one every 6 hours.</summary>
    public void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        _timer ??= new Timer(_ => Check(), null, Every, Every);
        Task.Run(async () =>
        {
            try
            {
                using var deadline = new CancellationTokenSource(Deadline);
                await Run(deadline.Token);
            }
            catch (Exception exception) { ClientUiRuntime.Log($"Plugin update check failed: {exception.Message}"); }
            finally { Volatile.Write(ref _checking, 0); }
        });
    }

    /// <summary>
    /// Writes the chat line for each newly staged version; a line that fails stays queued for the next frame. Game thread only, while a
    /// character is in the world.
    /// </summary>
    public void PostNotices()
    {
        while (_notices.TryPeek(out var notice))
        {
            Bridge.Chat(notice, SystemChat);
            _notices.TryDequeue(out _);
        }
    }

    public void Dispose() => _timer?.Dispose();

    private async Task Run(CancellationToken cancel)
    {
        if (!File.Exists(Path.Combine(_install, "install.json")) || File.Exists(Path.Combine(_install, "no-auto-update"))) return;
        var current = AssemblyName.GetAssemblyName(Path.Combine(_install, "LegACEy.Client.DecalPlugin.dll")).Version ?? new Version();
        var update = Path.Combine(_install, "update");
        var updaterRunning = Mutex.TryOpenExisting(UpdaterMutex, out var held);
        held?.Dispose();

        // The stamp holds the last check's time. Held open unshared, it makes this the one client that checks, cleans up and downloads;
        // while another client holds it, this one skips straight to the stages already there.
        FileStream? slot = null;
        try { slot = new FileStream(Path.Combine(_install, "update-check"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { }
        if (slot != null)
            await using (slot)
            {
                var last = DateTime.TryParse(new StreamReader(slot, leaveOpen: true).ReadToEnd(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var time) ? time : DateTime.MinValue;
                if (DateTime.UtcNow - last >= SharedGap)
                {
                    slot.SetLength(0);
                    slot.Write(Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")));
                    slot.Flush();
                    if (!updaterRunning) RemoveStale(update, current);
                    if (Newest(await Http.GetStringAsync(ReleasesUrl, cancel), current) is { } release) await Download(release, update, cancel);
                }
            }

        // A stage this client or another made, or one an updater left after a failed apply: announced once, applied at the game's exit.
        if (Staged(update).Where(version => version > Plain(current)).Max() is not { } staged) return;
        if (_announced.Add(staged)) _notices.Enqueue($"LegACEy {staged} is downloaded and installs when the game closes.");
        if (!updaterRunning) StartUpdater(Path.Combine(update, staged.ToString()));
    }

    private static async Task Download(PluginRelease release, string update, CancellationToken cancel)
    {
        var folder = Path.Combine(update, release.Version.ToString());
        if (File.Exists(Path.Combine(folder, "ready"))) return;
        Directory.CreateDirectory(folder);
        await Save(release.ZipUrl, Path.Combine(folder, ZipName(release.Version)), cancel);
        await Save(release.SumsUrl, Path.Combine(folder, SumsName), cancel);
        ClientUiRuntime.Log(Stage(folder, release.Version)
            ? $"Plugin update {release.Version} staged."
            : $"Plugin update {release.Version} not staged: the zip does not match {SumsName}.");
    }

    private static async Task Save(string url, string path, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, cancel);
    }

    /// <summary>The versions in <c>update\</c> whose stage is ready.</summary>
    private static IEnumerable<Version> Staged(string update) =>
        (Directory.Exists(update) ? Directory.GetDirectories(update) : Array.Empty<string>())
        .Where(folder => File.Exists(Path.Combine(folder, "ready")))
        .Select(folder => Version.TryParse(Path.GetFileName(folder), out var version) ? version : null)
        .OfType<Version>();

    /// <summary>
    /// With no updater running, removes every version folder that is not a ready stage newer than the running stub: what an updater left
    /// after applying (the exe), a download that failed or did not verify, and a stage the install already has.
    /// </summary>
    private static void RemoveStale(string update, Version current)
    {
        if (!Directory.Exists(update)) return;
        foreach (var folder in Directory.GetDirectories(update))
        {
            if (File.Exists(Path.Combine(folder, "ready")) && Version.TryParse(Path.GetFileName(folder), out var version) && version > Plain(current))
                continue;
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
