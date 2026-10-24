using System.Security.Cryptography;
using System.Text;
using LegACEy.Client.DecalPlugin;
using LegACEy.Updater;

namespace LegACEy.Client.Tests;

public sealed class WorldUpdatesTests : IDisposable
{
    private static readonly byte[] Patch = Encoding.ASCII.GetBytes("a world patch");
    private readonly string game = Path.Combine(Path.GetTempPath(), "legacey-world-check-" + Guid.NewGuid().ToString("N"));
    private int downloads;

    public WorldUpdatesTests() => Directory.CreateDirectory(game);

    public void Dispose() => Directory.Delete(game, true);

    private static string Manifest(int version) =>
        $$"""{ "version": {{version}}, "patch": "http://wsl.test/world-{{version}}.patch", "size": {{Patch.Length}}, "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(Patch))}}" }""";

    private WorldUpdates Updates(Func<string> manifest, params byte[][] bodies) => new(game, "http://wsl.test/world-manifest.json",
        (_, _) => Task.FromResult(manifest()),
        async (_, file, progress, cancel) =>
        {
            var body = bodies[Math.Min(downloads++, bodies.Length - 1)];
            await file.WriteAsync(body, cancel);
            progress(body.Length);
        });

    [Fact]
    public async Task AFolderWithoutAVersionFileIsRetailAndUpToDateWithWorldOne()
    {
        var updates = Updates(() => Manifest(1), Patch);

        await updates.Start();

        Assert.Equal(WorldState.UpToDate, updates.State);
        Assert.False(updates.HoldsEnter);
        Assert.Equal(0, downloads);
    }

    [Fact]
    public async Task ABehindFolderDownloadsAndVerifiesThePatch()
    {
        File.WriteAllText(Path.Combine(game, WorldFiles.VersionFile), WorldFiles.VersionJson(2));
        var updates = Updates(() => Manifest(3), Patch);

        await updates.Start();

        Assert.Equal(WorldState.Ready, updates.State);
        Assert.True(updates.HoldsEnter);
        Assert.Equal(100, updates.Percent);
        var stage = WorldFiles.Stage(game, 3);
        Assert.Equal(Patch, File.ReadAllBytes(Path.Combine(stage, WorldFiles.PatchFile)));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Patch)), File.ReadAllText(Path.Combine(stage, WorldFiles.ReadyFile)));
    }

    [Fact]
    public async Task ABadDownloadIsThrownAwayAndTriedOnceMore()
    {
        var updates = Updates(() => Manifest(2), Encoding.ASCII.GetBytes("a damaged patch"), Patch);

        await updates.Start();

        Assert.Equal(WorldState.Ready, updates.State);
        Assert.Equal(2, downloads);
    }

    [Fact]
    public async Task TwoBadDownloadsOrNoManifestLeaveEnterOpen()
    {
        var damaged = Updates(() => Manifest(2), Encoding.ASCII.GetBytes("a damaged patch"));
        await damaged.Start();
        Assert.Equal(WorldState.Unavailable, damaged.State);
        Assert.False(damaged.HoldsEnter);
        Assert.Equal(2, downloads);
        Assert.Empty(Directory.GetFiles(WorldFiles.Stage(game, 2)));

        var unreachable = Updates(() => throw new HttpRequestException("No route to host"));
        await unreachable.Start();
        Assert.Equal(WorldState.Unavailable, unreachable.State);
        Assert.False(unreachable.HoldsEnter);
    }

    [Fact]
    public async Task AStageThatFailedToApplyIsThrownAwayAndEnterOpens()
    {
        var stage = WorldFiles.Stage(game, 2);
        Directory.CreateDirectory(stage);
        File.WriteAllBytes(Path.Combine(stage, WorldFiles.PatchFile), Patch);
        File.WriteAllText(Path.Combine(stage, WorldFiles.ReadyFile), Convert.ToHexStringLower(SHA256.HashData(Patch)));
        File.WriteAllText(Path.Combine(stage, WorldFiles.FailedFile), "disk full");
        var updates = Updates(() => Manifest(2), Patch);

        await updates.Start();

        Assert.Equal(WorldState.Unavailable, updates.State);
        Assert.False(Directory.Exists(stage));
        Assert.Equal(0, downloads);
    }
}
