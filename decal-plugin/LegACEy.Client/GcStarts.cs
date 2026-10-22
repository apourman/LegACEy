using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Counts the runtime's garbage collections by generation, reason and kind, from its own GCStart events, so the performance log
/// can say why collections happen (allocation, low memory, induced) and not only how many.
/// </summary>
internal sealed class GcStarts : EventListener
{
    // Keyword GC (0x1) on the runtime's event source; GCStart arrives at Informational.
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private static readonly string[] Reasons =
    [
        "alloc", "induced", "low-memory", "empty", "alloc-large", "out-of-space", "out-of-space-large", "induced-not-forced",
        "stress", "induced-low-memory", "induced-compacting", "low-memory-host", "pm-full", "low-memory-host-blocking"
    ];
    private static readonly string[] Kinds = ["blocking", "background", "foreground"];
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Informational, GcKeyword);
    }

    // Runs on the runtime's event thread.
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName == null || !e.EventName.StartsWith("GCStart", StringComparison.Ordinal) || e.PayloadNames == null) return;
        var depth = Payload(e, "Depth");
        var reason = Payload(e, "Reason");
        var kind = Payload(e, "Type");
        var key = $"gen{depth}:{Name(Reasons, reason)}:{Name(Kinds, kind)}";
        lock (_counts) _counts[key] = _counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    /// <summary>"gen2:alloc:background=12 gen0:alloc:blocking=140 …", most frequent first, then starts counting again; "-" when none.</summary>
    public string TakeSummary()
    {
        lock (_counts)
        {
            if (_counts.Count == 0) return "-";
            var summary = string.Join(" ", _counts.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}={pair.Value}"));
            _counts.Clear();
            return summary;
        }
    }

    private static long Payload(EventWrittenEventArgs e, string name)
    {
        var index = e.PayloadNames!.IndexOf(name);
        return index < 0 || e.Payload == null ? -1 : Convert.ToInt64(e.Payload[index]);
    }

    private static string Name(string[] names, long value) => value >= 0 && value < names.Length ? names[value] : value.ToString();
}
