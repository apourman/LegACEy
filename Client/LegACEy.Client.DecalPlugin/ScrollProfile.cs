using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Bounded diagnostic counters for game callbacks. No per-sample disk I/O.</summary>
internal sealed class ScrollProfile
{
    internal const string Prefix = "[DEBUG-scroll07]";
    private const int MaximumMetrics = 256;
    private readonly Dictionary<(string Surface, string Stage, int Thread), Metric> _metrics = new();
    private int _dropped;

    public void Record(string surface, string stage, double milliseconds)
    {
        lock (_metrics) RecordCore(surface, stage, milliseconds);
    }

    private void RecordCore(string surface, string stage, double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return;
        var key = (surface, stage, Thread.CurrentThread.ManagedThreadId);
        if (!_metrics.TryGetValue(key, out var metric))
        {
            if (_metrics.Count >= MaximumMetrics) { _dropped++; return; }
            _metrics.Add(key, metric = new Metric());
        }
        metric.Count++;
        metric.Total += milliseconds;
        metric.Maximum = Math.Max(metric.Maximum, milliseconds);
        if (milliseconds >= 8) metric.Over8++;
        if (milliseconds >= 16) metric.Over16++;
    }

    public string Snapshot(int processId, double intervalSeconds, DateTime utcNow)
    {
        lock (_metrics) return SnapshotCore(processId, intervalSeconds, utcNow);
    }

    private string SnapshotCore(int processId, double intervalSeconds, DateTime utcNow)
    {
        var text = new StringBuilder();
        var header = Prefix + " utc=" + utcNow.ToString("O", CultureInfo.InvariantCulture) + " pid=" + processId;
        text.Append(header).Append(" intervalSeconds=").Append(intervalSeconds.ToString("F3", CultureInfo.InvariantCulture))
            .Append(" droppedMetrics=").Append(_dropped).AppendLine();
        foreach (var pair in _metrics)
        {
            var metric = pair.Value;
            text.Append(header).Append(" thread=").Append(pair.Key.Thread)
                .Append(" surface=").Append(pair.Key.Surface).Append(" stage=").Append(pair.Key.Stage)
                .Append(" count=").Append(metric.Count)
                .Append(" totalMs=").Append(metric.Total.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" avgMs=").Append((metric.Total / metric.Count).ToString("F3", CultureInfo.InvariantCulture))
                .Append(" maxMs=").Append(metric.Maximum.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" ge8ms=").Append(metric.Over8).Append(" ge16ms=").Append(metric.Over16).AppendLine();
        }
        _metrics.Clear();
        _dropped = 0;
        return text.ToString();
    }

    private sealed class Metric
    {
        public int Count;
        public double Total;
        public double Maximum;
        public int Over8;
        public int Over16;
    }
}
