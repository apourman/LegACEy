using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

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

    private sealed class MissingArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}
