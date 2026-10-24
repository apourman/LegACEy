using System;
using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>
/// Sends item moves one at a time. Retail drops a move sent while an earlier one still waits on the server, so a set dragged into a
/// pack moved only its first item. The next move goes once the inventory changes (the server answered the last one), or after
/// <see cref="Timeout"/> when no answer comes (a refused move).
/// </summary>
public sealed class InventoryMoveQueue
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly Action<uint, uint, int> _send;
    private readonly Func<TimeSpan> _clock;
    private readonly Queue<(uint Item, uint Container, int Slot)> _waiting = new();
    private TimeSpan? _sentAt;
    private bool _answered;

    public InventoryMoveQueue(Action<uint, uint, int> send, Func<TimeSpan> clock)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void Move(uint itemId, uint containerId, int slotIndex)
    {
        _waiting.Enqueue((itemId, containerId, slotIndex));
        Pump();
    }

    /// <summary>The inventory changed: the move in flight has landed.</summary>
    public void Answered() => _answered = true;

    /// <summary>Sends the next move when none is in flight. Call each frame, after the inventory flush.</summary>
    public void Pump()
    {
        if (_sentAt is { } sentAt && !_answered && _clock() - sentAt < Timeout) return;
        _sentAt = null;
        if (_waiting.Count == 0) return;
        var (item, container, slot) = _waiting.Dequeue();
        _sentAt = _clock();
        _answered = false;
        _send(item, container, slot);
    }

    /// <summary>Logoff: moves not yet sent are dropped.</summary>
    public void Clear()
    {
        _waiting.Clear();
        _sentAt = null;
    }
}
