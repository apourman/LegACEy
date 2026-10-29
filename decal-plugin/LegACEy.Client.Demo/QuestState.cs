using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// A contract the player holds, as the server's ContractTracker describes it: its stage (1 available, 2 in progress, 3 done, 4 and up a
/// progress count of stage − 4), and the seconds until its timer ends and until it can be repeated, counted from <see cref="Received"/>.
/// </summary>
public readonly record struct Contract(uint Id, uint Stage, double DoneIn, double RepeatsIn, double Received);

/// <summary>
/// The player's contracts as the server reports them, for the quests window: the full table at login (SendClientContractTrackerTable, game
/// event 0x0314) and one contract after each change (SendClientContractTracker, 0x0315), whose delete flag drops it. The texts are retail's,
/// as gmContractsUI writes them (our disassembly of the installed end-of-retail acclient.exe).
/// </summary>
public sealed class QuestState
{
    private const int GameEventMessage = 0xF7B0;
    internal const uint TableEvent = 0x0314, ContractEvent = 0x0315;
    private const uint InProgressStage = 2, DoneStage = 3, ProgressStage = 4;

    private readonly Dictionary<uint, Contract> _contracts = new();
    private readonly Func<double> _clock;

    /// <param name="clock">Seconds on a steady clock; the stopwatch's when not given.</param>
    public QuestState(Func<double>? clock = null) => _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    public IReadOnlyCollection<Contract> Contracts => _contracts.Values;

    /// <summary>Now, on the clock the contracts' <see cref="Contract.Received"/> counts on.</summary>
    public double Now => _clock();

    public event Action? Changed;

    public void Clear()
    {
        _contracts.Clear();
        Changed?.Invoke();
    }

    /// <summary>Reads a server message, if it is one the state follows. A message cut short is ignored.</summary>
    public void Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        if (messageType != GameEventMessage) return;
        if (raw.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == (uint)messageType) raw = raw.Slice(4);
        try
        {
            var reader = new MessageReader(raw);
            reader.Skip(8); // the player and the event sequence
            switch (reader.UInt32())
            {
                case TableEvent:
                    var count = reader.Header();
                    var table = new List<Contract>(count);
                    for (var i = 0; i < count; i++)
                    {
                        reader.Skip(4); // the key, which is the contract's id
                        table.Add(Read(ref reader));
                    }
                    _contracts.Clear();
                    foreach (var contract in table) _contracts[contract.Id] = contract;
                    break;
                case ContractEvent:
                    var one = Read(ref reader);
                    if (reader.UInt32() != 0) _contracts.Remove(one.Id);
                    else _contracts[one.Id] = one;
                    break;
                default:
                    return;
            }
        }
        catch (ArgumentOutOfRangeException) { return; }
        Changed?.Invoke();
    }

    private Contract Read(ref MessageReader reader)
    {
        reader.Skip(4); // version
        var id = reader.UInt32();
        var stage = reader.UInt32();
        var doneIn = BitConverter.Int64BitsToDouble(reader.Int64());
        var repeatsIn = BitConverter.Int64BitsToDouble(reader.Int64());
        return new Contract(id, stage, doneIn, repeatsIn, _clock());
    }

    // Retail's texts.

    /// <summary>
    /// The Status column, as gmContractsUI::FillProgressString writes it: Available, In Progress, Done, "Done (2d 3h to Repeat)" while the
    /// repeat waits, and for a progress count the contract's own format (such as "%d/20 Tuskers"), or In Progress when it has none.
    /// </summary>
    public static string Status(Contract contract, ContractInfo? info, double now)
    {
        switch (contract.Stage)
        {
            case 1: return "Available";
            case InProgressStage: return "In Progress";
            case DoneStage:
                if (contract.RepeatsIn > 0)
                {
                    var left = contract.RepeatsIn - (now - contract.Received);
                    return left > 0 ? $"Done ({Duration(left)} to Repeat)" : "Available";
                }
                return info?.Repeats == true ? "Available" : "Done";
            case >= ProgressStage:
                var format = info?.DescriptionProgress ?? string.Empty;
                return format.Length == 0 ? "In Progress" : Progress(format, contract.Stage - ProgressStage);
            default: return string.Empty;
        }
    }

    /// <summary>The Timed line: None without a timer, then the time left, then Finished.</summary>
    public static string Timed(Contract contract, ContractInfo? info, double now)
    {
        if (info?.Timed != true) return "None";
        var left = contract.DoneIn - (now - contract.Received);
        return left > 0 ? Duration(left) : "Finished";
    }

    /// <summary>
    /// Who to see, and where: the ending NPC while the contract is under way (or when it names no starting NPC), the starting NPC otherwise.
    /// </summary>
    public static (string Name, uint Cell) Contact(Contract contract, ContractInfo info)
    {
        var underWay = contract.Stage == InProgressStage || contract.Stage >= ProgressStage;
        return info.NpcEnd.Length > 0 && (underWay || info.NpcStart.Length == 0) ? (info.NpcEnd, info.EndCell) : (info.NpcStart, info.StartCell);
    }

    /// <summary>
    /// A cell as retail writes a contract's location: empty for none, Indoors for a dungeon or building cell, else the map coordinates of
    /// the outdoor cell's centre, such as "21.5S, 1.7W" (a coordinate of exactly 0 has no letter).
    /// </summary>
    public static string Location(uint cell)
    {
        if (cell == 0) return string.Empty;
        var index = cell & 0xFFFF;
        if (index is < 1 or > 0x40) return "Indoors";
        var east = ((int)(cell >> 24) * 8 + (int)(index - 1) / 8 - 1024) * 0.1 + 0.5;
        var north = ((int)((cell >> 16) & 0xFF) * 8 + (int)(index - 1) % 8 - 1024) * 0.1 + 0.5;
        return Coordinates(north, east);
    }

    /// <summary>"21.5S, 1.7W": north then east, negatives south and west, retail's "%.1f%s, %.1f%s".</summary>
    public static string Coordinates(double north, double east)
    {
        static string Letter(double value, char positive, char negative) => value > 0 ? positive.ToString() : value < 0 ? negative.ToString() : string.Empty;
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0}{1}, {2:0.0}{3}", Math.Abs(north), Letter(north, 'N', 'S'), Math.Abs(east), Letter(east, 'E', 'W'));
    }

    /// <summary>Retail's time span: months of 30 days, days, hours and minutes when not zero, then the seconds, such as "2d 3h 0s".</summary>
    public static string Duration(double seconds)
    {
        var left = (long)Math.Max(0, seconds);
        var text = new StringBuilder();
        foreach (var (size, unit) in new[] { (2592000L, "mo"), (86400L, "d"), (3600L, "h"), (60L, "m") })
        {
            if (left >= size) text.Append(left / size).Append(unit).Append(' ');
            left %= size;
        }
        return text.Append(left).Append('s').ToString();
    }

    /// <summary>A C format with one %d, as the contract table writes them.</summary>
    private static string Progress(string format, uint count)
    {
        var at = format.IndexOf("%d", StringComparison.Ordinal);
        return at < 0 ? format : format.Substring(0, at) + count.ToString(CultureInfo.InvariantCulture) + format.Substring(at + 2);
    }
}
