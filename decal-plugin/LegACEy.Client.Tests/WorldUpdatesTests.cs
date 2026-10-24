using System.Security.Cryptography;
using System.Text;
using LegACEy.Client.Demo;
using LegACEy.Client.DecalPlugin;
using LegACEy.Updater;

namespace LegACEy.Client.Tests;

public sealed class WorldUpdatesTests : IDisposable
{
    private const string Isles = "https://isles.test/world-manifest.json";
    private static readonly byte[] Patch = Encoding.ASCII.GetBytes("a world patch");
    private static readonly string PatchSha = Convert.ToHexStringLower(SHA256.HashData(Patch));
    private readonly string game = Path.Combine(Path.GetTempPath(), "legacey-world-check-" + Guid.NewGuid().ToString("N"));
    private int fetches, downloads;

    public WorldUpdatesTests() => Directory.CreateDirectory(game);

    public void Dispose() => Directory.Delete(game, true);

    private static WorldNote Note(int edition, string updateList = Isles) => new("Bob's Isles", edition, updateList);

    private static string Manifest(int version) =>
        $$"""{ "version": {{version}}, "patch": "https://isles.test/world-{{version}}.patch", "size": {{Patch.Length}}, "sha256": "{{PatchSha}}" }""";

    private void Holds(int version, string address, string name = "Bob's Isles") =>
        File.WriteAllText(Path.Combine(game, WorldFiles.VersionFile), WorldFiles.WorldJson(version, address, name));

    private WorldUpdates Updates(Func<string> manifest, params byte[][] bodies) => new(game,
        (_, _) =>
        {
            fetches++;
            return Task.FromResult(manifest());
        },
        async (_, file, progress, cancel) =>
        {
            var body = bodies[Math.Min(downloads++, bodies.Length - 1)];
            await file.WriteAsync(body, cancel);
            progress(body.Length);
        });

    [Fact]
    public void EnterWaitsForTheNoteAndAPlainServerLeavesTheFolderAlone()
    {
        var updates = Updates(() => Manifest(983), Patch);
        Assert.True(updates.HoldsEnter);

        updates.NoNote();

        Assert.Equal(WorldState.Off, updates.State);
        Assert.False(updates.HoldsEnter);
        Assert.Equal(0, fetches);
        Assert.Empty(Directory.GetFileSystemEntries(game));
    }

