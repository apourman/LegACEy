using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rectangle = System.Drawing.Rectangle;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class PanelHostRenderingTests
{
    [Fact]
    public void See_through_content_stays_transparent_so_a_drag_icon_has_no_background() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new Grid { Width = 32, Height = 32 }, 32, 32);
        panel.Tick();

        Assert.All(panel.Frame.Pixels, value => Assert.Equal(0, value));
    });

    [Fact]
    public void Headless_input_clicks_button_and_exposes_cursor_kind() => RenderThread.Run(() =>
    {
        var clicks = 0;
        using var panel = AvaloniaPanel.Create(() => new Button
        {
            Content = "Click",
            Width = 100,
            Height = 40,
            Cursor = new Cursor(StandardCursorType.Hand)
        }, 120, 60);

        var button = (Button)panel.Content;
        button.Click += (_, _) => clicks++;
        var restingPixels = panel.Frame.Pixels.ToArray();
        panel.PointerMove(30, 20);
        Assert.True(button.IsPointerOver);
        Assert.True(panel.Tick());
        var hoverPixels = panel.Frame.Pixels.ToArray();
        Assert.False(restingPixels.SequenceEqual(hoverPixels));
        panel.PointerDown(30, 20);
        Assert.True(button.IsPressed);
        Assert.True(panel.Tick());
        Assert.False(hoverPixels.SequenceEqual(panel.Frame.Pixels));
        panel.PointerUp(30, 20);
        Assert.False(button.IsPressed);

        Assert.Equal(1, clicks);
        Assert.Same(button.Cursor, panel.CursorKind);
    });

    [Fact]
    public void A_scaled_panel_lays_out_at_design_size_and_maps_presses_to_it() => RenderThread.Run(() =>
    {
        Point? pressed = null;
        var content = new Border { Background = Brushes.Gray };
        content.PointerPressed += (_, e) => pressed = e.GetPosition(content);
        using var panel = AvaloniaPanel.Create(() => content, 100, 60, scale: 0.5);

        Assert.Equal((100, 60), (panel.Frame.Width, panel.Frame.Height));
        Assert.Equal(new Size(200, 120), content.Bounds.Size);
        Assert.Same(content, panel.Content);
        panel.PointerDown(50, 30);
        Assert.Equal(new Point(100, 60), pressed);
        Assert.Equal(new Point(100, 60), panel.ToContent(50, 30));

        panel.Resize(80, 40);
        panel.Tick();
        Assert.Equal(new Size(160, 80), content.Bounds.Size);
    });

    [Fact]
    public void A_scaled_panel_draws_a_1px_border_as_whole_opaque_pixels_on_every_side() => RenderThread.Run(() =>
    {
        // 95 / 0.85 is not a whole design size, which is where a scale transform clipped and faded borders.
        using var panel = AvaloniaPanel.Create(() => new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1) }, 95, 57, scale: 0.85);

        var frame = panel.Frame;
        byte Alpha(int x, int y) => frame.Pixels[((y * frame.Width) + x) * 4 + 3];
        Assert.All(new[] { Alpha(0, 28), Alpha(frame.Width - 1, 28), Alpha(47, 0), Alpha(47, frame.Height - 1) }, alpha => Assert.Equal(0xff, alpha));
    });

    [Fact]
    public void Text_input_and_keyboard_focus_follow_the_focused_text_box() => RenderThread.Run(() =>
    {
        TextBox? textBox = null;
        using var panel = AvaloniaPanel.Create(() => textBox = new TextBox { Width = 180 }, 200, 60);

        Assert.False(panel.WantsKeyboard);
        panel.PointerDown(20, 20);
        panel.PointerUp(20, 20);
        Assert.True(panel.WantsKeyboard);

        panel.KeyDown(Key.A, KeyModifiers.Control);
        panel.KeyUp(Key.A, KeyModifiers.Control);
        panel.TextInput("hello");
        Assert.Equal("hello", textBox!.Text);

        panel.ClearFocus();
        Assert.False(panel.WantsKeyboard);
    });

    [Fact]
    public void A_press_outside_the_focused_text_box_leaves_it() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new StackPanel
        {
            Children = { new TextBox { Width = 180, Height = 30 }, new Border { Height = 30, Background = Brushes.Gray } }
        }, 200, 60);

        panel.PointerDown(20, 10);
        panel.PointerUp(20, 10);
        Assert.True(panel.WantsKeyboard);

        panel.PointerDown(20, 45);
        panel.PointerUp(20, 45);
        Assert.False(panel.WantsKeyboard);
    });

    [Fact]
    public void Wheel_input_scrolls_a_list_box() => RenderThread.Run(() =>
    {
        ListBox? listBox = null;
        using var panel = AvaloniaPanel.Create(() => listBox = new ListBox
        {
            Height = 50,
            ItemsSource = Enumerable.Range(0, 30).Select(i => $"Item {i}")
        }, 160, 60);
        panel.Tick();
        var scrollViewer = listBox!.GetVisualDescendants().OfType<ScrollViewer>().First();

        panel.MouseWheel(40, 30, 0, -120);

        Assert.True(scrollViewer.Offset.Y > 0);
        Assert.True(panel.Tick());
        Assert.NotEmpty(panel.Frame.DirtyRectangles);
        Assert.All(panel.Frame.DirtyRectangles, rect => Assert.True(rect.Width < 160 || rect.Height < 60));
    });

    [Fact]
    public void Headless_input_clicks_a_button_types_into_a_text_box_and_scrolls_a_list() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new InputFixture(), 360, 300);
        panel.Tick();
        var fixture = (InputFixture)panel.Content;
        var button = fixture.ClickButton;
        var textBox = fixture.Input;
        var label = fixture.Echo;
        var listBox = fixture.Items;

        var buttonPoint = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), panel.Content)!.Value;
        panel.PointerDown(buttonPoint.X, buttonPoint.Y);
        panel.PointerUp(buttonPoint.X, buttonPoint.Y);
        Assert.Equal("Click count: 1", button.Content);

        var textPoint = textBox.TranslatePoint(new Avalonia.Point(12, 12), panel.Content)!.Value;
        panel.PointerDown(textPoint.X, textPoint.Y);
        panel.PointerUp(textPoint.X, textPoint.Y);
        panel.TextInput("typed");
        Assert.Equal("typed", label.Text);
        Assert.True(panel.WantsKeyboard);

        var scrollViewer = listBox.GetVisualDescendants().OfType<ScrollViewer>().First();
        var listPoint = listBox.TranslatePoint(new Avalonia.Point(30, 30), panel.Content)!.Value;
        panel.MouseWheel(listPoint.X, listPoint.Y, 0, -120);
        Assert.True(scrollViewer.Offset.Y > 0);
    });

    [Fact]
    public void Tick_renders_a_control_to_a_bgra_frame() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(
            () => new Border { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56)) },
            16,
            16);

        panel.Tick();

        var frame = panel.Frame;
        Assert.Equal(16, frame.Width);
        Assert.Equal(16, frame.Height);
        Assert.Equal(16 * 4, frame.Stride);
        foreach (var (x, y) in new[] { (0, 0), (15, 0), (8, 8), (0, 15), (15, 15) })
        {
            var offset = (y * frame.Stride) + (x * 4);
            Assert.Equal(new byte[] { 0x56, 0x34, 0x12, 0xff }, frame.Pixels.Skip(offset).Take(4));
        }
    });

    [Fact]
    public void Tick_renders_text_over_the_background() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(
            () => new Border
            {
                Background = Brushes.Black,
                Child = new TextBlock { Text = "LegACEy", FontSize = 20, Foreground = Brushes.White }
            },
            120,
            40);

        panel.Tick();

        var frame = panel.Frame;
        var litPixels = Enumerable.Range(0, frame.Width * frame.Height)
            .Count(pixel => frame.Pixels[(pixel * 4) + 2] > 0x80);
        Assert.InRange(litPixels, 20, frame.Width * frame.Height / 2);
    });

    [Fact]
    public void Tick_reports_whether_the_frame_changed() => RenderThread.Run(() =>
    {
        var border = default(Border);
        using var panel = AvaloniaPanel.Create(() => border = new Border { Background = Brushes.Black }, 8, 8);

        Assert.False(panel.Tick());

        border!.Background = Brushes.White;
        Assert.True(panel.Tick());
        Assert.Equal(0xff, panel.Frame.Pixels[0]);
        Assert.False(panel.Tick());
    });

    [Fact]
    public void Tick_reports_dirty_rectangles_and_leaves_them_empty_when_idle() => RenderThread.Run(() =>
    {
        var control = default(RenderCountControl);
        using var panel = AvaloniaPanel.Create(() =>
        {
            control = new RenderCountControl { Width = 10, Height = 10 };
            return new StackPanel { Children = { control } };
        }, 40, 30);

        Assert.Contains(new Rectangle(0, 0, 40, 30), panel.Frame.DirtyRectangles);
        Assert.False(panel.Tick());
        Assert.Empty(panel.Frame.DirtyRectangles);

        var idleRenderCount = control!.RenderCount;
        var idleCaptureCount = panel.FrameCaptureCount;
        control.Tag = "updated-without-visual-change";
        Assert.False(panel.Tick());
        Assert.Equal(idleCaptureCount, panel.FrameCaptureCount);
        Assert.Equal(idleRenderCount, control.RenderCount);
        Assert.Empty(panel.Frame.DirtyRectangles);

        control.Opacity = 0.5;
        Assert.True(panel.Tick());
        Assert.Equal(idleCaptureCount + 1, panel.FrameCaptureCount);
        Assert.True(control.RenderCount > idleRenderCount);
        Assert.NotEmpty(panel.Frame.DirtyRectangles);
        Assert.All(panel.Frame.DirtyRectangles, rect => Assert.True(rect.Width < 40 || rect.Height < 30));
    });

    [Fact]
    public void Replacing_a_child_and_updating_it_repaints_without_input() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new Border
        {
            Child = new Border { Background = Brushes.Black }
        }, 8, 8);
        Assert.False(panel.Tick());
        var replacement = new Border { Background = Brushes.White };
        ((Border)panel.Content).Child = replacement;
        Assert.True(panel.Tick());
        Assert.Equal(0xff, panel.Frame.Pixels[0]);
        replacement.Background = Brushes.Red;
        Assert.True(panel.Tick());
        Assert.Equal(0, panel.Frame.Pixels[0]);
        Assert.False(panel.Tick());
    });

    [Fact]
    public void Moving_a_control_outside_the_panel_clears_its_old_pixels() => RenderThread.Run(() =>
    {
        Border? child = null;
        using var panel = AvaloniaPanel.Create(() => new Canvas
        {
            Background = Brushes.Black,
            Children = { (child = new Border { Width = 4, Height = 4, Background = Brushes.White }) }
        }, 8, 8);
        Assert.False(panel.Tick());
        child!.RenderTransform = new TranslateTransform(20, 0);
        Assert.True(panel.Tick());
        Assert.Equal(0, panel.Frame.Pixels[0]);
        Assert.False(panel.Tick());
    });

    [Fact]
    public void Custom_visual_invalidation_repaints_without_a_host_request() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new RenderCountControl(), 8, 8);
        Assert.False(panel.Tick());
        var control = (RenderCountControl)panel.Content;
        control.Brush = Brushes.White;
        control.InvalidateVisual();
        Assert.True(panel.Tick());
        Assert.Equal(0xff, panel.Frame.Pixels[0]);
        Assert.False(panel.Tick());
    });

    [Fact]
    public void Mutating_a_background_brush_repaints_without_replacing_the_property() => RenderThread.Run(() =>
    {
        var brush = new SolidColorBrush(Colors.Black);
        using var panel = AvaloniaPanel.Create(() => new Border { Background = brush }, 8, 8);
        Assert.False(panel.Tick());
        Assert.Null(panel.LastError);
        brush.Color = Colors.White;
        Assert.True(panel.Tick());
        Assert.Equal(0xff, panel.Frame.Pixels[0]);
        Assert.False(panel.Tick());
    });

    [Fact]
    public void Mutating_a_nested_gradient_stop_repaints() => RenderThread.Run(() =>
    {
        var brush = new LinearGradientBrush
        {
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 1) }
        };
        using var panel = AvaloniaPanel.Create(() => new Border { Background = brush }, 8, 8);
        Assert.False(panel.Tick());
        Assert.Null(panel.LastError);
        brush.GradientStops[0].Color = Colors.White;
        Assert.True(panel.Tick());
        Assert.NotEqual(0, panel.Frame.Pixels[0]);
        Assert.False(panel.Tick());
    });

    [Fact]
    public void Hidden_panel_drains_work_without_capturing_when_another_panel_ticks() => RenderThread.Run(() =>
    {
        using var hidden = AvaloniaPanel.Create(() => new RenderCountControl(), 8, 8);
        using var visible = AvaloniaPanel.Create(() => new Border { Background = Brushes.Black }, 8, 8);
        var control = (RenderCountControl)hidden.Content;
        var captures = hidden.FrameCaptureCount;
        Dispatcher.UIThread.Post(() =>
        {
            control.Brush = Brushes.White;
            control.InvalidateVisual();
        });
        hidden.DrainDispatcher();
        visible.Tick();
        Assert.Same(Brushes.White, control.Brush);
        Assert.Equal(captures, hidden.FrameCaptureCount);
        Assert.Equal(0, hidden.Frame.Pixels[0]);
        Assert.True(hidden.Tick());
        Assert.Equal(0xff, hidden.Frame.Pixels[0]);
        Assert.False(hidden.Tick());
    });

    [Fact]
    public void Drain_dispatcher_runs_queued_work_without_rendering() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new Border { Background = Brushes.Black }, 8, 8);
        Assert.False(panel.Tick());
        var drained = false;
        Dispatcher.UIThread.Post(() => drained = true);

        panel.DrainDispatcher();

        Assert.True(drained);
        Assert.Empty(panel.Frame.DirtyRectangles);
    });

    [Fact]
    public void Resize_and_content_loss_force_full_frame_uploads() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new Border { Background = Brushes.Black }, 8, 6);

        panel.Resize(12, 10);
        Assert.True(panel.Tick());
        Assert.Equal(12, panel.Frame.Width);
        Assert.Equal(10, panel.Frame.Height);
        Assert.Contains(new Rectangle(0, 0, 12, 10), panel.Frame.DirtyRectangles);

        Assert.True(panel.ContentLost());
        Assert.True(panel.Tick());
        Assert.Contains(new Rectangle(0, 0, 12, 10), panel.Frame.DirtyRectangles);
    });

    [Fact]
    public void Resize_keeps_the_content_attached_so_its_owner_does_not_see_a_detach() => RenderThread.Run(() =>
    {
        var content = new Border { Background = Brushes.Black };
        var detaches = 0;
        content.DetachedFromVisualTree += (_, _) => detaches++;
        using var panel = AvaloniaPanel.Create(() => content, 8, 6);

        panel.Resize(12, 10);
        panel.Tick();

        Assert.Same(content, panel.Content);
        Assert.Equal(0, detaches);
    });

    [Fact]
    public void Handler_exception_is_reported_by_the_panel_instead_of_escaping() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new Button { Content = "Fail" }, 80, 40);
        var button = (Button)panel.Content;
        button.Click += (_, _) => throw new InvalidOperationException("deliberate test failure");
        IPointer? pointer = null;
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        panel.PointerDown(20, 20);
        var error = Record.Exception(() => panel.PointerUp(20, 20));

        Assert.Null(error);
        Assert.IsType<InvalidOperationException>(panel.LastError);
        Assert.Equal("deliberate test failure", panel.LastError!.Message);
        Assert.NotNull(pointer);
        Assert.Null(pointer!.Captured);
    });

    [Fact]
    public void Throwing_button_in_a_panel_raises_the_error_event() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new InputFixture(), 360, 300);
        var failButton = ((InputFixture)panel.Content).FailButton;
        Exception? reported = null;
        panel.Error += error => reported = error;
        var point = failButton.TranslatePoint(new Avalonia.Point(failButton.Bounds.Width / 2,
            failButton.Bounds.Height / 2), panel.Content)!.Value;

        panel.PointerDown(point.X, point.Y);
        panel.PointerUp(point.X, point.Y);

        Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal("Deliberate test failure from the input fixture.", reported!.Message);
    });

    [Fact]
    public void Tick_from_another_thread_is_rejected()
    {
        var panel = RenderThread.Run(() => AvaloniaPanel.Create(() => new Border(), 4, 4));
        try
        {
            Assert.Throws<InvalidOperationException>(() => panel.Tick());
        }
        finally
        {
            RenderThread.Run(panel.Dispose);
        }
    }
}

