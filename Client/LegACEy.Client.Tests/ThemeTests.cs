using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LegACEy.Client.GameArt;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void Switching_theme_restyles_an_open_panel_without_recreating_its_content() => RenderThread.Run(() =>
    {
        Button? button = null;
        using var panel = AvaloniaPanel.Create(() => button = new Button { Content = "Theme" }, 120, 50);
        var originalButton = button;
        var plainTheme = new SimpleClientTheme();
        var acTheme = new AcClientTheme(new SolidColorArtSource());

        panel.ApplyTheme(plainTheme);
        panel.Tick();
        var plainPixels = panel.Frame.Pixels.ToArray();
        panel.ApplyTheme(acTheme);
        Assert.Same(originalButton, panel.Content);
        Assert.True(panel.Tick());

        Assert.NotEqual(plainPixels, panel.Frame.Pixels);
    });

    [Fact]
    public void Ac_theme_art_is_pixel_snapped_and_missing_art_uses_fallback() => RenderThread.Run(() =>
    {
        var art = new SolidColorArtSource();
        var bitmap = GameArtImageExtension.CreateBitmap(art, SolidColorArtSource.TestArtId);
        Assert.NotNull(bitmap);
        Assert.Equal(2, bitmap!.PixelSize.Width);
        Assert.Equal(2, bitmap.PixelSize.Height);
        Assert.Null(GameArtImageExtension.CreateBitmap(art, 0x0600ffff));

        using var fallbackPanel = AvaloniaPanel.Create(() => new Image
        {
            Source = (Bitmap)new GameArtImageExtension(0x0600ffff).ProvideValue(null!),
            Stretch = Stretch.Fill
        }, 4, 4);
        Assert.Equal(new byte[] { 0x30, 0x30, 0x38, 0xff }, fallbackPanel.Frame.Pixels.Take(4));

        using var panel = AvaloniaPanel.Create(() => new Border
        {
            Width = 20,
            Height = 20,
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x25, 0x35))
        }, 20, 20);
        panel.ApplyTheme(new AcClientTheme(art));
        Assert.False(panel.Tick());
    });

    [Fact]
    public void Nine_slice_draws_corners_at_native_size_and_tiles_without_blended_pixels() => RenderThread.Run(() =>
    {
        var art = new PatternArtSource();
        using var panel = AvaloniaPanel.Create(() => new NineSliceBorder(art, 2), 13, 13);

        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(panel.Frame, 0, 0));
        var pattern = new HashSet<string>(new[]
        {
            Convert.ToHexString(new byte[] { 0, 0, 255, 255 }),
            Convert.ToHexString(new byte[] { 0, 255, 0, 255 }),
            Convert.ToHexString(new byte[] { 255, 0, 0, 255 }),
            Convert.ToHexString(new byte[] { 255, 255, 255, 255 })
        });
        for (var y = 2; y < 11; y++)
        for (var x = 2; x < 11; x++)
            Assert.Contains(Convert.ToHexString(Pixel(panel.Frame, x, y)), pattern);
    });

    [Fact]
    public void Theme_gallery_contains_the_control_catalogue_and_requests_a_live_switch() => RenderThread.Run(() =>
    {
        ThemeGalleryControl? gallery = null;
        using var panel = AvaloniaPanel.Create(() => gallery = new ThemeGalleryControl(new PatternArtSource()), 580, 560);
        panel.ApplyTheme(new AcClientTheme(new PatternArtSource()));
        panel.Tick();
        var controls = panel.Content.GetVisualDescendants().ToArray();
        Assert.Contains(controls, item => item is NineSliceBorder);
        Assert.Contains(controls, item => item is TextBox);
        Assert.Contains(controls, item => item is ListBox);
        Assert.Contains(controls, item => item is TabControl);
        Assert.Contains(controls, item => item is ScrollBar);
        Assert.Contains(controls, item => item is CheckBox);
        Assert.Contains(controls, item => item is ProgressBar);

        var switched = false;
        gallery!.ThemeSwitchRequested += (_, _) => switched = true;
        var switchButton = controls.OfType<Button>().Single(button => Equals(button.Content, "Switch theme while this gallery stays open"));
        switchButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(switched);
    });

    private static byte[] Pixel(PanelFrame frame, int x, int y) =>
        frame.Pixels.Skip((y * frame.Stride) + (x * 4)).Take(4).ToArray();

    private sealed class SolidColorArtSource : IGameArtSource
    {
        public const uint TestArtId = 0x06000001;
        public GameImage? ReadImage(uint id) => id == TestArtId
            ? new GameImage(2, 2, new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 })
            : null;
    }

    private sealed class PatternArtSource : IGameArtSource
    {
        private static readonly byte[] Pixels =
        {
            0, 0, 255, 255, 0, 255, 0, 255,
            255, 0, 0, 255, 255, 255, 255, 255
        };

        public GameImage? ReadImage(uint id) => NineSliceBorder.DefaultPieceIds.Contains(id)
            ? new GameImage(2, 2, Pixels.ToArray())
            : null;
    }
}
