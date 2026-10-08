using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

public sealed class ScrollbarDragTests
{
    [Fact]
    public void Rapid_thumb_drag_preserves_capture_reversals_and_release() => RenderThread.Run(() =>
    {
        using var fixture = new DragFixture();
        var panel = fixture.Panel;
        var thumb = fixture.Thumb;
        IPointer? pointer = null;
        thumb.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
            RoutingStrategies.Bubble, handledEventsToo: true);
        var point = fixture.ThumbCenter;
        panel.PointerDown(point.X, point.Y);
        Assert.NotNull(pointer);
        Assert.NotNull(pointer!.Captured);
        Assert.Contains(thumb, ((Visual)pointer.Captured!).GetVisualAncestors().Prepend((Visual)pointer.Captured));
        panel.PointerMove(point.X, point.Y + 100);
        var down = fixture.Viewer.Offset.Y;
        Assert.True(down > 0);
        panel.PointerMove(point.X, point.Y + 30);
        Assert.True(fixture.Viewer.Offset.Y < down);
        // Capture must still deliver movement outside the window.
        panel.PointerMove(-20, point.Y + 150);
        Assert.True(fixture.Viewer.Offset.Y > down);
        panel.PointerUp(-20, point.Y + 150);
        Assert.Null(pointer.Captured);
        panel.Tick();
        var releasedOffset = fixture.Viewer.Offset.Y;
        panel.PointerMove(point.X, point.Y + 20);
        panel.Tick();
        Assert.Equal(releasedOffset, fixture.Viewer.Offset.Y);
        Assert.Null(panel.LastError);
    });

    [Fact]
    public void Thumb_moves_do_not_render_until_the_panel_tick() => RenderThread.Run(() =>
    {
        using var fixture = new DragFixture();
        var panel = fixture.Panel;
        var point = fixture.ThumbCenter;
        panel.PointerDown(point.X, point.Y);
        panel.Tick();
        var renders = fixture.Marker.RenderCount;
        for (var i = 1; i <= 12; i++) panel.PointerMove(point.X, point.Y + i * 5);
        Assert.True(fixture.Viewer.Offset.Y > 0);
        Assert.Equal(renders, fixture.Marker.RenderCount);
        Assert.True(panel.Tick());
        Assert.True(fixture.Marker.RenderCount > renders);
        panel.PointerUp(point.X, point.Y + 60);
        Assert.Null(panel.LastError);
    });

    [Fact]
    public void Batched_drag_finishes_at_the_same_offset_as_moves_with_intermediate_ticks() => RenderThread.Run(() =>
    {
        double Drag(bool tickBetweenMoves)
        {
            using var fixture = new DragFixture();
            var panel = fixture.Panel;
            var point = fixture.ThumbCenter;
            panel.PointerDown(point.X, point.Y);
            foreach (var distance in new[] { 80, 120, 20, 160, 45 })
            {
                panel.PointerMove(point.X, point.Y + distance);
                if (tickBetweenMoves) panel.Tick();
            }
            panel.PointerUp(point.X, point.Y + 45);
            panel.Tick();
            Assert.Null(panel.LastError);
            return fixture.Viewer.Offset.Y;
        }
        var expected = Drag(true);
        Assert.True(expected > 0);
        Assert.Equal(expected, Drag(false), precision: 3);
    });

    private sealed class DragFixture : IDisposable
    {
        private readonly PortalDat? _dat;
        public AvaloniaPanel Panel { get; }
        public ScrollViewer Viewer { get; }
        public Thumb Thumb { get; }
        public RenderCountControl Marker { get; } = new()
        {
            Width = 1, Height = 1, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        public Point ThumbCenter => Thumb.TranslatePoint(new Point(Thumb.Bounds.Width / 2,
            Thumb.Bounds.Height / 2), Panel.Content)!.Value;

        public DragFixture()
        {
            var path = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
            _dat = string.IsNullOrEmpty(path) ? null : new PortalDat(path);
            IGameArtSource art = _dat is null ? new NoArt() : _dat;
            Panel = AvaloniaPanel.Create(() => new Grid
            {
                Children = { new ThemeWindowChrome(art, "drag-list", new ScrollViewer { Content = Rows() }), Marker }
            }, 620, 460);
            Panel.ApplyTheme(new AcClientTheme(art));
            for (var i = 0; i < 10; i++) Panel.Tick();
            Viewer = Panel.Content.GetVisualDescendants().OfType<ScrollViewer>().First();
            var bar = Viewer.GetVisualDescendants().OfType<ScrollBar>()
                .Single(control => control.Orientation == Orientation.Vertical && control.IsVisible);
            Thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            Assert.True(Thumb.Bounds.Width > 0 && Thumb.Bounds.Height > 0);
            // A real visual invalidated by each delivered move detects hidden rendering.
            Panel.Content.AddHandler(InputElement.PointerMovedEvent, (_, _) => Marker.InvalidateVisual(),
                RoutingStrategies.Bubble, handledEventsToo: true);
        }
        public void Dispose() { Panel.Dispose(); _dat?.Dispose(); }

        private static StackPanel Rows()
        {
            var rows = new StackPanel();
            for (var i = 0; i < 200; i++)
                rows.Children.Add(new TextBlock { Text = "Row " + i, Height = 20 });
            return rows;
        }
    }

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}
