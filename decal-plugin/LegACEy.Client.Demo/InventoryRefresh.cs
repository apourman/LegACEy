using System;

namespace LegACEy.Client.Demo;

/// <summary>
/// Keeps an inventory snapshot current. Marks are cheap and a flush rebuilds at most once; <see cref="Changed"/> is raised
/// only when the rebuilt snapshot differs. A failing read keeps the last snapshot and stays stale, and is logged once per
/// failure streak.
/// </summary>
public sealed class InventoryRefresh
{
    private readonly Func<InventorySnapshot> _read;
    private readonly Action<string> _log;
    private bool _stale;
    private bool _failing;

    public InventoryRefresh(Func<InventorySnapshot> read, Action<string> log)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public InventorySnapshot Snapshot { get; private set; } = InventorySnapshot.Empty;
    public event Action? Changed;

    public void MarkStale() => _stale = true;

    /// <summary>Rebuilds the snapshot if it is stale, and raises <see cref="Changed"/> if the content differs.</summary>
    public void Flush()
    {
        if (!_stale) return;
        InventorySnapshot next;
        try { next = _read(); }
        catch (Exception exception)
        {
            if (!_failing) _log($"Inventory read failed; keeping the last snapshot and retrying: {exception.Message}");
            _failing = true;
            return;
        }
        _stale = false;
        _failing = false;
        if (next.Equals(Snapshot)) return;
        Snapshot = next;
        Changed?.Invoke();
    }

    /// <summary>Logoff: the inventory is gone, so the snapshot empties without a change event.</summary>
    public void Reset()
    {
        Snapshot = InventorySnapshot.Empty;
        _stale = false;
        _failing = false;
    }
}
