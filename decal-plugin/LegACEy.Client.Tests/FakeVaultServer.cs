using System;
using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Plugin.Vault;

namespace LegACEy.Client.Tests;

/// <summary>
/// An in-process stand-in for ACE's channel and Vault actions, for tests. It decodes real
/// request payloads and answers with real event payloads, so the client channel is exercised end to end.
/// Replies wait in a queue until Pump; a started transfer completes, and is pushed, on the pump after its delay.
/// </summary>
public sealed class FakeVaultServer : IServerChannelTransport
{
    private readonly Queue<(DateTime Due, byte[] Payload)> _outbox = new();
    private readonly List<(DateTime Due, bool Deposit, uint Guid)> _transfers = new();
    private readonly List<VaultItemView> _items;
    private readonly Func<DateTime> _clock;
    private uint _nextGuid = 0x80001000;

    public FakeVaultServer(Func<DateTime>? clock = null, IEnumerable<VaultItemView>? items = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _items = items?.ToList() ?? SampleItems().ToList();
    }

    public Action<byte[]>? Deliver { get; set; }
    public bool IsAvailable { get; set; } = true;
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(40);
    public TimeSpan TransferTime { get; set; } = TimeSpan.FromSeconds(3);
    public long Balance { get; set; } = 245;
    public int Capacity { get; set; } = 1000;
    public List<string> Received { get; } = new();
    /// <summary>Every page request, as the client sent it.</summary>
    public List<(string Search, int Offset, int Count)> ListRequests { get; } = new();
    /// <summary>Pack items the fake refuses to deposit, with the reason.</summary>
    public Dictionary<uint, string> Refused { get; } = new();
    public IReadOnlyList<VaultItemView> Items => _items;

    /// <summary>Changes the Vault the way something outside the window would (a /vault command, the website). Nothing is pushed: the test pushes it.</summary>
    public void Edit(Action<List<VaultItemView>> edit) => edit(_items);

    public bool Send(byte[] requestPayload)
    {
        if (!ChannelWire.TryDecodeRequest(requestPayload, out var id, out var action, out var body)) return false;
        Received.Add(action);
        switch (action)
        {
            case ChannelHello.Action:
                Reply(id, action, ChannelStatus.Ok, ChannelHello.Write("Preview Character", new[] { ChannelHello.Action, VaultProtocol.List, VaultProtocol.Deposit, VaultProtocol.Withdraw, VaultProtocol.Check, VaultProtocol.Move }));
                break;
            case VaultProtocol.List:
                var listReader = ChannelWire.Reader(body);
                var search = ChannelWire.ReadString(listReader);
                var offset = listReader.ReadInt32();
                var count = listReader.ReadInt32();
                ListRequests.Add((search, offset, count));
                var matches = _items.Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
                var page = matches.Skip(offset).Take(count).ToArray();
                Reply(id, action, ChannelStatus.Ok, VaultProtocol.WriteList(new VaultSnapshot(true, Balance, Capacity, _items.Count, matches.Count, page)));
                break;
            case VaultProtocol.Check:
                var checkedGuid = ChannelWire.Reader(body).ReadUInt32();
                var refusedBecause = Refused.TryGetValue(checkedGuid, out var reason) ? reason : null;
                Reply(id, action, ChannelStatus.Ok, VaultProtocol.WriteCheck(checkedGuid, refusedBecause == null, refusedBecause ?? string.Empty));
                break;
            case VaultProtocol.Move:
                var moveReader = ChannelWire.Reader(body);
                var movedGuid = moveReader.ReadUInt32();
                var toIndex = moveReader.ReadInt32();
                var movedItem = _items.FirstOrDefault(item => item.Guid == movedGuid);
                if (movedItem != null)
                {
                    _items.Remove(movedItem);
                    _items.Insert(Math.Max(0, Math.Min(toIndex, _items.Count)), movedItem);
                }
                Reply(id, action, ChannelStatus.Ok, VaultProtocol.WriteTransfer(movedItem != null, movedItem != null ? string.Empty : "That item is no longer in your Vault."));
                break;
            case VaultProtocol.Withdraw:
            case VaultProtocol.Deposit:
                var guid = ChannelWire.Reader(body).ReadUInt32();
                var deposit = action == VaultProtocol.Deposit;
                var refusal = deposit ? (Refused.TryGetValue(guid, out var depositRefusal) ? depositRefusal : null)
                    : _items.Any(item => item.Guid == guid) ? null : "That item is not in your Vault.";
                if (refusal == null) _transfers.Add((_clock() + TransferTime, deposit, guid));
                Reply(id, action, ChannelStatus.Ok, VaultProtocol.WriteTransfer(refusal == null, refusal ?? string.Empty));
                break;
            default:
                Reply(id, action, ChannelStatus.UnknownAction, ChannelWire.Body(w => ChannelWire.WriteString(w, $"The server has no '{action}' action.")));
                break;
        }
        return true;
    }

    /// <summary>Delivers due replies and finishes due transfers, pushing vault.changed for each.</summary>
    public void Pump()
    {
        var now = _clock();
        foreach (var transfer in _transfers.Where(t => t.Due <= now).ToArray())
        {
            _transfers.Remove(transfer);
            string message;
            if (transfer.Deposit)
            {
                var item = new VaultItemView(_nextGuid++, "Deposited pack item", 0x80, 1, 50, "held", "Preview Character", DateTimeOffset.UtcNow, 0x060011D4, 0, 0x06001031, 0, 0, 0);
                _items.Add(item);
                message = $"Your {item.Name} is in your Vault.";
            }
            else
            {
                var item = _items.First(i => i.Guid == transfer.Guid);
                _items.Remove(item);
                message = $"Your {item.Name} is back in your pack.";
            }
            Push(VaultProtocol.Changed, VaultProtocol.WriteChanged(true, transfer.Deposit ? "Deposited" : "Withdrawn", message, transfer.Guid));
        }
        while (_outbox.Count > 0 && _outbox.Peek().Due <= now)
            Deliver?.Invoke(_outbox.Dequeue().Payload);
    }

    public void Push(string topic, byte[] body) =>
        _outbox.Enqueue((_clock(), ChannelWire.EncodeEvent(ChannelEventKind.Push, ChannelStatus.Ok, 0, topic, body)));

    private void Reply(uint id, string action, ChannelStatus status, byte[] body) =>
        _outbox.Enqueue((_clock() + Latency, ChannelWire.EncodeEvent(ChannelEventKind.Reply, status, id, action, body)));

    public static IEnumerable<VaultItemView> SampleItems()
    {
        var deposited = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        (string Name, uint Type, uint Icon, uint Plate)[] samples =
        {
            ("Chainmail Shirt", 0x2, 0x06000FC7, 0x060011CF), ("Leather Boots", 0x2, 0x06000FAD, 0x060011F3),
            ("Gold Ring", 0x8, 0x06000FB5, 0x060011D5), ("Pendant", 0x8, 0x06000FBE, 0x060011D5),
            ("Steel Shield", 0x2, 0x06000FCB, 0x060011CF), ("Leather Cap", 0x2, 0x06000FAA, 0x060011F3),
            ("Blue Potion", 0x80, 0x06001012, 0x060011D4), ("Yellow Potion", 0x80, 0x06001013, 0x060011D4),
            ("Treasure Chest", 0x200, 0x06001020, 0x060011D4)
        };
        uint guid = 0x80000100;
        foreach (var sample in samples)
            yield return new VaultItemView(guid++, sample.Name, sample.Type, 1, 120, "held", "Arwic Wanderer", deposited, sample.Plate, 0, sample.Icon, 0, 0, 0);
    }
}