/// <summary>A button that counts its clicks, a text box echoed into a label, a scrollable list, and a button that throws.</summary>
internal sealed class InputFixture : StackPanel
{
    public InputFixture()
    {
        var clicks = 0;
        ClickButton = new Button { Content = "Click count: 0" };
        ClickButton.Click += (_, _) => ClickButton.Content = $"Click count: {++clicks}";
        Input = new TextBox { Width = 200 };
        Echo = new TextBlock();
        Input.TextChanged += (_, _) => Echo.Text = Input.Text;
        Items = new ListBox { Height = 80, ItemsSource = Enumerable.Range(1, 40).Select(i => "Row " + i).ToArray() };
        FailButton = new Button { Content = "Trigger UI failure" };
        FailButton.Click += (_, _) => throw new InvalidOperationException("Deliberate test failure from the input fixture.");
        Children.Add(ClickButton);
        Children.Add(Input);
        Children.Add(Echo);
        Children.Add(Items);
        Children.Add(FailButton);
    }

    public Button ClickButton { get; }
    public TextBox Input { get; }
    public TextBlock Echo { get; }
    public ListBox Items { get; }
    public Button FailButton { get; }
}

internal sealed class RenderCountControl : Control
{
    public int RenderCount { get; private set; }
    public IBrush Brush { get; set; } = Brushes.Black;

    public override void Render(DrawingContext context)
    {
        RenderCount++;
        context.FillRectangle(Brush, new Rect(Bounds.Size));
    }
}
