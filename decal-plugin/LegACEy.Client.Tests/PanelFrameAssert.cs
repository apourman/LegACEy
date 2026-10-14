using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>Frame checks shared by the window tests.</summary>
internal static class PanelFrameAssert
{
    /// <summary>
    /// A panel's frame must match a fresh draw of the same state, within two levels per channel. The tolerance exists because Avalonia blends a
    /// redrawn edge over the pixels it replaces: after a control is replaced, the rims of every slot differed from a fresh draw by one level
    /// (measured with the shortcut bar, changed and unchanged slots alike). A change of content differs by far more. Where an exact match is
    /// the point, compare the bytes exactly instead.
    /// </summary>
    public static void AssertDrawnLike(byte[] fresh, AvaloniaPanel host)
    {
        var actual = host.Frame.Pixels.ToArray();
        var stride = host.Frame.Stride / 4;
        var differing = new List<(int X, int Y)>();
        for (var i = 0; i + 4 <= fresh.Length; i += 4)
            if (Enumerable.Range(0, 4).Any(channel => Math.Abs(fresh[i + channel] - actual[i + channel]) > 2))
                differing.Add(((i / 4) % stride, (i / 4) / stride));
        if (differing.Count == 0) return;
        Assert.Fail($"{differing.Count} pixels differ, from ({differing.Min(p => p.X)},{differing.Min(p => p.Y)}) to ({differing.Max(p => p.X)},{differing.Max(p => p.Y)}).");
    }

    /// <summary>Every visible control lies inside the window, except scrolled-out cells, which the grid's viewport clips.</summary>
    public static void AssertNothingOutsideFrame(AvaloniaPanel host)
    {
        var window = new Rect(host.Content.Bounds.Size);
        foreach (var visual in host.Content.GetVisualDescendants().OfType<Visual>())
        {
            if (!visual.IsEffectivelyVisible || visual.GetVisualAncestors().OfType<ScrollViewer>().Any()) continue;
            var origin = visual.TranslatePoint(default, host.Content);
            if (origin == null) continue;
            var bounds = new Rect(origin.Value, visual.Bounds.Size);
            Assert.True(window.Contains(bounds), $"{visual.GetType().Name} at {bounds} lies outside the window {window}");
        }
    }
}
