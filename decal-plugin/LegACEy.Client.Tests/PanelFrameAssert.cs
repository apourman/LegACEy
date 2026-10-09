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
