using System.Security.Cryptography;
using DatReaderWriter;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using LegACEy.Updater;

namespace LegACEy.Client.Tests;

public sealed class WorldApplyTests : IDisposable
{
    private const string Cell = "client_cell_1.dat";
    private readonly string game = Path.Combine(Path.GetTempPath(), "legacey-world-" + Guid.NewGuid().ToString("N"));
    private string Stage => WorldFiles.Stage(game, 2);

    public WorldApplyTests()
    {
        Directory.CreateDirectory(game);
        // A tiny cell dat standing in for retail's: one landblock record.
        using var dat = Open(Path.Combine(game, Cell));
        dat.BlockAllocator.InitNew(DatFileType.Cell, 0, 256, 16);
        Assert.True(dat.TryWriteFileBytes(0x22A8FFFF, new byte[] { 1, 2, 3 }, 3, 982));
    }

    public void Dispose() => Directory.Delete(game, true);

    [Fact]
    public void WritesTheRecordsAndTheWorldVersion()
    {
        Stage2(new WorldPatchRecord(Cell, 0x22A8FFFF, 982, false, new byte[] { 9, 9, 9, 9 }),
            new WorldPatchRecord(Cell, 0x22A80100, 982, false, new byte[] { 7 }));
        File.WriteAllText(Path.Combine(Stage, WorldFiles.VersionFile), WorldFiles.WorldJson(2, "https://isles.test/l.json", "Bob's \"Isles\""));

        Assert.Equal(ApplyResult.Applied, WorldApply.Apply(game, game, Stage));

        using (var dat = Open(Path.Combine(game, Cell), DatAccessType.Read))
        {
            Assert.True(dat.TryGetFileBytes(0x22A8FFFF, out var landblock));
            Assert.Equal(new byte[] { 9, 9, 9, 9 }, landblock);
            Assert.True(dat.TryGetFileBytes(0x22A80100, out var cell));
            Assert.Equal(new byte[] { 7 }, cell);
        }
        var world = WorldFiles.ReadWorld(game);
        Assert.Equal((2, "https://isles.test/l.json", "Bob's \"Isles\""), (world.Version, world.Address, world.Name));
        Assert.False(File.Exists(Path.Combine(Stage, WorldFiles.ReadyFile)));
        Assert.False(File.Exists(Path.Combine(Stage, WorldFiles.PatchFile)));
    }

    [Fact]
    public void KeepsTheOriginalsWhenItFails()
    {
        // The version file can't be written, after the patched dat was swapped in: the swap is undone.
        Directory.CreateDirectory(Path.Combine(game, WorldFiles.VersionFile));
        Stage2(new WorldPatchRecord(Cell, 0x22A8FFFF, 982, false, new byte[] { 9, 9, 9, 9 }));
        var before = File.ReadAllBytes(Path.Combine(game, Cell));

        Assert.Equal(ApplyResult.Failed, WorldApply.Apply(game, game, Stage));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(game, Cell)));
        Assert.Equal(1, WorldFiles.ReadVersion(game));
        Assert.True(File.Exists(Path.Combine(Stage, WorldFiles.ReadyFile)));
        // failed tells the plugin to throw the stage away rather than close the game for it again.
        Assert.Equal(new[] { WorldFiles.FailedFile, WorldFiles.PatchFile, WorldFiles.ReadyFile }.Order(),
            Directory.GetFileSystemEntries(Stage).Select(Path.GetFileName).Order());
        Assert.Contains(WorldFiles.VersionFile, File.ReadAllText(Path.Combine(game, "updater.log")));
    }

    private void Stage2(params WorldPatchRecord[] records)
    {
        Directory.CreateDirectory(Stage);
        var patch = Path.Combine(Stage, WorldFiles.PatchFile);
        using (var stream = File.Create(patch)) new WorldPatch(2, records).Write(stream);
        File.WriteAllText(Path.Combine(Stage, WorldFiles.ReadyFile), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(patch))));
    }

    private static DatDatabase Open(string path, DatAccessType access = DatAccessType.ReadWrite) => new(options =>
    {
        options.FilePath = path;
        options.AccessType = access;
    });
}
