using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LegACEy.Updater;

/// <summary>One changed or new dat record (net48 has no records): which dat, its file id, its iteration, whether the dat stores it compressed, and its bytes.</summary>
public sealed class WorldPatchRecord(string dat, uint id, int iteration, bool compressed, byte[] data)
{
    public string Dat { get; } = dat;
    public uint Id { get; } = id;
    public int Iteration { get; } = iteration;
    public bool Compressed { get; } = compressed;
    public byte[] Data { get; } = data;
}

/// <summary>
/// A world patch: every dat record that differs from retail, and the world version it brings a game folder to. Built against retail, so
/// one patch brings any older folder up to date. Also compiled into the client, the patch builder and the tests, so it stays plain .NET.
/// </summary>
public sealed class WorldPatch(int version, IReadOnlyList<WorldPatchRecord> records)
{
    public int Version { get; } = version;
    public IReadOnlyList<WorldPatchRecord> Records { get; } = records;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LWP1");
    // Only the game's own dat files, by bare name: a patch never names a path.
    private static readonly Regex DatName = new(@"^client_[A-Za-z0-9_]+\.dat$", RegexOptions.Compiled);

    public void Write(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(Records.Count);
        foreach (var record in Records)
        {
            writer.Write(record.Dat);
            writer.Write(record.Id);
            writer.Write(record.Iteration);
            writer.Write(record.Compressed);
            writer.Write(record.Data.Length);
            writer.Write(record.Data);
        }
    }

    /// <summary>Reads a patch; throws <see cref="InvalidDataException"/> when it isn't one or names anything but a game dat.</summary>
    public static WorldPatch Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != "LWP1")
            throw new InvalidDataException("Not a LegACEy world patch.");
        var version = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (version < 1 || count < 0) throw new InvalidDataException("A world patch with a bad header.");
        var records = new List<WorldPatchRecord>();
        for (var i = 0; i < count; i++)
        {
            var dat = reader.ReadString();
            if (!DatName.IsMatch(dat)) throw new InvalidDataException($"A world patch naming {dat}, not a game dat.");
            var id = reader.ReadUInt32();
            var iteration = reader.ReadInt32();
            var compressed = reader.ReadBoolean();
            var length = reader.ReadInt32();
            var data = length >= 0 ? reader.ReadBytes(length) : Array.Empty<byte>();
            if (data.Length != length) throw new InvalidDataException("A world patch cut short.");
            records.Add(new WorldPatchRecord(dat, id, iteration, compressed, data));
        }
        return new WorldPatch(version, records);
    }
}

/// <summary>Where a game folder keeps its world version and its staged world patches.</summary>
public static class WorldFiles
{
    /// <summary>Beside the dats: <c>{ "version": N }</c>. A folder without it is version 1, retail.</summary>
    public const string VersionFile = "legacey-world.json";
    /// <summary>Under the game folder: <c>world-update\&lt;version&gt;\</c> holds the patch, then <c>ready</c> once it is verified.</summary>
    public const string UpdateFolder = "world-update";
    public const string PatchFile = "world.patch";
    /// <summary>Written last, holding the patch's SHA-256, so the updater checks the patch again before it applies it.</summary>
    public const string ReadyFile = "ready";
    /// <summary>Written by the updater when applying failed: the plugin then leaves Enter open and doesn't try that patch again (same
    /// SHA-256 in <c>ready</c>), so a patch that won't apply never closes the game at every launch.</summary>
    public const string FailedFile = "failed";
    public const string UpdaterMutex = @"Local\LegACEy.WorldUpdater";

    private static readonly Regex VersionValue = new(@"""version""\s*:\s*(\d+)", RegexOptions.Compiled);

    public static string Stage(string game, int version) => Path.Combine(game, UpdateFolder, version.ToString());

    /// <summary>The game folder's world version: 1 when the file is missing or can't be read.</summary>
    public static int ReadVersion(string game)
    {
        try
        {
            var path = Path.Combine(game, VersionFile);
            if (!File.Exists(path)) return 1;
            var match = VersionValue.Match(File.ReadAllText(path));
            return match.Success && int.TryParse(match.Groups[1].Value, out var version) && version >= 1 ? version : 1;
        }
        catch (Exception) { return 1; }
    }

    /// <summary>A file's SHA-256, lower-case hex.</summary>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    public static string VersionJson(int version) => $"{{ \"version\": {version} }}";
}
