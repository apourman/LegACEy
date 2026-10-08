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
    public void Fixed_size_shell_fits_and_renders_without_art_in_both_host_themes(bool retail) => RenderThread.Run(() =>
    {
        var art = new MissingArt();
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(art)),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        host.ApplyTheme(retail ? new AcClientTheme(art) : new SimpleClientTheme());
        host.Tick();
        Assert.Null(host.LastError);
        foreach (var label in host.Content.GetVisualDescendants().OfType<TextBlock>())
        {
            var origin = label.TranslatePoint(default, host.Content)!.Value;
            Assert.InRange(origin.X, 8, VaultShellPanel.WindowWidth - label.Bounds.Width - 8);
            Assert.InRange(origin.Y, 8, VaultShellPanel.WindowHeight - label.Bounds.Height - 8);
        }
        var labels = host.Content.GetVisualDescendants().OfType<TextBlock>().ToArray();
        var appraisal = labels.Single(label => label.Text == "Appraise item");
        var deposit = labels.Single(label => label.Text == "Deposit item");
        var appraisalBottom = appraisal.TranslatePoint(new Point(0, appraisal.Bounds.Height), host.Content)!.Value.Y;
        var depositTop = deposit.TranslatePoint(default, host.Content)!.Value.Y;
        Assert.True(appraisalBottom + 12 < depositTop, "Details must stay clear of the footer.");
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
        var close = Assert.Single(chrome.GetVisualDescendants().OfType<Button>());
        var point = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), chrome)!.Value;
        host.PointerDown(point.X, point.Y);
        host.PointerUp(point.X, point.Y);
        Assert.Equal(1, closes);
        Assert.False(host.WantsKeyboard);
        Assert.Null(host.LastError);
    });

    [Fact]
    public void Retail_cells_render_at_native_size_with_selection_above_the_item_and_shared_art() => RenderThread.Run(() =>
    {
        var art = new CellArt();
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(art)),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;
        var slots = Assert.Single(host.Content.GetVisualDescendants().OfType<UniformGrid>());
        var selectedLayers = slots.Children[0].GetVisualDescendants().OfType<Image>().ToArray();
        var emptyImage = Assert.Single(slots.Children[12].GetVisualDescendants().OfType<Image>());
        foreach (var image in selectedLayers.Append(emptyImage))
        {
            Assert.Equal(32, image.Bounds.Width);
            Assert.Equal(32, image.Bounds.Height);
        }
        Assert.Same(selectedLayers[0].Source, emptyImage.Source);
        Assert.Equal(new byte[] { 0x60, 0x40, 0x28, 0xff }, PixelAt(emptyImage, 16, 16));
        Assert.Equal(new byte[] { 0, 0xff, 0xff, 0xff }, PixelAt(selectedLayers[0], 1, 1));
        Assert.Equal(new byte[] { 0xff, 0x20, 0x10, 0xff }, PixelAt(selectedLayers[0], 16, 16));
        Assert.Equal(1, art.Reads[VaultShellPanel.InventoryCellArtId]);
        Assert.Equal(1, art.Reads[VaultShellPanel.InventorySelectionArtId]);
        Assert.Null(host.LastError);
        Assert.False(host.Tick());

        byte[] PixelAt(Control control, int x, int y)
        {
            var point = control.TranslatePoint(new Point(x, y), host.Content)!.Value;
            var offset = (int)point.Y * host.Frame.Stride + (int)point.X * 4;
            return host.Frame.Pixels.Skip(offset).Take(4).ToArray();
        }
    });

    private sealed class CellArt : IGameArtSource
    {
        public Dictionary<uint, int> Reads { get; } = new();

        public GameImage? ReadImage(uint id)
        {
            Reads[id] = Reads.TryGetValue(id, out var count) ? count + 1 : 1;
            if (id != VaultShellPanel.InventoryCellArtId && id != VaultShellPanel.InventorySelectionArtId && id != 0x06000FC7)
                return null;
            var pixels = new byte[32 * 32 * 4];
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var offset = (y * 32 + x) * 4;
                if (id == VaultShellPanel.InventorySelectionArtId)
                {
                    if (x >= 2 && x < 30 && y >= 2 && y < 30) continue;
                    pixels[offset + 1] = pixels[offset + 2] = 0xff;
                }
                else
                {
                    pixels[offset] = id == VaultShellPanel.InventoryCellArtId ? (byte)0x60 : (byte)0xff;
                    pixels[offset + 1] = id == VaultShellPanel.InventoryCellArtId ? (byte)0x40 : (byte)0x20;
                    pixels[offset + 2] = id == VaultShellPanel.InventoryCellArtId ? (byte)0x28 : (byte)0x10;
                }
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
