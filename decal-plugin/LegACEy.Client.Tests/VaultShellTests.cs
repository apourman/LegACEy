using System;
using System.Collections.Generic;
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
    [InlineData(false, 344, 606)]
    [InlineData(true, 344, 606)]
    [InlineData(false, 294, 306)]
    [InlineData(true, 294, 306)]
    public void Dereth_window_renders_without_errors_and_nothing_lies_outside_its_frame(bool withArt, int width, int height) => RenderThread.Run(() =>
    {
        IGameArtSource art = withArt ? new CellArt() : new MissingArt();
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(art)),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        host.ApplyTheme(new DerethClientTheme());
        if (width != VaultShellPanel.WindowWidth || height != VaultShellPanel.WindowHeight) host.Resize(width, height);
        host.Tick();
        Assert.Null(host.LastError);
        AssertNothingOutsideFrame(host);

        // The Dereth scrollbar is nine pixels across; the stock bar is not.
        var scrollBar = VerticalBar(host);
        Assert.Equal(9, scrollBar.Width);
        Assert.NotNull(scrollBar.GetVisualDescendants().OfType<Thumb>().SingleOrDefault());
        // The header's chest icon is the game's own art, requested from the DAT.
        if (art is CellArt cellArt) Assert.Contains(VaultShellPanel.ChestIconId, cellArt.Reads.Keys);
        Assert.False(host.Tick());
    });

    [Fact]
    public void At_the_minimum_size_with_the_status_line_shown_one_grid_row_fits_and_nothing_spills() => RenderThread.Run(() =>
    {
        using var vault = new LiveVaultHost(width: 294, height: 306);
        // A retail item dragged over the window's header, not a cell, shows its drop hint on the status line.
        vault.Window.RetailDragOver(0x50000099, "Fine Sword", new Point(40, 20));
        vault.Step();

        Assert.Contains(vault.Host.Content.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsVisible && (text.Text ?? string.Empty).StartsWith("Drop Fine Sword", StringComparison.Ordinal));
        var grid = Assert.Single(vault.Host.Content.GetVisualDescendants().OfType<DerethSlotGrid>());
        Assert.True(grid.Bounds.Height >= DerethSlotGrid.CellSize, $"The grid is {grid.Bounds.Height} px tall, less than one {DerethSlotGrid.CellSize} px row.");
        AssertNothingOutsideFrame(vault.Host);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Close_remains_clickable_without_a_dat() => RenderThread.Run(() =>
    {
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(new MissingArt())),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        var chrome = (VaultShellWindow)host.Content;
        var closes = 0;
        chrome.CloseRequested += (_, _) => closes++;
        // The header's close box is the only Dereth button in the header; the paging arrows are buttons too, and the scrollbars' repeat buttons are internal parts.
        var close = Assert.Single(chrome.GetVisualDescendants().OfType<DerethButton>());
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
        // The grid's bar: the search field's text box has bars of its own.
        var viewer = VaultFixture.GridScroller(host.Content);
        var bar = host.Content.GetVisualDescendants().OfType<ScrollBar>()
            .Single(control => control.Orientation == Avalonia.Layout.Orientation.Vertical && control.GetVisualAncestors().OfType<DerethSlotGrid>().Any());
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

    [Fact]
    public void The_grid_reflows_its_columns_when_the_window_is_resized() => RenderThread.Run(() =>
    {
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(new MissingArt())),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        host.ApplyTheme(new DerethClientTheme());
        host.Tick();
        Assert.Equal(6, Columns(host));

        host.Resize(594, VaultShellPanel.WindowHeight);
        host.Tick();
        Assert.Equal(11, Columns(host));

        host.Resize(294, VaultShellPanel.WindowHeight);
        host.Tick();
        Assert.Equal(5, Columns(host));
        Assert.Null(host.LastError);
    });

    [Fact]
    public void The_scrollbar_is_hidden_when_the_page_fits_and_shown_when_it_does_not() => RenderThread.Run(() =>
    {
        // Nine items fit the default window's grid, which keeps at least 24 slots.
        using (var few = new LiveVaultHost())
            Assert.False(VerticalBar(few.Host).IsVisible);

        // The sample Vault has 317 items, far more than one window shows.
        VaultShellPanel? vault = null;
        using var many = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(new MissingArt())),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        many.ApplyTheme(new DerethClientTheme());
        many.Tick();
        Assert.True(VerticalBar(many).IsVisible);
        Assert.Null(many.LastError);
    });

    /// <summary>The live Vault from the FakeVaultServer over the real channel wire, with the Dereth theme.</summary>
    private sealed class LiveVaultHost : IDisposable
    {
        private DateTime _now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        private readonly ServerChannelClient _channel;
        private readonly VaultShellPanel _panel;

        public LiveVaultHost(int width = VaultShellPanel.WindowWidth, int height = VaultShellPanel.WindowHeight)
        {
            Server = new FakeVaultServer(() => _now) { Latency = TimeSpan.FromMilliseconds(30) };
            _channel = new ServerChannelClient(Server, () => _now);
            Server.Deliver = _channel.Receive;
            var client = new VaultClient(_channel);
            VaultShellPanel? panel = null;
            Host = AvaloniaPanel.Create(() => new VaultShellWindow(panel = new VaultShellPanel(new MissingArt(), client)),
                VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
            _panel = panel!;
            Host.ApplyTheme(new DerethClientTheme());
            if (width != VaultShellPanel.WindowWidth || height != VaultShellPanel.WindowHeight) Host.Resize(width, height);
            Step();
            Step();
            Assert.Equal(VaultConnection.Live, client.Connection);
        }

        public FakeVaultServer Server { get; }
        public AvaloniaPanel Host { get; }
        public VaultShellWindow Window => (VaultShellWindow)Host.Content;

        public void Step()
        {
            _now += TimeSpan.FromMilliseconds(30);
            Server.Pump();
            _channel.Tick();
            Host.Tick();
        }

        public void Dispose()
        {
            _panel.Dispose();
            Host.Dispose();
        }
    }

    /// <summary>Every visible control lies inside the window, except scrolled-out cells, which the grid's viewport clips.</summary>
    private static void AssertNothingOutsideFrame(AvaloniaPanel host)
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

    private static int Columns(AvaloniaPanel host)
    {
        var cells = Assert.Single(host.Content.GetVisualDescendants().OfType<WrapPanel>()).Children;
        return cells.Count(cell => cell.Bounds.Y == cells[0].Bounds.Y);
    }

    /// <summary>The grid's bar. The search field's text box has scrollbars of its own.</summary>
    private static ScrollBar VerticalBar(AvaloniaPanel host) =>
        host.Content.GetVisualDescendants().OfType<ScrollBar>()
            .Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical && bar.GetVisualAncestors().OfType<DerethSlotGrid>().Any());

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
