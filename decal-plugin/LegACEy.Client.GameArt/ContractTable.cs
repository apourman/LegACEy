using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LegACEy.Client.GameArt;

/// <summary>
/// A contract as the portal DAT's contract table describes it: its name and texts, who starts and ends it, whether it has a repeat or a timer
/// quest flag, and the cells of its starting NPC, ending NPC and quest area (0 when it gives none).
/// </summary>
public sealed class ContractInfo(
    string name, string description, string descriptionProgress, string npcStart, string npcEnd, bool repeats, bool timed,
    uint startCell, uint endCell, uint questCell)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    /// <summary>A format with one %d, the progress count, or empty when the contract counts nothing.</summary>
    public string DescriptionProgress { get; } = descriptionProgress;
    public string NpcStart { get; } = npcStart;
    public string NpcEnd { get; } = npcEnd;
    public bool Repeats { get; } = repeats;
    public bool Timed { get; } = timed;
    public uint StartCell { get; } = startCell;
    public uint EndCell { get; } = endCell;
    public uint QuestCell { get; } = questCell;
}

/// <summary>
/// The portal DAT's contract table (file 0x0E00001D), read as ACE.DatLoader's ContractTable and Contract read it: a file id, then a packed
/// hash table of contract id and contract. Each contract is two words, twelve strings (a 16-bit length and the bytes, each padded to four)
/// and three positions (a cell id and a 28-byte frame).
/// </summary>
public static class ContractTable
{
    public const uint FileId = 0x0E00001D;

    public static IReadOnlyDictionary<uint, ContractInfo> Parse(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the table's own id
        var count = reader.ReadUInt16();
        reader.ReadUInt16(); // the bucket size
        var contracts = new Dictionary<uint, ContractInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadUInt32();
            reader.ReadUInt32(); // version
            reader.ReadUInt32(); // the contract's own id
            var name = Text(reader);
            var description = Text(reader);
            var progress = Text(reader);
            var npcStart = Text(reader);
            var npcEnd = Text(reader);
            for (var flag = 0; flag < 4; flag++) Text(reader); // stamped, started, finished and progress quest flags
            var timed = Text(reader).Length > 0;
            var repeats = Text(reader).Length > 0;
            contracts[key] = new ContractInfo(name, description, progress, npcStart, npcEnd, repeats, timed, Cell(reader), Cell(reader), Cell(reader));
        }
        return contracts;
    }

    private static string Text(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var bytes = reader.ReadBytes(length);
        reader.ReadBytes((4 - (2 + length) % 4) % 4);
        var text = new StringBuilder(length);
        foreach (var value in bytes) text.Append(Windows1252.ToChar(value));
        return text.ToString();
    }

    private static uint Cell(BinaryReader reader)
    {
        var cell = reader.ReadUInt32();
        reader.ReadBytes(28); // origin and orientation
        return cell;
    }
}
