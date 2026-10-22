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

/// <summary>Named <see cref="FrameSamples"/>, one per phase of a frame, so the log shows where the worst frames go.</summary>
internal sealed class PhaseTimes
{
    private readonly Dictionary<string, FrameSamples> _phases = new(StringComparer.Ordinal);

    /// <summary>Records the time since <paramref name="start"/>, a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>, under the phase.</summary>
    public void Add(string phase, long start)
    {
        if (!_phases.TryGetValue(phase, out var samples)) _phases[phase] = samples = new FrameSamples();
        samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>"phase=p50/p95/max …" for each phase that ran, then forgets the samples; "-" when none ran.</summary>
    public string TakeSummary()
    {
        var parts = new List<string>();
        foreach (var pair in _phases)
            if (pair.Value.Count > 0) parts.Add($"{pair.Key}={pair.Value.TakeSummary()}");
        return parts.Count == 0 ? "-" : string.Join(" ", parts);
    }
}
