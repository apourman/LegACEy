using System;
using System.Threading.Tasks;

namespace LegACEy.Client.GameArt;

/// <summary>
/// The indoor plans of the current landblock and the one before it. A plan is built once, on the thread pool, so the UI thread
/// only ever awaits it. Dungeons often span portals into the next landblock, so the last one stays too.
/// </summary>
public sealed class IndoorPlanCache
{
    private readonly Func<int, int, IndoorPlan> _build;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private (int X, int Y, Task<IndoorPlan> Plan)? _current;
    private (int X, int Y, Task<IndoorPlan> Plan)? _previous;

    /// <param name="log">Told why a landblock's plan could not be built.</param>
    public IndoorPlanCache(Func<int, int, IndoorPlan> build, Action<string>? log = null)
    {
        _build = build;
        _log = log;
    }

    /// <summary>
    /// The landblock's plan: the current one while the player stays in it (a failed one too, so a caller that asks every frame does
    /// not rebuild it), else the previous one, else built now on the thread pool. A failed plan is not kept once the player leaves
    /// its landblock; the next request builds it again.
    /// </summary>
    public Task<IndoorPlan> PlanAsync(int x, int y)
    {
        lock (_lock)
        {
            if (_current is { } current && current.X == x && current.Y == y) return current.Plan;
            if (_previous is { } previous && previous.X == x && previous.Y == y && !previous.Plan.IsFaulted)
            {
                _previous = Kept(_current);
                _current = previous;
                return previous.Plan;
            }

            _previous = Kept(_current);
            _current = (x, y, Task.Run(() => Build(x, y)));
            return _current.Value.Plan;
        }
    }

    private IndoorPlan Build(int x, int y)
    {
        try
        {
            return _build(x, y);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"The indoor plan of landblock {x:X2}{y:X2} could not be built: {exception.Message}");
            throw;
        }
    }

    /// <summary>An entry that has not failed, else null: a failed entry leaving the current slot is dropped.</summary>
    private static (int X, int Y, Task<IndoorPlan> Plan)? Kept((int X, int Y, Task<IndoorPlan> Plan)? entry) =>
        entry is { } kept && !kept.Plan.IsFaulted ? kept : null;
}
