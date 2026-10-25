using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// One changed or new dat record: which dat, its file id, its iteration, the format version its bytes are in (the high word of the dat
/// entry's flags, which the client unpacks by), and its bytes, always decompressed: the overlay serves them as uncompressed records.
/// </summary>
public sealed record WorldPatchRecord(string Dat, uint Id, int Iteration, ushort Version, byte[] Data);

/// <summary>
/// A world patch: every dat record that differs from retail, and the world version (the cell dat edition) it brings a game to. The client
/// never writes it into the dats: <see cref="WorldPatchIndex"/> serves it to the client's dat reads. Also compiled into the patch builder.
/// </summary>
public sealed class WorldPatch(int version, IReadOnlyList<WorldPatchRecord> records)
{
    public int Version { get; } = version;
    public IReadOnlyList<WorldPatchRecord> Records { get; } = records;

    internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("LWP3");
    // Only the game's own dat files, by bare name: a patch never names a path.
    internal static readonly Regex DatName = new(@"^client_[A-Za-z0-9_]+\.dat$", RegexOptions.Compiled);

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
            writer.Write(record.Version);
            writer.Write(record.Data.Length);
            writer.Write(record.Data);
        }
    }

    /// <summary>
    /// Whether the client already holds this retail record when a world switches on at character select, so a patch's copy of it shows
    /// only after a relaunch (mappers are held even at plugin start, so may never show). From the portal spike's cache probe: the
    /// connecting screen's textures and the font atlases by id, and the types held there whose ids it didn't log by prefix: Region (13),
    /// UI layouts (21, in the local dat), master property (39), fonts (40) and mappers (22, 25, 26, 27). Everything else (new ids, world
    /// art, character select's own art, the cell dat) shows at once. A new id under one of the prefixes counts too: only retail knows it's new.
    /// </summary>
    public static bool HeldAtSwitchOn(string dat, uint id) => !dat.StartsWith("client_cell_", StringComparison.Ordinal) && (
        (id >> 24) is 0x13 or 0x21 or 0x39 or 0x40 or 0x22 or 0x25 or 0x26 or 0x27
        || id is 0x06001343 or 0x06004CA9 or 0x06004D68 or >= 0x0600195C and <= 0x06001979
            or 0x06005ECF or 0x06005ED0 or 0x06005EE7 or 0x06005EE8 or 0x06005EED or 0x06005EEE or 0x06005EF4);

    /// <summary>A file's SHA-256, lower-case hex.</summary>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>Where one record's bytes sit in a patch file, its iteration and its format version.</summary>
public readonly record struct WorldPatchEntry(int Iteration, ushort Version, long Offset, int Length);

/// <summary>
/// A patch file's index: every record's dat, id, iteration, offset and length, read once, with the file kept open so a record's bytes are
/// read only when the client asks for them. Lookups and reads are safe from several threads at once (the client's loader threads).
/// </summary>
public sealed class WorldPatchIndex : IDisposable
{
    private readonly Dictionary<string, Dictionary<uint, WorldPatchEntry>> _dats;
    // Only its handle is used after indexing: positional reads, safe from several threads.
    private readonly FileStream _file;

    public int Version { get; }
    public string Path { get; }
    public int Count { get; }

    private WorldPatchIndex(string path, int version, Dictionary<string, Dictionary<uint, WorldPatchEntry>> dats, int count, FileStream file)
    {
        Path = path;
        Version = version;
        _dats = dats;
        Count = count;
        _file = file;
    }

    /// <summary>Indexes the patch at <paramref name="path"/>; throws <see cref="InvalidDataException"/> when it isn't one, names anything
    /// but a game dat, or is cut short.</summary>
    public static WorldPatchIndex Open(string path)
    {
        // Shared for delete, so a newer patch can replace the cache's files while this one is still served. Kept open for the reads.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (Encoding.ASCII.GetString(reader.ReadBytes(WorldPatch.Magic.Length)) != "LWP3") throw new InvalidDataException("Not a LegACEy world patch (LWP3).");
            var version = reader.ReadInt32();
            var count = reader.ReadInt32();
            if (version < 1 || count < 0) throw new InvalidDataException("A world patch with a bad header.");
            var dats = new Dictionary<string, Dictionary<uint, WorldPatchEntry>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var dat = reader.ReadString();
                if (!WorldPatch.DatName.IsMatch(dat)) throw new InvalidDataException($"A world patch naming {dat}, not a game dat.");
                var id = reader.ReadUInt32();
                var iteration = reader.ReadInt32();
                var format = reader.ReadUInt16();
                var length = reader.ReadInt32();
                var offset = stream.Position;
                if (length < 0 || offset + length > stream.Length) throw new InvalidDataException("A world patch cut short.");
                stream.Seek(length, SeekOrigin.Current);
                if (!dats.TryGetValue(dat, out var ids)) dats[dat] = ids = new Dictionary<uint, WorldPatchEntry>();
                ids[id] = new WorldPatchEntry(iteration, format, offset, length);
            }
            return new WorldPatchIndex(path, version, dats, count, stream);
        }
        catch (EndOfStreamException)
        {
            stream.Dispose();
            throw new InvalidDataException("A world patch cut short.");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The patch's records for one dat, by id; null when it has none.</summary>
    public IReadOnlyDictionary<uint, WorldPatchEntry>? Records(string dat) => _dats.TryGetValue(dat, out var ids) ? ids : null;

    public IEnumerable<string> Dats => _dats.Keys;

    /// <summary>A record's bytes, read from the file.</summary>
    public byte[] Read(WorldPatchEntry entry)
    {
        var bytes = new byte[entry.Length];
        var done = 0;
        while (done < bytes.Length)
        {
            var read = RandomAccess.Read(_file.SafeFileHandle, bytes.AsSpan(done), entry.Offset + done);
            if (read == 0) throw new InvalidDataException("A world patch cut short.");
            done += read;
        }
        return bytes;
    }

    public void Dispose() => _file.Dispose();
}
