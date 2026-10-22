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
