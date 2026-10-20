using System.IO.Compression;
using System.Security.Cryptography;
using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class PluginUpdatesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "legacey-stage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Fact]
    public void OnlyASetupInstallWithoutNoAutoUpdateChecks()
    {
        Directory.CreateDirectory(folder);
        using var updates = new PluginUpdates(folder);
        Assert.Equal("checkout build", updates.Off);

        File.WriteAllText(Path.Combine(folder, "install.json"), "{}");
        Assert.Null(updates.Off);

        File.WriteAllText(Path.Combine(folder, "no-auto-update"), "");
        Assert.Equal("updates off", updates.Off);
    }

    private static string Release(string tag, bool draft = false, bool prerelease = false) => $$"""
        { "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "LegACEy-{{tag}}-windows-x86.zip", "browser_download_url": "https://example.test/{{tag}}.zip" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/{{tag}}.sums" } ] }
        """;

    // The server's releases share the repository; drafts and prereleases never install; 0.10.0 is above 0.9.0.
    private static readonly string Releases = "[" + string.Join(",",
        Release("v0.11.0"),
        Release("decal-plugin-v0.12.0", draft: true),
        Release("decal-plugin-v0.11.0", prerelease: true),
        Release("decal-plugin-v0.9.0"),
        Release("decal-plugin-v0.10.0"),
        Release("decal-plugin-v0.6.1")) + "]";

    [Fact]
    public void PicksTheHighestPublishedPluginRelease()
    {
        var release = PluginUpdates.Newest(Releases, new Version(0, 6, 1, 0));

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 10, 0), release.Version);
        Assert.Equal("https://example.test/decal-plugin-v0.10.0.zip", release.ZipUrl);
        Assert.Equal("https://example.test/decal-plugin-v0.10.0.sums", release.SumsUrl);
    }

    [Fact]
    public void AlreadyCurrentMeansNoUpdate()
    {
        // The release build's -p:Version=0.10.0 gives the stub assembly version 0.10.0.0.
        Assert.Null(PluginUpdates.Newest(Releases, new Version(0, 10, 0, 0)));
    }

    [Fact]
    public void StagesOnlyAVerifiedZip()
    {
        var version = new Version(0, 10, 0);
        var stage = Path.Combine(folder, "update", "0.10.0");
        var content = Path.Combine(folder, "content");
        Directory.CreateDirectory(Path.Combine(content, "runtime"));
        File.WriteAllText(Path.Combine(content, "runtime", "coreclr.dll"), "new clr");
        Directory.CreateDirectory(stage);
        var zip = Path.Combine(stage, PluginUpdates.ZipName(version));
        ZipFile.CreateFromDirectory(content, zip);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)));
        var sums = Path.Combine(stage, "SHA256SUMS.txt");

        File.WriteAllText(sums, $"{new string('0', 64)}  {PluginUpdates.ZipName(version)}\n");
        Assert.False(PluginUpdates.Stage(stage, version));
        Assert.False(Directory.Exists(Path.Combine(stage, "files")));
        Assert.False(File.Exists(Path.Combine(stage, "ready")));

        File.WriteAllText(sums, $"{new string('1', 64)}  LegACEy.Client.DecalPlugin.dll\n{hash}  {PluginUpdates.ZipName(version)}\n");
        Assert.True(PluginUpdates.Stage(stage, version));
        Assert.Equal("new clr", File.ReadAllText(Path.Combine(stage, "files", "runtime", "coreclr.dll")));
        Assert.True(File.Exists(Path.Combine(stage, "ready")));
    }

    [Fact]
    public async Task A_pressed_check_says_checking_then_its_outcome_and_a_press_too_soon_is_told_to_wait()
    {
        using var updates = new PluginUpdates(Install(), _ => Task.FromResult("[]"));

        await updates.CheckNow();
        Assert.Equal(new[] { UpdateNoticeKind.Checking, UpdateNoticeKind.UpToDate }, Kinds(updates));

        // Every press within the minute is told to wait; chat hears it once.
        await updates.CheckNow();
        await updates.CheckNow();
        var waits = Notices(updates);
        Assert.Equal(new[] { UpdateNoticeKind.Wait, UpdateNoticeKind.Wait }, waits.Select(notice => notice.Kind));
        Assert.NotNull(waits[0].Chat);
        Assert.Null(waits[1].Chat);

        using var offline = new PluginUpdates(Install(), _ => throw new HttpRequestException("GitHub is down"));
        await offline.CheckNow();
        Assert.Equal(new[] { UpdateNoticeKind.Checking, UpdateNoticeKind.Failed }, Kinds(offline));
    }

    [Fact]
    public async Task A_background_check_is_silent_until_an_update_is_staged_and_then_says_ready_once()
    {
        var install = Install();
        using var updates = new PluginUpdates(install, _ => Task.FromResult("[]"));
        await updates.Check();
        Assert.Empty(Notices(updates));

        // Another client staged the next version and its updater is already waiting for the game to close.
        var current = typeof(PluginUpdates).Assembly.GetName().Version!;
        var next = new Version(current.Major + 1, 0, 0);
        Directory.CreateDirectory(Path.Combine(install, "update", next.ToString()));
        File.WriteAllText(Path.Combine(install, "update", next.ToString(), "ready"), "");
        using var updater = new Mutex(true, @"Local\LegACEy.Updater");

        await updates.Check();
        var ready = Assert.Single(Notices(updates));
        Assert.Equal(UpdateNoticeKind.Ready, ready.Kind);
        Assert.Equal(next, ready.Version);
        Assert.False(ready.SeveralClients);
        await updates.Check();
        Assert.Empty(Notices(updates));

        // A press says it again.
        await updates.CheckNow();
        Assert.Equal(new[] { UpdateNoticeKind.Checking, UpdateNoticeKind.Ready }, Kinds(updates));
    }

    /// <summary>A setup install whose stub is this build's client assembly.</summary>
    private string Install()
    {
        var install = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "install.json"), "{}");
        File.Copy(typeof(PluginUpdates).Assembly.Location, Path.Combine(install, "LegACEy.Client.DecalPlugin.dll"));
        return install;
    }

    private static List<UpdateNotice> Notices(PluginUpdates updates)
    {
        var notices = new List<UpdateNotice>();
        updates.PostNotices(notices.Add);
        return notices;
    }

    private static IEnumerable<UpdateNoticeKind> Kinds(PluginUpdates updates) => Notices(updates).Select(notice => notice.Kind);
}
