using System;
using System.Collections.Generic;
using System.Globalization;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Millisecond timings collected between performance log lines, summarised as median, 95th percentile and maximum.</summary>
internal sealed class FrameSamples
{
    // Room for 30 s at 60 fps; a faster client grows the list once and keeps it.
    private readonly List<double> _values = new(2048);

    public int Count => _values.Count;

    public void Add(double milliseconds) => _values.Add(milliseconds);

    /// <summary>"p50/p95/max" in milliseconds, then forgets the samples; "-" when there were none.</summary>
    public string TakeSummary()
    {
        if (_values.Count == 0) return "-";
        _values.Sort();
        var summary = string.Format(CultureInfo.InvariantCulture, "{0:F2}/{1:F2}/{2:F2}",
            Percentile(0.50), Percentile(0.95), _values[_values.Count - 1]);
        _values.Clear();
        return summary;
    }

    // Nearest rank on the sorted values.
    private double Percentile(double p) => _values[Math.Max(0, (int)Math.Ceiling(p * _values.Count) - 1)];
}

/// <summary>The worst and total of a timing, for parts too many to keep every sample of.</summary>
internal sealed class PeakTimes
{
    private double _max;
    private double _total;

    public void Add(double milliseconds)
    {
        if (milliseconds > _max) _max = milliseconds;
        _total += milliseconds;
    }

    /// <summary>"max/total" in milliseconds, then starts again.</summary>
    public string TakeSummary()
    {
        var summary = string.Format(CultureInfo.InvariantCulture, "{0:F2}/{1:F1}", _max, _total);
        _max = _total = 0;
        return summary;
    }
}

/// <summary>Named phases of a frame: their times (<see cref="FrameSamples"/>) and the bytes this thread allocated in each.</summary>
internal sealed class PhaseTimes
{
    private readonly Dictionary<string, (FrameSamples Times, long[] Bytes)> _phases = new(StringComparer.Ordinal);

    /// <summary>Where a phase starts: the time and this thread's allocated bytes.</summary>
    public readonly record struct Mark(long Timestamp, long Bytes);

    public static Mark Start() => new(System.Diagnostics.Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    /// <summary>Records the time and allocation since <paramref name="start"/> under the phase.</summary>
    public void Add(string phase, Mark start)
    {
        var bytes = GC.GetAllocatedBytesForCurrentThread() - start.Bytes;
        if (!_phases.TryGetValue(phase, out var entry)) _phases[phase] = entry = (new FrameSamples(), new long[1]);
        entry.Times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start.Timestamp).TotalMilliseconds);
        entry.Bytes[0] += bytes;
    }

    /// <summary>"phase=p50/p95/max ms,KB allocated" for each phase that ran, then forgets the samples; "-" when none ran.</summary>
    public string TakeSummary()
    {
        var parts = new List<string>();
        foreach (var pair in _phases)
        {
            if (pair.Value.Times.Count == 0) continue;
            parts.Add($"{pair.Key}={pair.Value.Times.TakeSummary()},{pair.Value.Bytes[0] / 1024}KB");
            pair.Value.Bytes[0] = 0;
        }
        return parts.Count == 0 ? "-" : string.Join(" ", parts);
    }
}
