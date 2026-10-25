using System.Security.Cryptography;
using System.Text;
using LegACEy.Client.Demo;
using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class WorldUpdatesTests : IDisposable
{
    private const string Isles = "https://isles.test/world-manifest.json";
    private const string Server = "10.0.0.5:9000";
    private readonly string game = Path.Combine(Path.GetTempPath(), "legacey-world-check-" + Guid.NewGuid().ToString("N"));
    private readonly WorldCache cache;
    private int fetches, downloads;

    public WorldUpdatesTests()
    {
        Directory.CreateDirectory(game);
        cache = new WorldCache(game);
    }

    public void Dispose() => Directory.Delete(game, true);

    private static WorldNote Note(int edition, string updateList = Isles) => new("Bob's Isles", edition, updateList);

    private static byte[] Patch(int version, params WorldPatchRecord[] records)
    {
        var all = records.Prepend(new WorldPatchRecord("client_cell_1.dat", 0xFFFF0001, version, 1, BitConverter.GetBytes(version))).ToList();
        using var stream = new MemoryStream();
        new WorldPatch(version, all).Write(stream);
        return stream.ToArray();
    }

    private static string Manifest(int version, byte[] patch) =>
        $$"""{ "version": {{version}}, "patch": "https://isles.test/world-{{version}}.patch", "size": {{patch.Length}}, "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(patch))}}" }""";

    private WorldUpdates Updates(Func<string> manifest, params byte[][] bodies) => Updates(Server, manifest, bodies);

    private WorldUpdates Updates(string? server, Func<string> manifest, params byte[][] bodies) => new(cache, server,
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

    private static WorldUpdates Unreachable(WorldCache cache, string? server = Server) =>
        new(cache, server, (_, _) => throw new HttpRequestException("No route to host"), (_, _, _, _) => throw new HttpRequestException("No route to host"));

    /// <summary>A first visit: downloaded, switched on and reported, so the world is cached for <see cref="Server"/>.</summary>
    private async Task Visit(int version, params WorldPatchRecord[] records)
    {
        var patch = Patch(version, records);
        var updates = Updates(() => Manifest(version, patch), patch);
        await updates.Heard(Note(version));
        updates.SwitchedOn(reported: true);
        updates.ActivePatch!.Dispose(); // the game closed: Windows refuses to change a patch file still open
        (fetches, downloads) = (0, 0);
    }

    [Fact]
    public async Task AFirstVisitDownloadsHoldsEnterUntilSwitchedOnAndTheNextStartHasTheWorld()
    {
        var patch = Patch(983);
        var updates = Updates(() => Manifest(983, patch), patch);
        Assert.Null(updates.AtStartup());
        Assert.True(updates.HoldsEnter);

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.Switching, updates.State);
        Assert.True(updates.HoldsEnter);
        Assert.Equal(983, updates.Pending!.Version);
        Assert.Null(updates.ActivePatch);

        updates.SwitchedOn(reported: true);

        Assert.Equal(WorldState.On, updates.State);
        Assert.False(updates.HoldsEnter);
        Assert.Equal(983, updates.ActivePatch!.Version);
        Assert.Equal(983, Updates(() => throw new InvalidOperationException()).AtStartup()!.Version);
        Assert.Null(Updates("10.0.0.6:9000", () => throw new InvalidOperationException()).AtStartup());
    }

    [Fact]
    public async Task AReturningPlayerAtTheNotesEditionIsUpToDateWithoutAsking()
    {
        await Visit(983);
        var updates = Unreachable(cache);
        Assert.NotNull(updates.AtStartup());

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.UpToDate, updates.State);
        Assert.False(updates.HoldsEnter);
    }

    [Fact]
    public async Task ACachedWorldReachedThroughAnotherHostSwitchesOnWithoutDownloading()
    {
        await Visit(983);
        var updates = Unreachable(cache, "isles.example:9000");

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.Switching, updates.State);
        Assert.Equal(983, updates.Pending!.Version);
    }

    public static TheoryData<string, uint, bool> NewReleaseRecords => new()
    {
        { "client_portal.dat", 0x0200F001, false }, // a new model
        { "client_portal.dat", 0x06004376, false }, // a world texture
        { "client_cell_1.dat", 0x22A8FFFF, false }, // a landblock, whatever its prefix
        { "client_portal.dat", 0x06005ECF, true }, // a font atlas held at character select
        { "client_local_English.dat", 0x21000000, true }, // the connecting screen's layout
    };

    [Theory]
    [MemberData(nameof(NewReleaseRecords))]
    public async Task ANewReleaseDownloadsAndAsksForARelaunchOnlyForRecordsHeldAtSwitchOn(string dat, uint id, bool relaunch)
    {
        await Visit(983);
        var patch = Patch(984, new WorldPatchRecord(dat, id, 2, 3, new byte[] { 1, 2 }));
        var updates = Updates(() => Manifest(984, patch), patch);
        updates.AtStartup();

        await updates.Heard(Note(984));
        updates.SwitchedOn(reported: true);

        Assert.Equal((WorldState.On, 984, 1), (updates.State, updates.ActivePatch!.Version, downloads));
        Assert.Equal(relaunch, updates.PortalNeedsRelaunch);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AHeldRecordTheLastWorldChangedNeedsARelaunchOnlyWhenTheNewWorldDropsIt(bool kept, bool relaunch)
    {
        var atlas = new WorldPatchRecord("client_portal.dat", 0x06005ECF, 2, 3, new byte[] { 1, 2 });
        await Visit(983, atlas);
        var patch = kept ? Patch(984, atlas) : Patch(984);
        var updates = Updates(() => Manifest(984, patch), patch);
        updates.AtStartup();

        await updates.Heard(Note(984));
        updates.SwitchedOn(reported: true);

        Assert.Equal(relaunch, updates.PortalNeedsRelaunch);
    }

    [Fact]
    public async Task APlainServerSwitchesTheStartupWorldOffAndTheNextStartIsRetail()
    {
        await Visit(983);
        var updates = Unreachable(cache);
        Assert.NotNull(updates.AtStartup());

        updates.NoNote();
        updates.NoNote();

        Assert.Equal(WorldState.Off, updates.State);
        Assert.False(updates.HoldsEnter);
        Assert.Null(updates.ActivePatch);
        Assert.Null(Unreachable(cache).AtStartup());
        Assert.Null(Unreachable(cache).AtStartup());
    }

    [Fact]
    public async Task AServerThatRolledItsWorldBackGetsItsOwnEditionNotTheNewerCachedOne()
    {
        await Visit(990);
        var patch = Patch(983);
        var updates = Updates(() => Manifest(983, patch), patch);

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.Switching, updates.State);
        Assert.Equal((983, 1), (updates.Pending!.Version, downloads));
    }

    [Fact]
    public async Task ANoteAfterTheEndOfTheExchangeStillSwitchesTheWorldOn()
    {
        await Visit(983);
        var updates = Unreachable(cache);
        updates.AtStartup();

        updates.NoNote();
        await updates.Heard(Note(983));

        Assert.Equal(WorldState.Switching, updates.State);
        Assert.True(updates.HoldsEnter);
        Assert.NotNull(Unreachable(cache).AtStartup());
    }

    [Fact]
    public async Task AStartupWorldTheServerNeverConfirmedIsOffNextStart()
    {
        await Visit(983);
        Assert.NotNull(Unreachable(cache).AtStartup());

        // Kicked at connect: no note, no end of the exchange.
        Assert.Null(Unreachable(cache).AtStartup());
        var confirmed = Unreachable(cache);
        await confirmed.Heard(Note(983));
        Assert.NotNull(Unreachable(cache).AtStartup());
    }

    [Fact]
    public async Task ACachedPatchThatFailsItsChecksumIsIgnoredAtStartup()
    {
        await Visit(983);
        var file = Directory.GetFiles(Path.Combine(game, WorldCache.FolderName), "world-983.patch", SearchOption.AllDirectories).Single();
        File.AppendAllText(file, "damage");

        Assert.Null(Unreachable(cache).AtStartup());
    }

    [Fact]
    public async Task ABadDownloadIsThrownAwayAndTriedOnceMore()
    {
        var patch = Patch(983);
        var updates = Updates(() => Manifest(983, patch), Encoding.ASCII.GetBytes("a damaged patch"), patch);

        await updates.Heard(Note(983));

        Assert.Equal(WorldState.Switching, updates.State);
        Assert.Equal(2, downloads);
    }

    [Fact]
    public async Task TwoBadDownloadsOrNoUpdateListLeaveEnterToTheServer()
    {
        var patch = Patch(983);
        var damaged = Updates(() => Manifest(983, patch), Encoding.ASCII.GetBytes("a damaged patch"));
        await damaged.Heard(Note(983));
        Assert.Equal(WorldState.Unavailable, damaged.State);
        Assert.False(damaged.HoldsEnter);
        Assert.Equal(2, downloads);
        Assert.Null(cache.Find(Isles));

        var unreachable = Unreachable(cache);
        await unreachable.Heard(Note(983));
        Assert.Equal(WorldState.Unavailable, unreachable.State);
        Assert.False(unreachable.HoldsEnter);
    }

    [Fact]
    public void OnlyTheServerIsReadFromTheCommandLine()
    {
        Assert.Equal("10.0.0.5:9000", WorldCache.Server(new[] { "acclient.exe", "-a", "bob", "-v", "hunter2", "-h", "10.0.0.5", "-p", "9000", "-rodat", "on" }));
        Assert.Equal("10.0.0.5:9000", WorldCache.Server(new[] { "acclient.exe", "-a", "bob", "-v", "hunter2", "-h", "10.0.0.5:9000", "-rodat", "off" }));
        Assert.Null(WorldCache.Server(new[] { "acclient.exe", "-h", "10.0.0.5", "-a", "bob" }));
    }

    [Theory]
    [InlineData("http://isles.test/world-manifest.json", false)]
    [InlineData("ftp://isles.test/world-manifest.json", false)]
    [InlineData("https://isles.test/world-manifest.json", true)]
    [InlineData("http://localhost:8080/world-manifest.json", true)]
    [InlineData("http://172.26.254.65:8080/world-manifest.json", true)]
    [InlineData("http://192.168.1.4/world-manifest.json", true)]
    [InlineData("http://172.32.0.1/world-manifest.json", false)]
    public void PlainHttpIsOnlyForThisMachineOrAPrivateNetwork(string address, bool allowed) => Assert.Equal(allowed, WorldUpdates.Allowed(address));

    [Fact]
    public void ThePatchIndexServesEveryRecordsExactBytesAndRefusesACutShortPatch()
    {
        var records = new[]
        {
            new WorldPatchRecord("client_cell_1.dat", 0xA9B4FFFF, 3, 2, Encoding.ASCII.GetBytes("a landblock")),
            new WorldPatchRecord("client_cell_1.dat", 0xA9B40100, 1, 3, Array.Empty<byte>()),
            new WorldPatchRecord("client_portal.dat", 0x01000001, 2, 3, Encoding.ASCII.GetBytes("a model")),
        };
        var path = Path.Combine(game, "test.patch");
        File.WriteAllBytes(path, Patch(983, records));

        using (var index = WorldPatchIndex.Open(path))
        {
            Assert.Equal((983, 4), (index.Version, index.Count));
            foreach (var record in records)
            {
                var entry = index.Records(record.Dat)![record.Id];
                Assert.Equal((record.Iteration, record.Version), (entry.Iteration, entry.Version));
                Assert.Equal(record.Data, index.Read(entry));
            }
            Assert.Null(index.Records("client_highres.dat"));
        }

        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^3]);
        Assert.Throws<InvalidDataException>(() => WorldPatchIndex.Open(path));
    }
}
