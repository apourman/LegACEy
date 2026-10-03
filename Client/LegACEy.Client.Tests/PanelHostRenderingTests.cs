using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Threading;
using LegACEy.Client.PanelHost;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(LegACEy.Client.Tests.TestAppBuilder))]

namespace LegACEy.Client.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class TestApplication : Application
{
}

public sealed class PanelHostRenderingTests
{
    [AvaloniaFact]
    public void Tick_renders_a_control_to_a_bgra_frame()
    {
        using var panel = AvaloniaPanel.Create(
            new Border { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56)) },
            16,
            16);

        panel.Tick(TimeSpan.FromMilliseconds(16));

        var frame = panel.Frame;
        Assert.Equal(16, frame.Width);
        Assert.Equal(16, frame.Height);
        var center = (8 * frame.Stride) + (8 * 4);
        Assert.Equal(0x56, frame.Pixels[center]);
        Assert.Equal(0x34, frame.Pixels[center + 1]);
        Assert.Equal(0x12, frame.Pixels[center + 2]);
        Assert.Equal(0xff, frame.Pixels[center + 3]);
    }
}
