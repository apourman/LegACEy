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
    private readonly object _lock = new();
    private (int X, int Y, Task<IndoorPlan> Plan)? _current;
    private (int X, int Y, Task<IndoorPlan> Plan)? _previous;

    public IndoorPlanCache(Func<int, int, IndoorPlan> build)
    {
        _build = build;
    }

    /// <summary>The landblock's plan: cached if it is the current or the previous landblock, otherwise built now on the thread pool.</summary>
    public Task<IndoorPlan> PlanAsync(int x, int y)
    {
        lock (_lock)
        {
            if (_current is { } current && current.X == x && current.Y == y) return current.Plan;
            if (_previous is { } previous && previous.X == x && previous.Y == y)
            {
                _previous = _current;
                _current = previous;
                return previous.Plan;
            }

            _previous = _current;
            _current = (x, y, Task.Run(() => _build(x, y)));
            return _current.Value.Plan;
        }
    }
}
