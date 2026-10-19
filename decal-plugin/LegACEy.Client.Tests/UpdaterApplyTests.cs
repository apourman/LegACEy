using LegACEy.Updater;

namespace LegACEy.Client.Tests;

public sealed class UpdaterApplyTests : IDisposable
{
    private readonly string install = Path.Combine(Path.GetTempPath(), "legacey-updater-" + Guid.NewGuid().ToString("N"));
    private string Stage => Path.Combine(install, "update", "0.7.0", "files");

    public UpdaterApplyTests()
    {
        Write(install, "LegACEy.Client.DecalPlugin.dll", "old stub");
        Write(install, "LegACEy.Client.GameArt.dll", "old art");
        Write(install, "runtime/coreclr.dll", "old clr");
        Write(install, "runtime/gone.dll", "dropped by the release");
        Write(install, "Plugins/Vault/Vault.dll", "old vault");
        Write(install, "Plugins/Retired/Retired.dll", "dropped by the release");
        Write(install, "window-positions.txt", "player");
        Write(install, "retail-chat-native", "");
        Write(install, "install.json", "{ \"channel\": \"release\" }");

        Write(Stage, "LegACEy.Client.DecalPlugin.dll", "new stub");
        Write(Stage, "LICENSE", "new license");
        Write(Stage, "runtime/coreclr.dll", "new clr");
        Write(Stage, "Plugins/Vault/Vault.dll", "new vault");
        Write(Stage, "Plugins/Inventory/Inventory.dll", "new inventory");
    }

    public void Dispose() => Directory.Delete(install, true);

    [Fact]
    public void AppliesTheStageAndKeepsPlayerFiles()
    {
        Ready();

        Assert.Equal(ApplyResult.Applied, StageApply.Apply(install, Stage));

        Assert.Equal(new Dictionary<string, string>
        {
            ["LegACEy.Client.DecalPlugin.dll"] = "new stub",
            ["LegACEy.Client.GameArt.dll"] = "old art",
            ["LICENSE"] = "new license",
            ["runtime/coreclr.dll"] = "new clr",
            ["Plugins/Vault/Vault.dll"] = "new vault",
            ["Plugins/Inventory/Inventory.dll"] = "new inventory",
            ["window-positions.txt"] = "player",
            ["retail-chat-native"] = "",
            ["install.json"] = "{ \"channel\": \"release\" }",
        }, Snapshot());
        // The version folder stays for the client to remove (the running updater is in it), but holds no stage.
        var version = Path.Combine(install, "update", "0.7.0");
        Assert.False(File.Exists(Path.Combine(version, "ready")));
        Assert.False(Directory.Exists(Stage));
        Assert.False(Directory.Exists(Path.Combine(version, "rollback")));
        Assert.True(File.Exists(Path.Combine(install, "updater.log")));
    }

    [Fact]
    public void RollsBackWhenAFileCannotBeReplaced()
    {
        // A folder where the stage has a file: that file can't be written, after runtime\ and Plugins\ were swapped.
        Write(Stage, "blocked.dll", "new");
        Directory.CreateDirectory(Path.Combine(install, "blocked.dll"));
        Ready();
        var before = Snapshot();

        Assert.Equal(ApplyResult.Failed, StageApply.Apply(install, Stage));

        Assert.Equal(before, Snapshot());
        Assert.True(File.Exists(Path.Combine(install, "update", "0.7.0", "ready")));
        Assert.Equal("new clr", File.ReadAllText(Path.Combine(Stage, "runtime", "coreclr.dll")));
        Assert.Contains("blocked.dll", File.ReadAllText(Path.Combine(install, "updater.log")));
    }

    [Fact]
    public void IgnoresAStageWithoutReady()
    {
        var before = Snapshot();

        Assert.Equal(ApplyResult.NotReady, StageApply.Apply(install, Stage));

        Assert.Equal(before, Snapshot());
        Assert.True(File.Exists(Path.Combine(Stage, "runtime", "coreclr.dll")));
    }

    private void Ready() => Write(Path.Combine(install, "update", "0.7.0"), "ready", "");

    // Every install file outside update\ and the log, by forward-slash relative path.
    private Dictionary<string, string> Snapshot() => Directory
        .EnumerateFiles(install, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(install, path).Replace('\\', '/'))
        .Where(path => !path.StartsWith("update/") && path != "updater.log")
        .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(install, path)));

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
