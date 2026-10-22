using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LegACEy.Client.PanelHost;
using Xunit;
using Xunit.Abstractions;
using Rectangle = System.Drawing.Rectangle;

namespace LegACEy.Client.Tests;

public sealed class RetainedFramebufferTests
{
    private const int Width = 300;
    private const int Height = 400;
    private readonly ITestOutputHelper _output;

    public RetainedFramebufferTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void A_small_change_reports_only_its_area_and_its_pixels_match_a_full_render() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => Rows(Brushes.Red), Width, Height);
        var indicator = IndicatorOf(panel);
        var before = panel.Frame.Pixels.ToArray();

        indicator.Background = Brushes.Lime;
        PanelCompositor.Advance();

        Assert.True(panel.Changed);
        var reported = Union(panel.Frame.DirtyRectangles);
        var origin = indicator.TranslatePoint(default, panel.Content)!.Value;
        var change = new Rectangle((int)origin.X, (int)origin.Y, 40, 20);
        Assert.True(reported.Contains(change), $"{reported} does not cover the change {change}");
        Assert.True(reported.Width * reported.Height <= 2 * change.Width * change.Height, $"{reported} is far bigger than the change {change}");
        Assert.Equal((long)reported.Width * reported.Height, panel.PaintedArea);
        AssertChangesInside(before, panel, "recolour");
        using var full = AvaloniaPanel.Create(() => Rows(Brushes.Lime), Width, Height);
        Assert.Equal(full.Frame.Pixels, panel.Frame.Pixels);
    });

    [Fact]
    public void Input_and_a_popup_change_pixels_only_inside_the_reported_area() => RenderThread.Run(() =>
    {
        StackPanel Content(string text) => new()
        {
            Background = Brushes.Black,
            Children =
            {
                new Button { Content = "Click", Width = 80, Height = 30 },
                new TextBox { Width = 200, Text = text },
                new ComboBox { Width = 120, ItemsSource = new[] { "One", "Two", "Three" }, SelectedIndex = 0 },
                new Border { Height = 200 }
            }
        };
        using var panel = AvaloniaPanel.Create(() => Content(""), Width, Height);
        var children = ((StackPanel)panel.Content).Children;
        var (button, textBox, combo) = ((Button)children[0], (TextBox)children[1], (ComboBox)children[2]);
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var before = panel.Frame.Pixels.ToArray();
        void Step(string what)
        {
            PanelCompositor.Advance();
            _output.WriteLine($"{what}: changed {panel.Changed}, reported {Union(panel.Frame.DirtyRectangles)}");
            AssertChangesInside(before, panel, what);
            before = panel.Frame.Pixels.ToArray();
        }
        Point Centre(Visual visual) => visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), panel.Content)!.Value;
        void Click(Point point)
        {
            panel.PointerDown(point.X, point.Y);
            panel.PointerUp(point.X, point.Y);
        }

        panel.PointerMove(Centre(button).X, Centre(button).Y);
        Step("hover");
        Assert.True(panel.Changed);
        Click(Centre(button));
        Step("click");
        Assert.Equal(1, clicks);

        Click(Centre(textBox));
        Step("focus");
        Assert.True(panel.WantsKeyboard);
        panel.TextInput("typed");
        Step("typing");
        Assert.Equal("typed", textBox.Text);
        Assert.True(panel.Changed);
        var typed = Union(panel.Frame.DirtyRectangles);
        Assert.True(typed.Width * typed.Height < Width * Height / 4, $"typing reported {typed}");

        Click(Centre(combo));
        Step("drop-down opens");
        Assert.True(combo.IsDropDownOpen);
        var items = TopLevel.GetTopLevel(panel.Content)!.GetVisualDescendants().OfType<ComboBoxItem>().ToList();
        var lastItem = Centre(items[^1]);
        Assert.True(Union(panel.Frame.DirtyRectangles).Contains((int)lastItem.X, (int)lastItem.Y));
        panel.PointerMove(Centre(items[1]).X, Centre(items[1]).Y);
        Step("hover an item");
        panel.KeyDown(Key.Escape);
        panel.KeyUp(Key.Escape);
        Step("drop-down closes");
        Assert.False(combo.IsDropDownOpen);

        // A move over plain background changes nothing visible, so nothing is reported.
        panel.ClearFocus();
        panel.PointerMove(150, 300);
        Step("leave");
        panel.PointerMove(160, 310);
        Step("plain background");
        Assert.False(panel.Changed);
        Assert.Empty(panel.Frame.DirtyRectangles);

        // Nothing stale is left behind: the kept pixels match a whole render of the same state.
        for (var i = 0; i < 3; i++) Step("settle");
        using var full = AvaloniaPanel.Create(() => Content("typed"), Width, Height);
        Assert.Equal(full.Frame.Pixels, panel.Frame.Pixels);
    });

    [Fact]
    public void Resize_rescale_show_after_hide_and_content_loss_give_a_full_frame_matching_a_full_render() => RenderThread.Run(() =>
    {
        // A control that stays put while the window resizes: Avalonia repaints it only when the whole frame is redrawn.
        Canvas Content(IBrush brush) => new()
        {
            Children = { new Border { Width = 40, Height = 20, Background = brush, [Canvas.LeftProperty] = 10.0, [Canvas.TopProperty] = 10.0 } }
        };
        void AssertFull(AvaloniaPanel panel, IBrush brush, int width, int height, double scale, string what)
        {
            Assert.True(panel.Changed, what);
            Assert.Equal((width, height), (panel.Frame.Width, panel.Frame.Height));
            Assert.Contains(new Rectangle(0, 0, width, height), panel.Frame.DirtyRectangles);
            using var full = AvaloniaPanel.Create(() => Content(brush), width, height, scale);
            Assert.True(full.Frame.Pixels.AsSpan().SequenceEqual(panel.Frame.Pixels), $"{what}: pixels differ from a full render");
        }
        using var panel = AvaloniaPanel.Create(() => Content(Brushes.Lime), Width, Height);
        var square = (Border)((Canvas)panel.Content).Children[0];

        panel.Resize(200, 300);
        PanelCompositor.Advance();
        AssertFull(panel, Brushes.Lime, 200, 300, 1, "resize");

        panel.Resize(200, 300, scale: 0.85);
        PanelCompositor.Advance();
        AssertFull(panel, Brushes.Lime, 200, 300, 0.85, "rescale");

        panel.Visible = false;
        PanelCompositor.Advance();
        square.Background = Brushes.Red;
        PanelCompositor.Advance();
        Assert.False(panel.Changed);
        panel.Visible = true;
        PanelCompositor.Advance();
        AssertFull(panel, Brushes.Red, 200, 300, 0.85, "shown again");

        panel.ContentLost();
        PanelCompositor.Advance();
        AssertFull(panel, Brushes.Red, 200, 300, 0.85, "content lost");
    });

    [Fact]
    public void Rendering_keeps_one_frame_and_creates_no_bitmap_per_render() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => Rows(Brushes.Red), Width, Height);
        var indicator = IndicatorOf(panel);
        var pixels = panel.Frame.Pixels;

        for (var i = 0; i < 20; i++)
        {
            indicator.Background = i % 2 == 0 ? Brushes.Lime : Brushes.Red;
            PanelCompositor.Advance();
            Assert.True(panel.Changed);
        }

        Assert.Same(pixels, panel.Frame.Pixels);
        // The headless window's own surface creates a WriteableBitmap per render, and each adds GC memory pressure; it never runs.
        Assert.Null(LastRenderedFrame.GetValue(TopLevel.GetTopLevel(panel.Content)!.PlatformImpl));
    });

    [Fact]
    public void MeasureAt_gives_the_size_a_panel_lays_out_at_the_same_scale() => RenderThread.Run(() =>
    {
        Border Label() => new()
        {
            Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = "Measure me" }
        };

        var measured = AvaloniaPanel.MeasureAt(Label(), Size.Infinity, 0.85);
        using var panel = AvaloniaPanel.Create(Label, 255, 340, 0.85);

        Assert.Equal(measured, panel.Content.Bounds.Size);
    });

    [Fact]
    public void The_frame_stays_pinned_from_creation_until_released_and_is_not_replaced_while_locked() => RenderThread.Run(() =>
    {
        AvaloniaPanel.Create(() => new Border(), 1, 1).Dispose();
        var window = new Window();
        var framebuffer = RetainedFramebuffer.Install(window, 16, 12);
        try
        {
            // Skia may read the address before the first lock.
            Assert.NotEqual(IntPtr.Zero, framebuffer.Address);
            using (framebuffer.Lock(out _))
                Assert.Throws<InvalidOperationException>(() => framebuffer.Resize(8, 6));
            framebuffer.Resize(8, 6);
            Assert.Equal((8, 6), (framebuffer.Frame.Width, framebuffer.Frame.Height));
            Assert.NotEqual(IntPtr.Zero, framebuffer.Address);
        }
        finally
        {
            window.Close();
            framebuffer.Release();
        }
        Assert.Equal(IntPtr.Zero, framebuffer.Address);
    });

    [Fact]
    public void A_changed_frame_allocates_less_than_the_21_Oct_probe() => RenderThread.Run(() =>
    {
        // The 21 Oct probe (#115): one 31-row window, 12 KB per changed tick.
        const int frames = 200;
        using var panel = AvaloniaPanel.Create(() => Rows(Brushes.Red), Width, Height);
        var indicator = IndicatorOf(panel);
        void Change(int i)
        {
            indicator.Background = i % 2 == 0 ? Brushes.Lime : Brushes.Red;
            PanelCompositor.Advance();
        }
        for (var i = 0; i < 50; i++) Change(i);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < frames; i++)
        {
            Change(i);
            Assert.True(panel.Changed);
        }
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;

        _output.WriteLine($"changed frame: {bytes} B");
        Assert.InRange(bytes, 0, 12 * 1024);
    });

    private static readonly FieldInfo LastRenderedFrame = typeof(AvaloniaHeadlessPlatform).Assembly
        .GetType("Avalonia.Headless.HeadlessWindowImpl")!.GetField("_lastRenderedFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Rectangle Union(IReadOnlyList<Rectangle> rectangles) =>
        rectangles.Aggregate(Rectangle.Empty, (union, rect) => union.IsEmpty ? rect : Rectangle.Union(union, rect));

    /// <summary>Every pixel that differs from <paramref name="before"/> lies inside the panel's reported dirty rectangles.</summary>
    private static void AssertChangesInside(byte[] before, AvaloniaPanel panel, string what)
    {
        var frame = panel.Frame;
        Assert.Equal(before.Length, frame.Pixels.Length);
        for (var i = 0; i < before.Length; i += 4)
        {
            if (before.AsSpan(i, 4).SequenceEqual(frame.Pixels.AsSpan(i, 4))) continue;
            int x = i / 4 % frame.Width, y = i / 4 / frame.Width;
            Assert.True(frame.DirtyRectangles.Any(rect => rect.Contains(x, y)), $"{what}: pixel ({x},{y}) changed outside {Union(frame.DirtyRectangles)}");
        }
    }

    /// <summary>A framed list of 31 text rows with a 40×20 indicator among them that the tests recolour.</summary>
    private static Border Rows(IBrush indicatorBrush)
    {
        var stack = new StackPanel();
        for (var i = 0; i < 31; i++)
            stack.Children.Add(new TextBlock { Text = $"Row {i}: the quick brown fox", Foreground = Brushes.White, Margin = new Thickness(4, 0) });
        stack.Children.Insert(15, new Border
        {
            Width = 40, Height = 20, Background = indicatorBrush, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(130, 0)
        });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x18, 0x10)),
            BorderBrush = Brushes.Goldenrod,
            BorderThickness = new Thickness(1),
            Child = stack
        };
    }

    private static Border IndicatorOf(AvaloniaPanel panel) => ((StackPanel)((Border)panel.Content).Child!).Children.OfType<Border>().Single();
}
