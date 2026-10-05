using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

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
        Assert.Null(panel.LastError);
        Assert.Contains(content.GetVisualDescendants().OfType<Image>(), image => image.Width == 20 && image.Height == 20);
    });

    [Fact]
    public void Performance_row_template_accepts_empty_content_during_container_recycling() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new PerformanceListPanel(new NoArt()), 360, 320);
        var content = (PerformanceListPanel)panel.Content;
        Assert.Null(panel.LastError);
        Assert.Null(content.ItemsList.ItemTemplate!.Build(null));
    });

    [Fact]
    public void Performance_panel_opens_and_scrolls_with_the_game_theme_without_disabling_ui() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new PerformanceListPanel(new NoArt()), 360, 320);
        panel.ApplyTheme(new AcClientTheme(new NoArt()));
        panel.Tick();
        Assert.Null(panel.LastError);
        panel.MouseWheel(40, 80, 0, -120);
        panel.Tick();
        Assert.Null(panel.LastError);
        var viewer = panel.Content.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(viewer.Offset.Y > 0);
    });

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}
