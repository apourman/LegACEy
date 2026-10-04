using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;

namespace LegACEy.Client.Tests;

public sealed class PerformanceListPanelTests
{
    [Fact]
    public void Performance_panel_contains_500_fake_pack_rows_and_an_icon_column() => RenderThread.Run(() =>
    {
        PerformanceListPanel? content = null;
        using var panel = AvaloniaPanel.Create(() => content = new PerformanceListPanel(new NoArt()), 360, 320);

        var items = Assert.IsAssignableFrom<System.Collections.IEnumerable>(content!.ItemsList.ItemsSource);
        Assert.Equal(500, items.Cast<object>().Count());
        Assert.Contains(content.GetVisualDescendants().OfType<Image>(), image => image.Width == 20 && image.Height == 20);
    });

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}
