using System;

namespace LegACEy.Client.PanelHost;

/// <summary>Tracks the consecutive slow-tick budget for one panel.</summary>
public sealed class PanelFailureBudget
{
    public static readonly TimeSpan MaximumTick = TimeSpan.FromMilliseconds(50);
    public const int ConsecutiveSlowTickLimit = 3;

    private int _consecutiveSlowTicks;

    public int ConsecutiveSlowTicks => _consecutiveSlowTicks;

    /// <summary>Returns true after an error or three consecutive ticks taking more than 50 ms.</summary>
    public bool Record(TimeSpan elapsed, Exception? error = null)
    {
        if (error != null) return true;
        if (elapsed > MaximumTick)
            _consecutiveSlowTicks++;
        else
            _consecutiveSlowTicks = 0;
        return _consecutiveSlowTicks >= ConsecutiveSlowTickLimit;
    }
}
