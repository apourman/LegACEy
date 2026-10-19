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
}
