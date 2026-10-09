using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Vault;

namespace LegACEy.Client.Tests;

public sealed class VaultShellTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dereth_window_renders_without_errors_and_nothing_lies_outside_its_frame(bool withArt) => RenderThread.Run(() =>
    {
        IGameArtSource art = withArt ? new CellArt() : new MissingArt();
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(art)),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        host.ApplyTheme(new DerethClientTheme());
        host.Tick();
        Assert.Null(host.LastError);

        var window = new Rect(host.Content.Bounds.Size);
        foreach (var visual in host.Content.GetVisualDescendants().OfType<Visual>())
        {
            // Scrolled-out cells sit beyond the grid's viewport on purpose; the viewport clips them.
            if (!visual.IsEffectivelyVisible || visual.GetVisualAncestors().OfType<ScrollViewer>().Any()) continue;
            var origin = visual.TranslatePoint(default, host.Content);
            if (origin == null) continue;
            var bounds = new Rect(origin.Value, visual.Bounds.Size);
            Assert.True(window.Contains(bounds), $"{visual.GetType().Name} at {bounds} lies outside the window {window}");
        }

        // The Dereth scrollbar is nine pixels across; the stock bar is not.
        var scrollBar = host.Content.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.Equal(9, scrollBar.Width);
        Assert.NotNull(scrollBar.GetVisualDescendants().OfType<Thumb>().SingleOrDefault());
        // The header's chest icon is the game's own art, requested from the DAT.
        if (art is CellArt cellArt) Assert.Contains(VaultShellPanel.ChestIconId, cellArt.Reads.Keys);
        Assert.False(host.Tick());
    });

    [Fact]
    public void Close_is_the_only_action_and_remains_clickable_without_a_dat() => RenderThread.Run(() =>
    {
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(new MissingArt())),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        var chrome = (VaultShellWindow)host.Content;
        var closes = 0;
        chrome.CloseRequested += (_, _) => closes++;
        // The scrollbars' own repeat buttons are internal parts, not actions.
        var close = Assert.Single(chrome.GetVisualDescendants().OfType<Button>()
            .Where(button => !button.GetVisualAncestors().OfType<ScrollBar>().Any()));
        var point = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), chrome)!.Value;
        host.PointerDown(point.X, point.Y);
        host.PointerUp(point.X, point.Y);
        Assert.Equal(1, closes);
        Assert.False(host.WantsKeyboard);
        Assert.Null(host.LastError);
    });

    [Fact]
    public void Clicking_the_scrollbar_groove_below_the_thumb_pages_the_grid_down() => RenderThread.Run(() =>
    {
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(new MissingArt())),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        host.ApplyTheme(new DerethClientTheme());
        host.Tick();
        var viewer = host.Content.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var bar = host.Content.GetVisualDescendants().OfType<ScrollBar>().Single(control => control.Orientation == Avalonia.Layout.Orientation.Vertical);
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
        var groove = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height + 4), host.Content)!.Value;
        Assert.Equal(0, viewer.Offset.Y);

        host.PointerDown(groove.X, groove.Y);
        host.PointerUp(groove.X, groove.Y);
        host.Tick();

        Assert.True(viewer.Offset.Y > 0, "A click on the groove below the thumb should page the grid down.");
        Assert.Null(host.LastError);
    });

    [Fact]
    public void Item_icons_are_drawn_at_native_size_from_the_dat() => RenderThread.Run(() =>
    {
        var art = new CellArt();
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(art)),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        host.ApplyTheme(new DerethClientTheme());
        host.Tick();
        var cells = Assert.Single(host.Content.GetVisualDescendants().OfType<WrapPanel>()).Children;
        var layers = cells[0].GetVisualDescendants().OfType<Image>().ToArray();
        Assert.NotEmpty(layers);
        foreach (var image in layers)
        {
            Assert.Equal(32, image.Bounds.Width);
            Assert.Equal(32, image.Bounds.Height);
        }
        Assert.Equal(1, art.Reads[0x06000FC7u]);
        Assert.Null(host.LastError);
    });

    private sealed class CellArt : IGameArtSource
    {
        public Dictionary<uint, int> Reads { get; } = new();

        public GameImage? ReadImage(uint id)
        {
            Reads[id] = Reads.TryGetValue(id, out var count) ? count + 1 : 1;
            if (id != 0x06000FC7 && id != VaultShellPanel.ChestIconId)
                return null;
            var pixels = new byte[32 * 32 * 4];
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var offset = (y * 32 + x) * 4;
                pixels[offset] = 0xff;
                pixels[offset + 1] = 0x20;
                pixels[offset + 2] = 0x10;
                pixels[offset + 3] = 0xff;
            }
            return new GameImage(32, 32, pixels);
        }
    }

    private sealed class MissingArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}
