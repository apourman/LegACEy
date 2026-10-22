using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
    private static readonly PanelFailureBudget StepBudget = new();
    private static readonly Stopwatch StepClock = new();

    internal static void Register(AvaloniaPanel panel) => Panels.Add(panel);

    internal static void Unregister(AvaloniaPanel panel) => Panels.Remove(panel);

    /// <summary>
    /// Pump Avalonia's work and run its render timer once for every panel, then capture each shown panel whose content changed.
    /// Afterwards each panel's <see cref="AvaloniaPanel.Changed"/> and its frame's dirty rectangles say what this advance changed.
    /// </summary>
    /// <remarks>
    /// A failure goes to the error handler of the panel that raised it (see <see cref="Step"/>), as a capture's does. A failure in the
    /// shared work that no one panel raised is thrown, and so is a shared step over the tick budget: more than 50 ms for three frames
    /// in a row.
    /// </remarks>
    public static void Advance()
    {
        // Nothing to run, and before the first panel Avalonia may not be initialized: touching its dispatcher would bind it here.
        if (Panels.Count == 0) return;
        Dispatcher.UIThread.VerifyAccess();
        StepClock.Restart();
        Step();
        StepClock.Stop();
        if (StepBudget.Record(StepClock.Elapsed))
            throw new TimeoutException($"Avalonia's shared step exceeded 50 ms for {StepBudget.ConsecutiveSlowTicks} consecutive frames ({StepClock.Elapsed.TotalMilliseconds:F1} ms).");
        Capturing.AddRange(Panels);
        try
        {
            foreach (var panel in Capturing) panel.Capture();
        }
        finally { Capturing.Clear(); }
    }

    /// <summary>
    /// Hide and show panels as asked, then drain queued Avalonia work and run one render-timer tick. A failure raised by one panel's
    /// content goes to that panel's error handler; one that no panel raised is thrown.
    /// </summary>
    internal static void Step()
    {
        try { StepCore(); }
        catch (Exception exception) when (OwnerOf(exception) is { } owner)
        {
            // The owner's handler closes it (its plugin turns off) or the client UI; then the panels left run again. One whose
            // handler left it failing fails again here, and that is thrown.
            owner.ReportError(exception);
            for (var i = 0; i < Panels.Count; i++) Panels[i].Resume();
        }
    }

    private static void StepCore()
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

    /// <summary>
    /// The panel whose content raised <paramref name="exception"/>, or null. Avalonia lays out and paints every window in one pass,
    /// so the stack names no window: the innermost control on it (or the control a lambda on it belongs to) whose type only one panel
    /// shows picks that panel. Avalonia's own controls are in every panel, so they pick one only when one panel is registered.
    /// </summary>
    /// <remarks>
    /// ponytail: the stack attributes the failure, not a separate layout and paint per panel, which Avalonia doesn't offer. A frame
    /// inlined away in Release attributes less; what is left unattributed is thrown, and the client turns off the plugin whose code is
    /// on the stack, or else its own UI.
    /// </remarks>
    private static AvaloniaPanel? OwnerOf(Exception exception)
    {
        for (var error = exception; error != null; error = error.InnerException)
            foreach (var frame in new StackTrace(error).GetFrames())
                for (var type = frame.GetMethod()?.DeclaringType; type != null; type = type.DeclaringType)
                {
                    if (!typeof(Visual).IsAssignableFrom(type)) continue;
                    var showing = Panels.Where(panel => panel.Content.GetSelfAndVisualDescendants().Any(type.IsInstanceOfType)).Take(2).ToList();
                    if (showing.Count == 1) return showing[0];
                }
        return null;
    }
}
