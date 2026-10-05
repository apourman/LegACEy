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
        ThemeWindowChrome? chrome = null;
        using var panel = AvaloniaPanel.Create(() =>
        {
            var button = new Button { Content = "Theme" };
            return chrome = new ThemeWindowChrome(new SolidColorArtSource(), "Test", button);
        }, 120, 80);
        var originalChrome = chrome;
        var plainTheme = new SimpleClientTheme();
        var acTheme = new AcClientTheme(new SolidColorArtSource());

        panel.ApplyTheme(plainTheme);
        var windowChrome = chrome!;
        var artFrame = windowChrome.GetVisualDescendants().OfType<NineSliceBorder>().Single();
        var plainFrame = windowChrome.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("theme-window-frame"));
        Assert.False(artFrame.IsVisible);
        Assert.True(plainFrame.IsVisible);
        panel.Tick();
        var plainPixels = panel.Frame.Pixels.ToArray();
        panel.ApplyTheme(acTheme);
        Assert.Same(originalChrome, panel.Content);
        Assert.True(artFrame.IsVisible);
        Assert.False(plainFrame.IsVisible);
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
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(panel.Frame, 3, 2));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(panel.Frame, 4, 2));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(panel.Frame, 2, 3));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(panel.Frame, 2, 4));
    });

    [Fact]
    public void Theme_gallery_contains_the_control_catalogue_and_requests_a_live_switch() => RenderThread.Run(() =>
    {
        ThemeWindowChrome? chrome = null;
        using var panel = AvaloniaPanel.Create(() =>
        {
            var gallery = new ThemeGalleryControl();
            return chrome = new ThemeWindowChrome(new PatternArtSource(), "Gallery", gallery);
        }, 580, 560);
        panel.ApplyTheme(new AcClientTheme(new PatternArtSource()));
        panel.Tick();
        var content = panel.Content!;
        var controls = content.GetVisualDescendants().ToArray();
        Assert.Same(chrome, content);
        Assert.Contains(controls, item => item is NineSliceBorder);
        Assert.Contains(controls, item => item is TextBox);
        Assert.Contains(controls, item => item is ListBox);
        Assert.Contains(controls, item => item is TabControl);
        Assert.Contains(controls, item => item is ScrollBar);
        Assert.Contains(controls, item => item is CheckBox);
        Assert.Contains(controls, item => item is ProgressBar);
        Assert.Contains(controls.OfType<Button>(), button => button.Classes.Contains("sample-hover"));
        Assert.Contains(controls.OfType<Button>(), button => button.Classes.Contains("sample-pressed"));
        Assert.Contains(controls.OfType<ListBoxItem>(), item => item.Classes.Contains("sample-hover"));
        Assert.Contains(controls.OfType<ListBoxItem>(), item => item.Classes.Contains("sample-selected"));
        Assert.Contains(controls.OfType<TabItem>(), item => item.Classes.Contains("sample-hover"));
        Assert.Contains(controls.OfType<TabItem>(), item => item.Classes.Contains("sample-selected"));
        Assert.Contains(controls.OfType<ScrollBar>(), item => item.Classes.Contains("sample-hover"));
        Assert.Contains(controls.OfType<ScrollBar>(), item => item.Classes.Contains("sample-pressed"));
        Assert.Contains(controls.OfType<ScrollBar>(), item => !item.IsEnabled);
        Assert.Contains(controls.OfType<ScrollViewer>(), item => !item.IsEnabled);
        Assert.NotNull(chrome!.CloseButton);

        var sampleTypes = new[]
        {
            typeof(Button), typeof(TextBox), typeof(ListBox), typeof(TabControl),
            typeof(ScrollViewer), typeof(ScrollBar), typeof(CheckBox), typeof(ProgressBar), typeof(ToolTip), typeof(TextBlock)
        };
        foreach (var sampleType in sampleTypes)
        foreach (var state in new[] { "sample-normal", "sample-hover", "sample-pressed", "sample-disabled" })
            Assert.True(controls.Any(control => sampleType.IsInstanceOfType(control) && control.Classes.Contains(state)), $"Missing {state} sample for {sampleType.Name}.");
        foreach (var state in new[] { "sample-normal", "sample-hover", "sample-pressed", "sample-disabled" })
        {
            Assert.Contains(controls.OfType<ListBox>(), list => list.Items.OfType<ListBoxItem>().Any(item => item.Classes.Contains(state)));
            Assert.Contains(controls.OfType<TabControl>(), tabs => tabs.Items.OfType<TabItem>().Any(item => item.Classes.Contains(state)));
        }

        var switched = false;
        var gallery = controls.OfType<ThemeGalleryControl>().Single();
        gallery.ThemeSwitchRequested += (_, _) => switched = true;
        var switchButton = controls.OfType<Button>().Single(button => Equals(button.Content, "Switch theme while this gallery stays open"));
        switchButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(switched);

        var closeRequested = false;
        chrome.CloseRequested += (_, _) => closeRequested = true;
        chrome.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(closeRequested);
    });

    [Fact]
    public void Closing_a_preview_chrome_removes_that_surface_from_its_workspace() => RenderThread.Run(() =>
    {
        var workspace = new StackPanel();
        var chrome = new ThemeWindowChrome(new SolidColorArtSource(), "Preview", new TextBlock { Text = "Surface" });
        workspace.Children.Add(chrome);

        chrome.CloseAndRemoveFrom(workspace);
        chrome.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.DoesNotContain(chrome, workspace.Children);
    });

    [Fact]
    public void Nine_slice_edges_repeat_native_pixels_and_crop_the_final_partial_tile() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new NineSliceBorder(new PatternArtSource(), 2), 13, 13);
        for (var offset = 0; offset < 9; offset++)
        {
            Assert.Equal(offset % 2 == 0 ? new byte[] { 0, 0, 255, 255 } : new byte[] { 255, 0, 0, 255 }, Pixel(panel.Frame, 0, offset + 2));
            Assert.Equal(offset % 2 == 0 ? new byte[] { 0, 0, 255, 255 } : new byte[] { 0, 255, 0, 255 }, Pixel(panel.Frame, offset + 2, 0));
        }
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(panel.Frame, 10, 10));
    });

    [Theory]
    [InlineData("Button")]
    [InlineData("TextBox")]
    [InlineData("ListBox")]
    [InlineData("ListBoxItem")]
    [InlineData("TabControl")]
    [InlineData("TabItem")]
    [InlineData("ScrollViewer")]
    [InlineData("ScrollBar")]
    [InlineData("CheckBox")]
    [InlineData("ProgressBar")]
    [InlineData("ToolTip")]
    public void Ac_art_is_visible_on_standard_controls_without_window_chrome(string kind) => RenderThread.Run(() =>
    {
        Control Create() => kind switch
        {
            "Button" => new Button(), "TextBox" => new TextBox(), "ListBox" => new ListBox(),
            "ListBoxItem" => new ListBoxItem(), "TabControl" => new TabControl(), "TabItem" => new TabItem(),
            "ScrollViewer" => new ScrollViewer(), "ScrollBar" => new ScrollBar { Orientation = Avalonia.Layout.Orientation.Horizontal, Maximum = 100, Value = 30 },
            "CheckBox" => new CheckBox(), "ProgressBar" => new ProgressBar { Value = 50 }, "ToolTip" => new ToolTip(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        using var panel = AvaloniaPanel.Create(Create, 240, 80);
        panel.ApplyTheme(new AcClientTheme(new ControlArtSource()));
        panel.Tick();
        Assert.True(ContainsPixel(panel.Frame, ControlArtSource.Normal) || ContainsPixel(panel.Frame, ControlArtSource.Panel), $"No game-art pixel rendered for {kind}.");
    });

    [Fact]
    public void Ac_button_uses_art_for_live_states_and_remains_clickable_across_theme_switches() => RenderThread.Run(() =>
    {
        var button = new Button();
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        using var panel = AvaloniaPanel.Create(() => button, 100, 30);
        var theme = new AcClientTheme(new ControlArtSource());
        panel.ApplyTheme(theme);
        panel.Tick();
        Assert.Equal(ControlArtSource.Normal, Pixel(panel.Frame, 10, 10));
        panel.PointerMove(10, 10);
        panel.Tick();
        Assert.Equal(ControlArtSource.Hover, Pixel(panel.Frame, 10, 10));
        panel.PointerDown(10, 10);
        panel.Tick();
        Assert.Equal(ControlArtSource.Pressed, Pixel(panel.Frame, 10, 10));
        panel.PointerUp(10, 10);
        Assert.Equal(1, clicks);
        panel.ApplyTheme(new SimpleClientTheme());
        panel.Tick();
        Assert.NotEqual(ControlArtSource.Hover, Pixel(panel.Frame, 10, 10));
        panel.ApplyTheme(theme);
        panel.Tick();
        Assert.Same(button, panel.Content);
        Assert.Equal(ControlArtSource.Hover, Pixel(panel.Frame, 10, 10));
        button.IsEnabled = false;
        panel.Tick();
        Assert.NotEqual(ControlArtSource.Hover, Pixel(panel.Frame, 10, 10));
        panel.PointerDown(10, 10);
        panel.PointerUp(10, 10);
        Assert.Equal(1, clicks);
    });

    [Fact]
    public void Ac_theme_preserves_text_entry_checkboxes_tabs_and_scrolling() => RenderThread.Run(() =>
    {
        var field = new TextBox { Width = 280, Height = 32 };
        var check = new CheckBox { Content = "Check", Height = 26 };
        var tabs = new TabControl { Height = 70, Items = { new TabItem { Header = "One", Content = "First" }, new TabItem { Header = "Two", Content = "Second" } } };
        var list = new ListBox { Height = 70, ItemsSource = Enumerable.Range(0, 40).Select(i => $"Row {i}") };
        var bar = new ScrollBar { Width = 280, Height = 20, Orientation = Avalonia.Layout.Orientation.Horizontal, Maximum = 100, ViewportSize = 10, Value = 30 };
        using var panel = AvaloniaPanel.Create(() => new StackPanel { Spacing = 4, Children = { field, check, tabs, list, bar } }, 320, 300);
        panel.ApplyTheme(new AcClientTheme(new ControlArtSource()));
        panel.Tick();
        void Click(Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;
            panel.PointerDown(point.X, point.Y);
            panel.PointerUp(point.X, point.Y);
            panel.Tick();
        }
        Click(field);
        panel.TextInput("hello");
        Assert.Equal("hello", field.Text);
        Assert.True(panel.WantsKeyboard);
        Click(check.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "border"));
        Assert.True(check.IsChecked);
        Click(tabs.GetVisualDescendants().OfType<TabItem>().Last());
        Assert.Equal(1, tabs.SelectedIndex);
        var listPoint = list.TranslatePoint(new Point(30, 25), panel.Content)!.Value;
        panel.MouseWheel(listPoint.X, listPoint.Y, 0, -120);
        Assert.True(list.GetVisualDescendants().OfType<ScrollViewer>().First().Offset.Y > 0);
        var increment = bar.GetVisualDescendants().OfType<RepeatButton>().Single(button => button.Name == "PART_LineDownButton");
        Click(increment);
        Assert.True(bar.Value > 30);
        panel.ClearFocus();
        Assert.False(panel.WantsKeyboard);
    });

    [Fact]
    public void Native_close_face_renders_and_closes_without_a_stock_glyph_overlay() => RenderThread.Run(() =>
    {
        var close = new Button { Content = "×", Classes = { "theme-window-close" } };
        var clicked = false;
        close.Click += (_, _) => clicked = true;
        using var panel = AvaloniaPanel.Create(() => close, 24, 23);
        panel.ApplyTheme(new AcClientTheme(new ControlArtSource()));
        panel.Tick();
        Assert.Equal(ControlArtSource.Hover, Pixel(panel.Frame, 10, 10));
        panel.PointerDown(10, 10);
        panel.Tick();
        Assert.Equal(ControlArtSource.Pressed, Pixel(panel.Frame, 10, 10));
        panel.PointerUp(10, 10);
        Assert.True(clicked);
    });

    [Fact]
    public void Retail_corner_mapping_preserves_native_corners_without_thickening_the_edges() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new NineSliceBorder(new AsymmetricArtSource()), 21, 21);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(panel.Frame, 3, 3));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(panel.Frame, 16, 3));
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, Pixel(panel.Frame, 3, 16));
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, Pixel(panel.Frame, 16, 16));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(panel.Frame, 4, 0));
        Assert.Equal(new byte[] { 255, 255, 0, 255 }, Pixel(panel.Frame, 4, 20));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(panel.Frame, 0, 4));
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, Pixel(panel.Frame, 20, 4));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(panel.Frame, 4, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(panel.Frame, 1, 4));
    });

    private sealed class AsymmetricArtSource : IGameArtSource
    {
        public GameImage ReadImage(uint id)
        {
            var (width, height, color) = id switch
            {
                0x060074C3 => (4, 4, new byte[] { 0, 0, 255, 255 }),
                0x060074C4 => (5, 4, new byte[] { 255, 0, 0, 255 }),
                0x060074C5 => (4, 5, new byte[] { 0, 255, 255, 255 }),
                0x060074C6 => (5, 5, new byte[] { 255, 0, 255, 255 }),
                0x060074BF => (2, 1, new byte[] { 0, 255, 0, 255 }),
                0x060074C1 => (2, 1, new byte[] { 255, 255, 0, 255 }),
                0x060074C0 => (1, 2, new byte[] { 255, 255, 255, 255 }),
                0x060074C2 => (1, 2, new byte[] { 128, 128, 128, 255 }),
                _ => (2, 2, new byte[] { 0, 0, 0, 255 })
            };
            return new GameImage(width, height, Enumerable.Range(0, width * height).SelectMany(_ => color).ToArray());
        }
    }

    [Fact]
    public void Scrollbar_gallery_samples_show_the_same_thumb_art_as_live_states() => RenderThread.Run(() =>
    {
        var bars = new[] { "sample-normal", "sample-hover", "sample-pressed", "sample-disabled" }
            .Select(state => new ScrollBar { Width = 200, Height = 20, Maximum = 100, ViewportSize = 10, Value = 30,
                Orientation = Avalonia.Layout.Orientation.Horizontal, Classes = { state }, IsEnabled = state != "sample-disabled" }).ToArray();
        using var panel = AvaloniaPanel.Create(() => new StackPanel { Spacing = 4, Children = { bars[0], bars[1], bars[2], bars[3] } }, 220, 100);
        panel.ApplyTheme(new AcClientTheme(new ControlArtSource()));
        panel.Tick();
        byte[] ThumbPixel(ScrollBar bar)
        {
            var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            var point = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), panel.Content)!.Value;
            return Pixel(panel.Frame, (int)point.X, (int)point.Y);
        }
        Assert.Equal(ControlArtSource.Normal, ThumbPixel(bars[0]));
        Assert.Equal(ControlArtSource.Hover, ThumbPixel(bars[1]));
        Assert.Equal(ControlArtSource.Pressed, ThumbPixel(bars[2]));
        Assert.NotEqual(ControlArtSource.Normal, ThumbPixel(bars[3]));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_open_native_tooltip_uses_art_and_switches_with_its_owning_panel(bool translucentPanel) => RenderThread.Run(() =>
    {
        var expected = translucentPanel ? new byte[] { 0, 0, 0, 255 } : ControlArtSource.Panel;
        TextBlock owner = null!;
        ToolTip tip = null!;
        // Construct tooltip services only after the host initializes its UI dispatcher.
        using var panel = AvaloniaPanel.Create(() =>
        {
            owner = new TextBlock { Text = "Hover" };
            tip = new ToolTip { Content = "Tip", Width = 60, Height = 30 };
            ToolTip.SetTip(owner, tip);
            return owner;
        }, 120, 80);
        panel.ApplyTheme(new AcClientTheme(new ControlArtSource(translucentPanel)));
        ToolTip.SetIsOpen(owner, true);
        panel.Tick();
        byte[] TipPixel(bool expectText = false)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(60, 30));
            bitmap.Render(tip);
            var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(60 * 30 * 4);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, 60, 30), memory, 60 * 30 * 4, 60 * 4);
                var pixels = new byte[60 * 30 * 4];
                System.Runtime.InteropServices.Marshal.Copy(memory, pixels, 0, pixels.Length);
                if (expectText)
                    Assert.True(Enumerable.Range(3, 16).SelectMany(y => Enumerable.Range(5, 26).Select(x => ((y * 60) + x) * 4))
                        .Any(i => pixels[i + 2] > expected[2] + 10 && pixels[i + 1] > expected[1] + 10), "Tooltip text must remain visible over its artwork.");
                return pixels.Skip(((25 * 60) + 55) * 4).Take(4).ToArray();
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
        }
        try
        {
            Assert.Equal(expected, TipPixel(expectText: true));
            panel.ApplyTheme(new SimpleClientTheme());
            panel.Tick();
            Assert.True(ToolTip.GetIsOpen(owner));
            Assert.NotEqual(expected, TipPixel());
            panel.ApplyTheme(new AcClientTheme(new ControlArtSource(translucentPanel)));
            panel.Tick();
            Assert.Equal(expected, TipPixel(expectText: true));
        }
        finally { ToolTip.SetIsOpen(owner, false); }
    });

    private static bool ContainsPixel(PanelFrame frame, byte[] expected) =>
        Enumerable.Range(0, frame.Width * frame.Height).Any(i => frame.Pixels.AsSpan(i * 4, 4).SequenceEqual(expected));

    private sealed class ControlArtSource : IGameArtSource
    {
        private readonly bool _translucentPanel;
        public ControlArtSource(bool translucentPanel = false) => _translucentPanel = translucentPanel;
        public static readonly byte[] Normal = { 0x12, 0x34, 0x56, 0xff };
        public static readonly byte[] Hover = { 0x21, 0x43, 0x65, 0xff };
        public static readonly byte[] Pressed = { 0x31, 0x54, 0x76, 0xff };
        public static readonly byte[] Panel = { 0x41, 0x65, 0x87, 0xff };
        public GameImage ReadImage(uint id)
        {
            var color = id == AcClientTheme.WindowChromeCenterId ? Panel :
                id is AcClientTheme.ButtonHoverId or AcClientTheme.CloseNormalId or AcClientTheme.CheckOnId or 0x06004C64 or 0x06004C84 ? Hover : id is AcClientTheme.ButtonPressedId or AcClientTheme.ClosePressedId or 0x06004C65 or 0x06004C85 ? Pressed : Normal;
            if (id == AcClientTheme.WindowChromeCenterId && _translucentPanel) color = new byte[] { 0, 0, 0, 128 };
            return new GameImage(6, 6, Enumerable.Range(0, 36).SelectMany(_ => color).ToArray());
        }
    }

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
