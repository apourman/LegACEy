using System.Collections.Generic;
using Avalonia.Headless;
using Avalonia.Threading;

namespace LegACEy.Client.PanelHost;

/// <summary>
/// Runs Avalonia once per game frame for every panel: one dispatcher pump and one render-timer tick, then a capture of each shown
/// panel whose content changed.
/// </summary>
/// <remarks>
/// Avalonia's jobs and the headless render timer are shared by every window: a pump lays out and commits all of them, and a timer
/// tick paints all of them. Run per panel, the panel run first in a frame paid for the others. Panels register as they are created
/// and unregister as they are disposed; like the panels, the compositor is used only on the thread that initialized Avalonia.
/// </remarks>
public static class PanelCompositor
{
    private static readonly List<AvaloniaPanel> Panels = new();
    // Advance walks a copy, reused frame to frame: a panel's error handler may dispose panels mid-walk.
    private static readonly List<AvaloniaPanel> Capturing = new();

    internal static void Register(AvaloniaPanel panel) => Panels.Add(panel);

    internal static void Unregister(AvaloniaPanel panel) => Panels.Remove(panel);

    /// <summary>
    /// Pump Avalonia's work and run its render timer once for every panel, then capture each shown panel whose content changed.
    /// Afterwards each panel's <see cref="AvaloniaPanel.Changed"/> and its frame's dirty rectangles say what this advance changed.
    /// </summary>
    /// <remarks>
    /// A capture's failure goes to its panel's error handler. A failure in the shared work belongs to no one panel, so it is thrown.
    /// </remarks>
    public static void Advance()
    {
        // Nothing to run, and before the first panel Avalonia may not be initialized: touching its dispatcher would bind it here.
        if (Panels.Count == 0) return;
        Dispatcher.UIThread.VerifyAccess();
        Step();
        Capturing.AddRange(Panels);
        try
        {
            foreach (var panel in Capturing) panel.Capture();
        }
        finally { Capturing.Clear(); }
    }

    /// <summary>Hide and show panels as asked, then drain queued Avalonia work and run one render-timer tick.</summary>
    internal static void Step()
    {
        var settle = false;
        for (var i = 0; i < Panels.Count; i++) settle |= Panels[i].BeginStep();
        if (settle)
        {
            // Showing or resizing a headless window first commits its composition target; advance that commit before the tick paints
            // the latest content.
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
        // Let Avalonia process layout and drawing invalidations before a capture decides whether the rendered frame needs copying.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
