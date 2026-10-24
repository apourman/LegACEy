using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegACEy.Client.DecalPlugin;

/// <summary>A cached world: its update-list address, name and edition, its patch file and SHA-256, and the server it was last reached at.</summary>
internal sealed record CachedWorld(string Address, string Name, int Edition, string Patch, string Sha256, string? Server);

/// <summary>
/// Every world this game folder has visited, under <c>legacey-worlds\&lt;world key&gt;\</c>: <c>world.json</c> and the patch it names. The
/// world key comes from the update-list address, so one world reached through several hosts shares one entry. <c>servers.json</c> maps
/// <c>host:port</c> to a world key, so the right world is switched on at startup. The game's dats are never touched.
/// </summary>
internal sealed class WorldCache
{
    public const string FolderName = "legacey-worlds";
    private const string WorldFile = "world.json", ServersFile = "servers.json";

    private readonly string _root;

    public WorldCache(string game) => _root = Path.Combine(game, FolderName);

    public static string Key(string address) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address)))[..16];

    private string Folder(string address) => Path.Combine(_root, Key(address));

    /// <summary>The cached world with this update list; null when there is none or its entry can't be read.</summary>
    public CachedWorld? Find(string address)
    {
        try
        {
            var path = Path.Combine(Folder(address), WorldFile);
            if (!File.Exists(path)) return null;
            var world = JsonSerializer.Deserialize(File.ReadAllText(path), WorldCacheJson.Default.CachedWorld);
            return world?.Address == address ? world : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The cached world last reached at <paramref name="server"/> (<c>host:port</c>); null when none is.</summary>
    public CachedWorld? ForServer(string server) =>
        Servers().TryGetValue(server, out var address) ? Find(address) : null;

    /// <summary>A file to download a patch for this world into, of this client's own, so two clients never write the same one.</summary>
    public string PartFile(string address)
    {
        var folder = Folder(address);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"world.{Environment.ProcessId}.part");
    }

    /// <summary>Keeps a downloaded and verified patch as the world's, and remembers the server it came from. Older patches of the world
    /// are removed when nothing holds them open.</summary>
    public CachedWorld Store(string address, string name, int edition, string sha256, string part, string? server)
    {
        var folder = Folder(address);
        // Named by edition: the patch being replaced may still be open, served to this or another client.
        var patch = Path.Combine(folder, $"world-{edition}.patch");
        File.Move(part, patch, overwrite: true);
        var world = new CachedWorld(address, name, edition, Path.GetFileName(patch), sha256, server);
        Write(Path.Combine(folder, WorldFile), JsonSerializer.Serialize(world, WorldCacheJson.Default.CachedWorld));
        foreach (var old in Directory.GetFiles(folder, "world-*.patch"))
        {
            if (old == patch) continue;
            try { File.Delete(old); }
            catch (Exception) { /* Still open in a running client; removed by a later store. */ }
        }
        if (server != null) Remember(server, address);
        return world;
    }

    /// <summary>The world's patch, checked against its SHA-256 and indexed; null, and logged, when it's missing or doesn't match.</summary>
    public WorldPatchIndex? Open(CachedWorld world, Action<string> log)
    {
        try
        {
            var path = Path.Combine(Folder(world.Address), Path.GetFileName(world.Patch));
            var sha = WorldPatch.Sha256(path);
            if (!sha.Equals(world.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log($"Cached world {world.Name} ignored: its patch's SHA-256 is {sha}, not {world.Sha256}.");
                return null;
            }
            var index = WorldPatchIndex.Open(path);
            if (index.Version == world.Edition) return index;
            index.Dispose();
            log($"Cached world {world.Name} ignored: its patch is world {index.Version}, not {world.Edition}.");
        }
        catch (Exception exception) { log($"Cached world {world.Name} ignored: {exception.Message}"); }
        return null;
    }

    /// <summary>The world to switch on next time the game starts for <paramref name="server"/>.</summary>
    public void Remember(string server, string address)
    {
        var servers = Servers();
        if (servers.TryGetValue(server, out var known) && known == address) return;
        servers[server] = address;
        SaveServers(servers);
    }

    /// <summary>
    /// Marks a world switched on at startup as not yet confirmed by the server's note. A game kicked at connect for reporting a world the
    /// server dropped never sees a note, so the next start finds the mark and starts retail.
    /// </summary>
    public void Unconfirmed(string server, bool on)
    {
        var mark = Path.Combine(_root, $"unconfirmed-{Key(server)}");
        try
        {
            if (on) File.WriteAllText(mark, server);
            else File.Delete(mark);
        }
        catch (Exception exception) { ClientUiRuntime.Log($"Could not {(on ? "mark" : "clear")} the startup world's check: {exception.Message}"); }
    }

    public bool IsUnconfirmed(string server) => File.Exists(Path.Combine(_root, $"unconfirmed-{Key(server)}"));

    /// <summary>The server no longer runs a world: the game starts retail for it.</summary>
    public void Forget(string server)
    {
        var servers = Servers();
        if (servers.Remove(server)) SaveServers(servers);
    }

    private Dictionary<string, string> Servers()
    {
        try
        {
            var path = Path.Combine(_root, ServersFile);
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), WorldCacheJson.Default.DictionaryStringString) ?? new Dictionary<string, string>();
        }
        catch (Exception) { /* Unreadable: no world is switched on at startup until the next note rewrites it. */ }
        return new Dictionary<string, string>();
    }

    private void SaveServers(Dictionary<string, string> servers)
    {
        try
        {
            Directory.CreateDirectory(_root);
            Write(Path.Combine(_root, ServersFile), JsonSerializer.Serialize(servers, WorldCacheJson.Default.DictionaryStringString));
        }
        catch (Exception exception) { ClientUiRuntime.Log($"Could not save the world cache's server list: {exception.Message}"); }
    }

    /// <summary>Written beside, then moved over, so another client never reads half a file.</summary>
    private static void Write(string path, string text)
    {
        var part = $"{path}.{Environment.ProcessId}.part";
        File.WriteAllText(part, text);
        File.Move(part, path, overwrite: true);
    }

    /// <summary>
    /// The server the game was launched for, <c>host:port</c> from <c>-h</c> and <c>-p</c>; null when either is missing. Nothing else is
    /// read: the line also carries the account and password.
    /// </summary>
    internal static string? Server(IReadOnlyList<string> arguments)
    {
        string? host = null, port = null;
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i].Equals("-h", StringComparison.OrdinalIgnoreCase)) host = arguments[i + 1];
            else if (arguments[i].Equals("-p", StringComparison.OrdinalIgnoreCase)) port = arguments[i + 1];
        }
        return host is { Length: > 0 } && ushort.TryParse(port, out var number) ? $"{host}:{number}" : null;
    }

    /// <summary>The game process's own command line, split as Windows splits it. Never logged.</summary>
    internal static IReadOnlyList<string> CommandLine()
    {
        var argv = CommandLineToArgvW(GetCommandLineW(), out var count);
        if (argv == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            var arguments = new string[count];
            for (var i = 0; i < count; i++) arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return arguments;
        }
        finally { LocalFree(argv); }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCommandLineW();

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(IntPtr commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>The cache's files, read and written without reflection: the game's runtime has reflection-based JSON switched off.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CachedWorld))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class WorldCacheJson : JsonSerializerContext;
