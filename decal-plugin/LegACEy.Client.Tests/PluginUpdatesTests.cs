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
    public async Task A_press_while_another_client_checks_is_told_to_wait()
    {
        var install = Install();
        using var updates = new PluginUpdates(install, _ => Task.FromResult("[]"));
        // Another client holds the check stamp open while it checks.
        using (new FileStream(Path.Combine(install, "update-check"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await updates.CheckNow();

        Assert.Equal(new[] { UpdateNoticeKind.Checking, UpdateNoticeKind.Wait }, Kinds(updates));
    }

    [Fact]
    public async Task A_background_check_is_silent_until_a_newer_release_is_out_and_then_offers_it_once_without_downloading()
    {
        var install = Install();
        var releases = "[]";
        using var updates = new PluginUpdates(install, _ => Task.FromResult(releases));
        await updates.Check();
        Assert.Empty(Notices(updates));

        var next = Next();
        releases = $"[{Release($"decal-plugin-v{next}")}]";
        await updates.CheckNow();
        var ready = Notices(updates).Last();
        Assert.Equal(UpdateNoticeKind.Ready, ready.Kind);
        Assert.Equal(next, ready.Version);
        Assert.False(ready.SeveralClients);
        Assert.False(Directory.Exists(Path.Combine(install, "update", next.ToString())));

        // Offered once; the next background check stays quiet.
        File.Delete(Path.Combine(install, "update-check"));
        await updates.Check();
        Assert.Empty(Notices(updates));
    }

    [Fact]
    public async Task Installing_downloads_the_release_a_check_found_and_says_downloaded_or_failed()
    {
        var install = Install();
        var next = Next();
        using var updates = new PluginUpdates(install, _ => Task.FromResult($"[{Release($"decal-plugin-v{next}")}]"));

        // Nothing found yet: nothing to install.
        await updates.Install(next);
        Assert.Empty(Notices(updates));

        await updates.CheckNow();
        Notices(updates);
        // The release's host isn't there, so the download fails and nothing is staged.
        await updates.Install(next);
        var failed = Assert.Single(Notices(updates));
        Assert.Equal((UpdateNoticeKind.Failed, next), (failed.Kind, failed.Version));

        // Already staged, with an updater waiting: it says downloaded at once.
        var stage = Path.Combine(install, "update", next.ToString());
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "ready"), "");
        using var updater = new Mutex(true, @"Local\LegACEy.Updater");
        await updates.Install(next);
        var downloaded = Assert.Single(Notices(updates));
        Assert.Equal((UpdateNoticeKind.Downloaded, next), (downloaded.Kind, downloaded.Version));
    }

    private static Version Next()
    {
        var current = typeof(PluginUpdates).Assembly.GetName().Version!;
        return new Version(current.Major + 1, 0, 0);
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