    [Fact]
    public async Task ARetailFolderDownloadsTheServersWorldAndTheStageNamesIt()
    {
        var updates = Updates(() => Manifest(983), Patch);

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.Ready, updates.State);
        Assert.True(updates.HoldsEnter);
        var stage = WorldFiles.Stage(game, 983);
        Assert.Equal(Patch, File.ReadAllBytes(Path.Combine(stage, WorldFiles.PatchFile)));
        Assert.Equal(PatchSha, File.ReadAllText(Path.Combine(stage, WorldFiles.ReadyFile)));
        var world = WorldFiles.ReadWorld(stage);
        Assert.Equal((983, Isles, "Bob's Isles"), (world.Version, world.Address, world.Name));
    }

    [Fact]
    public async Task ABehindFolderOfTheSameWorldDownloads()
    {
        Holds(983, Isles);
        var updates = Updates(() => Manifest(984), Patch);

        await updates.Heard(Note(984));

        Assert.Equal(WorldState.Ready, updates.State);
        Assert.Equal(1, downloads);
    }

    [Fact]
    public async Task AFolderAtTheServersEditionIsUpToDateWithoutAsking()
    {
        Holds(983, Isles);
        var updates = Updates(() => throw new HttpRequestException("No route to host"));

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.UpToDate, updates.State);
        Assert.False(updates.HoldsEnter);
        Assert.Equal(0, fetches);
    }

    [Fact]
    public async Task AFolderHoldingAnotherWorldKeepsEnterHeldAndIsNeverChanged()
    {
        Holds(983, Isles);
        var before = File.ReadAllText(Path.Combine(game, WorldFiles.VersionFile));
        var updates = Updates(() => Manifest(990), Patch);

        await updates.Heard(new WorldNote("Frost Reach", 990, "https://frost.test/world-manifest.json"));

        Assert.Equal(WorldState.OtherWorld, updates.State);
        Assert.True(updates.HoldsEnter);
        Assert.Equal("Bob's Isles", updates.Folder!.Name);
        Assert.Equal((0, 0), (fetches, downloads));
        Assert.Equal(before, File.ReadAllText(Path.Combine(game, WorldFiles.VersionFile)));
        Assert.False(Directory.Exists(Path.Combine(game, WorldFiles.UpdateFolder)));
    }

    [Theory]
    [InlineData("http://isles.test/world-manifest.json", false)]
    [InlineData("ftp://isles.test/world-manifest.json", false)]
    [InlineData("https://isles.test/world-manifest.json", true)]
    [InlineData("http://localhost:8080/world-manifest.json", true)]
    [InlineData("http://127.0.0.1/world-manifest.json", true)]
    [InlineData("http://172.26.254.65:8080/world-manifest.json", true)]
    [InlineData("http://192.168.1.4/world-manifest.json", true)]
    [InlineData("http://172.32.0.1/world-manifest.json", false)]
    public void PlainHttpIsOnlyForThisMachineOrAPrivateNetwork(string address, bool allowed) => Assert.Equal(allowed, WorldUpdates.Allowed(address));

    [Fact]
    public async Task APublicHttpUpdateListIsRefusedAndEnterOpens()
    {
        var updates = Updates(() => Manifest(983), Patch);

        await updates.Heard(Note(983, "http://isles.test/world-manifest.json"));

        Assert.Equal(WorldState.Unavailable, updates.State);
        Assert.False(updates.HoldsEnter);
        Assert.Equal(0, fetches);
    }

    [Fact]
    public async Task ABadDownloadIsThrownAwayAndTriedOnceMore()
    {
        var updates = Updates(() => Manifest(983), Encoding.ASCII.GetBytes("a damaged patch"), Patch);

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.Ready, updates.State);
        Assert.Equal(2, downloads);
    }

    [Fact]
    public async Task TwoBadDownloadsOrNoUpdateListLeaveEnterOpen()
    {
        var damaged = Updates(() => Manifest(983), Encoding.ASCII.GetBytes("a damaged patch"));
        await damaged.Heard(Note(983));
        Assert.Equal(WorldState.Unavailable, damaged.State);
        Assert.False(damaged.HoldsEnter);
        Assert.Equal(2, downloads);
        Assert.False(File.Exists(Path.Combine(WorldFiles.Stage(game, 983), WorldFiles.PatchFile)));

        var unreachable = Updates(() => throw new HttpRequestException("No route to host"));
        await unreachable.Heard(Note(983));
        Assert.Equal(WorldState.Unavailable, unreachable.State);
        Assert.False(unreachable.HoldsEnter);
    }

    private string FailedStage(byte[] patch)
    {
        var stage = WorldFiles.Stage(game, 983);
        Directory.CreateDirectory(stage);
        File.WriteAllBytes(Path.Combine(stage, WorldFiles.PatchFile), patch);
        File.WriteAllText(Path.Combine(stage, WorldFiles.ReadyFile), Convert.ToHexStringLower(SHA256.HashData(patch)));
        File.WriteAllText(Path.Combine(stage, WorldFiles.FailedFile), "disk full");
        return stage;
    }

    [Fact]
    public async Task APatchThatFailedToApplyIsNotTriedAgainAndEnterOpens()
    {
        var stage = FailedStage(Patch);
        var updates = Updates(() => Manifest(983), Patch);

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.InstallFailed, updates.State);
        Assert.False(updates.HoldsEnter);
        Assert.True(File.Exists(Path.Combine(stage, WorldFiles.FailedFile)));
        Assert.Equal(0, downloads);
    }

    [Fact]
    public async Task ANewPatchForAVersionThatFailedIsDownloaded()
    {
        var stage = FailedStage(Encoding.ASCII.GetBytes("an older patch"));
        var updates = Updates(() => Manifest(983), Patch);

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.Ready, updates.State);
        Assert.False(File.Exists(Path.Combine(stage, WorldFiles.FailedFile)));
        Assert.Equal(1, downloads);
    }
}
