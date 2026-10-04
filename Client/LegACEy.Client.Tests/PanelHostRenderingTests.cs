using Avalonia.Controls;
using Avalonia.Media;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class PanelHostRenderingTests
{
    [Fact]
    public void Tick_renders_a_control_to_a_bgra_frame() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(
            () => new Border { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56)) },
            16,
            16);

        panel.Tick();

        var frame = panel.Frame;
        Assert.Equal(16, frame.Width);
        Assert.Equal(16, frame.Height);
        Assert.Equal(16 * 4, frame.Stride);
        foreach (var (x, y) in new[] { (0, 0), (15, 0), (8, 8), (0, 15), (15, 15) })
        {
            var offset = (y * frame.Stride) + (x * 4);
            Assert.Equal(new byte[] { 0x56, 0x34, 0x12, 0xff }, frame.Pixels.Skip(offset).Take(4));
        }
    });

    [Fact]
    public void Tick_renders_text_over_the_background() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(
            () => new Border
            {
                Background = Brushes.Black,
                Child = new TextBlock { Text = "LegACEy", FontSize = 20, Foreground = Brushes.White }
            },
            120,
            40);

        panel.Tick();

        var frame = panel.Frame;
        var litPixels = Enumerable.Range(0, frame.Width * frame.Height)
            .Count(pixel => frame.Pixels[(pixel * 4) + 2] > 0x80);
        Assert.InRange(litPixels, 20, frame.Width * frame.Height / 2);
    });

    [Fact]
    public void Tick_reports_whether_the_frame_changed() => RenderThread.Run(() =>
    {
        var border = default(Border);
        using var panel = AvaloniaPanel.Create(() => border = new Border { Background = Brushes.Black }, 8, 8);

        Assert.False(panel.Tick());

        border!.Background = Brushes.White;
        Assert.True(panel.Tick());
        Assert.Equal(0xff, panel.Frame.Pixels[0]);
        Assert.False(panel.Tick());
    });

    [Fact]
    public void Tick_from_another_thread_is_rejected()
    {
        var panel = RenderThread.Run(() => AvaloniaPanel.Create(() => new Border(), 4, 4));
        try
        {
            Assert.Throws<InvalidOperationException>(() => panel.Tick());
        }
        finally
        {
            RenderThread.Run(panel.Dispose);
        }
    }
}
