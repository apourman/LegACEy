using System;
using System.Diagnostics;
using System.Drawing;
using LegACEy.Client.PanelHost;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// An Avalonia panel drawn straight onto the game's Direct3D 9 device at a screen position, with no window chrome around it.
/// Retail surfaces are rendered from the frame event; LegACEy windows prepare their frame there and draw it later from the post-UI
/// EndScene seam.
/// </summary>
/// <remarks>
/// ponytail: the panel ticks, but nothing reaches the device yet. The Managed DirectX texture upload and quad draw (still in the
/// DecalPlugin project) become direct D3D9 vtable calls here, on a managed-pool texture.
/// </remarks>
internal sealed class ScreenSurface : IDisposable
{
    private readonly PanelFailureBudget _failureBudget = new();

    public ScreenSurface(IntPtr device, AvaloniaPanel panel)
    {
        Device = device;
        Panel = panel;
    }

    public IntPtr Device { get; }

    public AvaloniaPanel Panel { get; }

    public Point Location { get; set; }

    public bool Visible { get; set; }

    public double LastTickMilliseconds { get; private set; }

    public double LastUploadMilliseconds { get; private set; }

    public int LastDirtyRectangleCount { get; private set; }

    public Rectangle Bounds => new(Location, new Size(Panel.Frame.Width, Panel.Frame.Height));

    /// <summary>Tick Avalonia and upload the frame without drawing it.</summary>
    public void Prepare()
    {
        if (!Visible)
        {
            Panel.DrainDispatcher();
            return;
        }
        var timer = Stopwatch.StartNew();
        Panel.Tick();
        timer.Stop();
        LastTickMilliseconds = timer.Elapsed.TotalMilliseconds;
        LastDirtyRectangleCount = Panel.Frame.DirtyRectangles.Count;
        if (Panel.LastError != null)
            return;
        if (_failureBudget.Record(timer.Elapsed))
            throw new TimeoutException($"Panel tick exceeded 50 ms for {_failureBudget.ConsecutiveSlowTicks} consecutive frames ({timer.Elapsed.TotalMilliseconds:F1} ms).");
    }

    /// <summary>Draw the prepared frame at its current location.</summary>
    public void DrawNow()
    {
    }

    public void Dispose() => Panel.Dispose();

    /// <summary>Draws a 2 px outline just inside the bounds in a solid colour, then puts every device state back.</summary>
    public static void DrawOutline(IntPtr device, Rectangle bounds, Color color)
    {
    }
}
