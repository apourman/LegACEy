using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class WindowOpacitiesTests
{
    [Fact]
    public void A_window_draws_at_its_own_opacity_or_else_all_windows_kept_per_character()
    {
        var store = new MemoryWindowPositionStore();
        var opacities = new WindowOpacities(store);
        Assert.Equal(100, opacities.Of("server", "Alice", "dereth-radar"));

        opacities.SetAll("server", "Alice", 70);
        opacities.Set("server", "Alice", "dereth-radar", 42);

        Assert.Equal(40, opacities.Of("server", "Alice", "dereth-radar"));
        // A plugin window with no value of its own follows All windows.
        Assert.Equal(70, opacities.Of("server", "Alice", "Vault/vault"));
        Assert.Equal(100, opacities.Of("server", "Bob", "dereth-radar"));
        // Never so faint the window is lost; and after a restart the same store still has Alice's values.
        opacities.Set("server", "Alice", "Vault/vault", 0);
        var restarted = new WindowOpacities(store);
        Assert.Equal(WindowOpacities.Faintest, restarted.Of("server", "Alice", "Vault/vault"));
        Assert.Equal(40, restarted.Of("server", "Alice", "dereth-radar"));
    }

    [Fact]
    public void Opacity_fades_a_window_background_and_leaves_its_frame_and_title_solid() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new DerethWindow("Opacity", null, new Border()), 200, 150);
        byte[] Drawn(double fill)
        {
            DerethFrame.SetFillOpacity(panel.Content, fill);
            panel.Tick();
            return panel.Frame.Pixels.ToArray();
        }
        var whole = Drawn(1);
        var faded = Drawn(0.3);
        var stride = panel.Frame.Stride;
        byte Alpha(byte[] pixels, int x, int y) => pixels[y * stride + x * 4 + 3];

        // The fill in the middle of the window fades to the setting.
        Assert.Equal(255, Alpha(whole, 100, 100));
        Assert.InRange(Alpha(faded, 100, 100), 70, 84);
        // The frame's edge and the title draw as they did.
        for (var y = 20; y < 140; y++) Assert.Equal(Alpha(whole, 3, y), Alpha(faded, 3, y));
        Assert.Contains(Enumerable.Range(8, 60), x => Alpha(whole, x, 22) == 255 && Alpha(faded, x, 22) == 255);
    });
}
