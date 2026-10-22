using Avalonia.Controls;
using Avalonia.Media;
using Rectangle = System.Drawing.Rectangle;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class PanelCompositorTests
{
    [Fact]
    public void Idle_panels_advance_with_no_change_and_no_capture() => RenderThread.Run(() =>
    {
        using var a = AvaloniaPanel.Create(() => new Border { Background = Brushes.Black }, 8, 8);
        using var b = AvaloniaPanel.Create(() => new Border { Background = Brushes.Gray }, 8, 8);
        using var c = AvaloniaPanel.Create(() => new TextBlock { Text = "idle" }, 40, 20);
        PanelCompositor.Advance();
        var captures = new[] { a.FrameCaptureCount, b.FrameCaptureCount, c.FrameCaptureCount };

        PanelCompositor.Advance();

        foreach (var panel in new[] { a, b, c })
        {
            Assert.False(panel.Changed);
            Assert.Empty(panel.Frame.DirtyRectangles);
        }
        Assert.Equal(captures, new[] { a.FrameCaptureCount, b.FrameCaptureCount, c.FrameCaptureCount });
    });

    [Fact]
    public void An_idle_advance_over_several_panels_allocates_nothing() => RenderThread.Run(() =>
    {
        using var a = AvaloniaPanel.Create(() => new Border { Background = Brushes.Black }, 8, 8);
        using var b = AvaloniaPanel.Create(() => new TextBlock { Text = "idle" }, 40, 20);
        using var c = AvaloniaPanel.Create(() => new StackPanel { Children = { new Button { Content = "OK" } } }, 60, 30);
        for (var i = 0; i < 5; i++) PanelCompositor.Advance();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) PanelCompositor.Advance();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    });

    [Fact]
    public void Changing_one_panel_changes_only_it_and_its_pixels_match_a_full_render() => RenderThread.Run(() =>
    {
        Canvas Content(IBrush square) => new()
        {
            Background = Brushes.Black,
            Children = { new Border { Width = 4, Height = 4, Background = square, [Canvas.LeftProperty] = 6.0, [Canvas.TopProperty] = 2.0 } }
        };
        using var a = AvaloniaPanel.Create(() => Content(Brushes.Gray), 16, 12);
        using var b = AvaloniaPanel.Create(() => Content(Brushes.Gray), 16, 12);
        using var c = AvaloniaPanel.Create(() => Content(Brushes.Gray), 16, 12);
        PanelCompositor.Advance();

        ((Border)((Canvas)b.Content).Children[0]).Background = Brushes.White;
        PanelCompositor.Advance();

        Assert.False(a.Changed);
        Assert.False(c.Changed);
        Assert.True(b.Changed);
        var changed = b.Frame.DirtyRectangles.Aggregate(Rectangle.Union);
        Assert.True(changed.Contains(new Rectangle(6, 2, 4, 4)), $"{changed} does not cover the changed square");
        using var full = AvaloniaPanel.Create(() => Content(Brushes.White), 16, 12);
        Assert.Equal(full.Frame.Pixels, b.Frame.Pixels);
    });

    [Fact]
    public void A_panel_shown_again_after_being_hidden_shows_its_current_content() => RenderThread.Run(() =>
    {
        var border = new Border { Background = Brushes.Black };
        using var panel = AvaloniaPanel.Create(() => border, 8, 8);
        panel.Visible = false;
        PanelCompositor.Advance();
        var captures = panel.FrameCaptureCount;

        border.Background = Brushes.White;
        PanelCompositor.Advance();
        Assert.False(panel.Changed);
        Assert.Equal(captures, panel.FrameCaptureCount);
        Assert.Equal(0, panel.Frame.Pixels[0]);

        panel.Visible = true;
        PanelCompositor.Advance();
        Assert.True(panel.Changed);
        Assert.Contains(new Rectangle(0, 0, 8, 8), panel.Frame.DirtyRectangles);
        Assert.Equal(0xff, panel.Frame.Pixels[0]);
    });

    [Fact]
    public void A_resized_or_rescaled_panel_gets_a_full_first_frame() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new Border { Background = Brushes.Gray }, 8, 6);
        PanelCompositor.Advance();

        panel.Resize(12, 10);
        PanelCompositor.Advance();
        Assert.True(panel.Changed);
        Assert.Equal((12, 10), (panel.Frame.Width, panel.Frame.Height));
        Assert.Contains(new Rectangle(0, 0, 12, 10), panel.Frame.DirtyRectangles);
        Assert.All(panel.Frame.Pixels, value => Assert.NotEqual(0, value));

        PanelCompositor.Advance();
        Assert.False(panel.Changed);

        panel.Resize(12, 10, scale: 1.5);
        PanelCompositor.Advance();
        Assert.True(panel.Changed);
        Assert.Contains(new Rectangle(0, 0, 12, 10), panel.Frame.DirtyRectangles);
    });

    [Fact]
    public void Changing_one_panel_attributes_its_layout_and_paint_to_it() => RenderThread.Run(() =>
    {
        var text = new TextBlock { Text = "first" };
        using var a = AvaloniaPanel.Create(() => new TextBlock { Text = "still" }, 60, 20);
        using var b = AvaloniaPanel.Create(() => text, 60, 20);
        PanelCompositor.Advance();

        text.Text = "second, longer";
        PanelCompositor.Advance();

        Assert.True(b.LayoutPasses > 0);
        Assert.True(b.PaintedArea > 0);
        Assert.Equal(0, a.LayoutPasses);
        Assert.Equal(0L, a.PaintedArea);
        Assert.Equal(b.PaintedArea, PanelCompositor.PaintedArea);
    });

    [Fact]
    public void An_idle_panel_reports_no_layout_passes_and_no_painted_area() => RenderThread.Run(() =>
    {
        using var a = AvaloniaPanel.Create(() => new TextBlock { Text = "idle" }, 40, 20);
        using var b = AvaloniaPanel.Create(() => new Border { Background = Brushes.Gray }, 8, 8);
        PanelCompositor.Advance();

        PanelCompositor.Advance();

        foreach (var panel in new[] { a, b })
        {
            Assert.Equal(0, panel.LayoutPasses);
            Assert.Equal(0L, panel.PaintedArea);
        }
        Assert.Equal(0L, PanelCompositor.PaintedArea);
    });

    [Fact]
    public void A_changed_advance_allocates_within_its_budget() => RenderThread.Run(() =>
    {
        var border = new Border { Background = Brushes.Gray };
        using var panel = AvaloniaPanel.Create(() => border, 16, 12);
        PanelCompositor.Advance();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            border.Background = i % 2 == 0 ? Brushes.White : Brushes.Black;
            PanelCompositor.Advance();
        }

        Assert.True(panel.Changed);
        // Measured at 6.7 KB per changed advance before attribution; attribution adds nothing to that.
        Assert.InRange((GC.GetAllocatedBytesForCurrentThread() - before) / 100, 0, 8 * 1024);
    });
}
