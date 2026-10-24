using System;
using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>
/// Sends item moves one at a time. Retail refuses a move sent while an earlier request still waits on the server ("You can only move
/// or use one item at a time"), so a set dragged into a pack moved only its first item. The next move goes once the client has no
/// request in flight, or after <see cref="Timeout"/> when it never clears.
/// </summary>
public sealed class InventoryMoveQueue
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly Action<uint, uint, int> _send;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<bool> _pending;
    private readonly Queue<(uint Item, uint Container, int Slot)> _waiting = new();
    private TimeSpan? _sentAt;

    /// <param name="pending">True while the client waits on the server for the last move sent.</param>
    public InventoryMoveQueue(Action<uint, uint, int> send, Func<TimeSpan> clock, Func<bool> pending)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pending = pending ?? throw new ArgumentNullException(nameof(pending));
    }

    public void Move(uint itemId, uint containerId, int slotIndex)
    {
        _waiting.Enqueue((itemId, containerId, slotIndex));
        Pump();
    }

    /// <summary>Sends the next move when no request is in flight. Call each frame.</summary>
    public void Pump()
    {
        if (_waiting.Count == 0) return;
        // Only a move sent here is waited on; the first goes at once, as a single move always did.
        if (_sentAt is { } sentAt && _clock() - sentAt < Timeout && _pending()) return;
        var (item, container, slot) = _waiting.Dequeue();
        _sentAt = _clock();
        _send(item, container, slot);
    }

    /// <summary>Logoff: moves not yet sent are dropped.</summary>
    public void Clear()
    {
        _waiting.Clear();
        _sentAt = null;
    }
}
