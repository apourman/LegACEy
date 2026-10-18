using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>The Dereth radar window, driven as a player does, against a fake of the radar's port and a fake ground.</summary>
public sealed class RetailRadarWindowTests
{
    [Fact]
    public void Hovering_shows_the_resize_corners_only_while_the_UI_is_unlocked() => RenderThread.Run(() =>
    {
        var port = PortWith();
        using var panel = Open(port, out var window);
        Assert.False(window.CornersVisible);

        panel.PointerMove(6, 6);
        panel.Tick();
        Assert.True(window.CornersVisible);

        // Locking while the pointer is over the radar hides them, and the window no longer resizes.
        port.UiLocked = true;
        window.Refresh();
        Assert.False(window.CornersVisible);
        var windows = new WindowManager(new System.Drawing.Size(1600, 1000), new MemoryWindowPositionStore(), "Thistledown", "Alice");
        var radar = windows.Open(new WindowDefinition("radar", "Radar", RetailRadarWindow.DesignWidth, RetailRadarWindow.DesignHeight, titleBarHeight: 1,
            resizing: RetailRadarWindow.Resizing(() => port.UiLocked), dragsFromContent: true), new System.Drawing.Point(100, 100));
        var corner = new System.Drawing.Point(radar.Bounds.Right - 2, radar.Bounds.Bottom - 2);
        windows.Press(corner);
        windows.Move(new System.Drawing.Point(corner.X + 100, corner.Y + 100));
        windows.Release();
        Assert.Equal(new System.Drawing.Size(292, 348), radar.Size);

        port.UiLocked = false;
        window.Refresh();
        Assert.True(window.CornersVisible);
    });

    [Fact]
    public void Dragging_an_edge_or_corner_draws_the_radar_larger_or_smaller_keeping_its_shape()
    {
        var windows = new WindowManager(new System.Drawing.Size(1600, 1000), new MemoryWindowPositionStore(), "Thistledown", "Alice");
        var radar = windows.Open(new WindowDefinition("radar", "Radar", RetailRadarWindow.DesignWidth, RetailRadarWindow.DesignHeight, titleBarHeight: 1,
            resizing: RetailRadarWindow.Resizing(() => false), dragsFromContent: true), new System.Drawing.Point(100, 100));
        Assert.Equal(new System.Drawing.Size(292, 348), radar.Size);

        void Drag(System.Drawing.Point grab, int dx, int dy)
        {
            windows.Press(grab);
            windows.Move(new System.Drawing.Point(grab.X + dx, grab.Y + dy));
            windows.Release();
        }
        // The right edge alone brings the height along; the corner goes no larger than three times the layout, nor smaller than it.
        Drag(new System.Drawing.Point(radar.Bounds.Right - 2, radar.Bounds.Top + 100), 73, 0);
        Assert.Equal(new System.Drawing.Size(365, 435), radar.Size);
        Drag(new System.Drawing.Point(radar.Bounds.Right - 2, radar.Bounds.Bottom - 2), 500, 500);
        Assert.Equal(new System.Drawing.Size(438, 522), radar.Size);
        Drag(new System.Drawing.Point(radar.Bounds.Right - 2, radar.Bounds.Bottom - 2), -500, -500);
        Assert.Equal(new System.Drawing.Size(146, 174), radar.Size);
    }

    private const uint Outdoors = 0xC6A90014;
    private const uint Indoors = 0xC6A90100;

    [Fact]
    public void Hovering_a_dot_names_it_with_its_distance_and_direction() => RenderThread.Run(() =>
    {
        var port = PortWith(new RadarObject(7, "Mirra", RadarColor.Gold, RadarBehavior.ShowAlways, Near(east: 20, north: 20)));
        using var panel = Open(port, out var window);

        Hover(panel, window, 7);

        Assert.Equal("Mirra · 28 m NE", window.HoverLabel);
    });

    [Fact]
    public void Clicking_a_dot_selects_it_through_the_port() => RenderThread.Run(() =>
    {
        var port = PortWith(new RadarObject(7, "Mirra", RadarColor.Gold, RadarBehavior.ShowAlways, Near(east: 20, north: 0)));
        using var panel = Open(port, out var window);

        Click(panel, window, 7);

        Assert.Equal(new uint[] { 7 }, port.Selected);
    });

    [Fact]
    public void The_wheel_zooms_in_and_out_within_25_and_150_metres() => RenderThread.Run(() =>
    {
        using var panel = Open(PortWith(), out var window);

        Wheel(panel, window, notches: 10);
        Assert.Equal(25, window.Options.Zoom);
        Wheel(panel, window, notches: -30);
        Assert.Equal(150, window.Options.Zoom);
    });

    [Fact]
    public void The_lock_button_locks_and_unlocks_the_UI_through_the_port() => RenderThread.Run(() =>
    {
        var port = PortWith();
        using var panel = Open(port, out var window);

        Press(panel, Medallion(panel, "Lock UI"));
        Assert.True(port.UiLocked);
        Press(panel, Medallion(panel, "Unlock UI"));
        Assert.False(port.UiLocked);
    });

    [Fact]
    public void The_lock_button_is_not_shown_where_retail_has_no_lock() => RenderThread.Run(() =>
    {
        var port = PortWith();
        port.UiLockAvailable = false;
        using var panel = Open(port, out _);

        Assert.False(Medallion(panel, "Lock UI").IsVisible);
    });

    [Fact]
    public void The_options_popover_toggles_north_up_and_saves_the_change() => RenderThread.Run(() =>
    {
        RadarOptions? saved = null;
        using var panel = Open(PortWith(), out var window, saved: options => saved = options);

        Press(panel, Medallion(panel, "Radar options"));
        Press(panel, Row(panel, "North up"));

        Assert.True(window.Options.NorthUp);
        Assert.True(saved!.NorthUp);
    });

    [Fact]
    public void Outdoors_the_ground_turns_under_the_dots_and_indoors_it_falls_back_to_dots() => RenderThread.Run(() =>
    {
        var port = PortWith();
        var ground = new GroundTiles(new FakeGround());
        using var panel = Open(port, out var window, ground: ground);
        WaitForGround(ground);

        // One tile is converted to a bitmap a read, so the nine come in over nine reads.
        window.Refresh();
        Assert.Equal(1, window.TilesDrawn);
        for (var read = 1; read < 9; read++) window.Refresh();
        Assert.Equal(9, window.TilesDrawn);

        port.Player = new RadarPlayer(new RadarPosition(Indoors, 60, 84, 0), 0);
        window.Refresh();
        Assert.Equal(0, window.TilesDrawn);
    });

    [Fact]
    public void Ground_off_draws_dots_on_dark() => RenderThread.Run(() =>
    {
        var ground = new GroundTiles(new FakeGround());
        using var panel = Open(PortWith(), out var window, ground: ground, options: new RadarOptions(Ground: false));

        window.Refresh();

        Assert.Equal(0, window.TilesDrawn);
    });

    [Fact]
    public void Dragging_the_frame_moves_the_radar_unless_the_UI_is_locked() => RenderThread.Run(() =>
    {
        var moves = 0;
        var port = PortWith();
        using var panel = Open(port, out var window, startMove: () => moves++);

        panel.PointerDown(3, 170);
        panel.PointerUp(3, 170);
        Assert.Equal(1, moves);

        port.UiLocked = true;
        panel.PointerDown(3, 170);
        panel.PointerUp(3, 170);
        Assert.Equal(1, moves);
    });

    [Fact]
    public void Clicking_the_coordinates_hands_them_on_without_moving_the_radar() => RenderThread.Run(() =>
    {
        var moves = 0;
        using var panel = Open(PortWith(), out var window, startMove: () => moves++);
        string? clicked = null;
        window.CoordinatesClicked += text => clicked = text;

        Press(panel, Medallion(panel, "Click to paste in chat"));
        Assert.Equal(PortWith().Player!.Position.Coordinates, clicked);
        Assert.Equal(0, moves);
    });

    [Fact]
    public void Options_and_zoom_come_back_for_the_next_window() => RenderThread.Run(() =>
    {
        RadarOptions? saved = null;
        using var panel = Open(PortWith(), out var window, saved: options => saved = options);
        Press(panel, Medallion(panel, "Radar options"));
        Press(panel, Row(panel, "Range rings"));
        Wheel(panel, window, notches: 2);

        using var again = Open(PortWith(), out var reopened, options: saved);

        Assert.Equal(window.Options, reopened.Options);
        Assert.True(reopened.Options.Rings);
    });

    [Fact]
    public void A_radar_with_nothing_moving_draws_no_new_frame() => RenderThread.Run(() =>
    {
        var port = PortWith(new RadarObject(7, "Mirra", RadarColor.Gold, RadarBehavior.ShowAlways, Near(east: 20, north: 20)));
        using var panel = Open(port, out var window);
        window.Refresh();
        panel.Tick();
        var captures = panel.FrameCaptureCount;

        window.Refresh();
        panel.Tick();
        Assert.Equal(captures, panel.FrameCaptureCount);

        port.Player = new RadarPlayer(new RadarPosition(Outdoors, 60, 84, 0), 90);
        window.Refresh();
        panel.Tick();
        Assert.Equal(captures + 1, panel.FrameCaptureCount);
    });

    // A radar window over the port and ground, shown at its size, with its frame drawn once.
    private static AvaloniaPanel Open(FakeRadarPort port, out RetailRadarWindow window, GroundTiles? ground = null, RadarOptions? options = null,
        Action<RadarOptions>? saved = null, Action? startMove = null)
    {
        RetailRadarWindow? created = null;
        var panel = AvaloniaPanel.Create(() => created = new RetailRadarWindow(port, () => ground, options ?? new RadarOptions(), saved ?? (_ => { }),
            () => 0, startMove ?? (() => { })), RetailRadarWindow.DesignWidth, RetailRadarWindow.DesignHeight);
        window = created!;
        window.Refresh();
        panel.Tick();
        return panel;
    }

    private static FakeRadarPort PortWith(params RadarObject[] objects)
    {
        var port = new FakeRadarPort { Player = new RadarPlayer(new RadarPosition(Outdoors, 60, 84, 0), 0) };
        port.Objects.AddRange(objects);
        return port;
    }

    // A point east and north of the player, in the same landblock.
    private static RadarPosition Near(float east, float north) => new(Outdoors, 60 + east, 84 + north, 0);

    // A dot's centre in panel pixels, from the window's own frame.
    private static Point DotAt(RetailRadarWindow window, uint id)
    {
        var dot = window.Frame!.Dots.Single(candidate => candidate.Id == id);
        return new Point(RetailRadarWindow.ScopeOrigin.X + dot.At.X, RetailRadarWindow.ScopeOrigin.Y + dot.At.Y) * RetailRadarWindow.Scale;
    }

    private static void Hover(AvaloniaPanel panel, RetailRadarWindow window, uint id)
    {
        var at = DotAt(window, id);
        panel.PointerMove(at.X, at.Y);
        panel.Tick();
    }

    private static void Click(AvaloniaPanel panel, RetailRadarWindow window, uint id)
    {
        var at = DotAt(window, id);
        panel.PointerMove(at.X, at.Y);
        panel.PointerDown(at.X, at.Y);
        panel.PointerUp(at.X, at.Y);
        panel.Tick();
    }

    private static void Wheel(AvaloniaPanel panel, RetailRadarWindow window, int notches)
    {
        var centre = new Point(RetailRadarWindow.ScopeOrigin.X + 59, RetailRadarWindow.ScopeOrigin.Y + 59) * RetailRadarWindow.Scale;
        panel.MouseWheel(centre.X, centre.Y, 0, notches);
        panel.Tick();
    }

    private static void Press(AvaloniaPanel panel, Control control)
    {
        panel.Tick();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
        panel.Tick();
    }

    private static Control Medallion(AvaloniaPanel panel, string tip) =>
        panel.Content.GetVisualDescendants().OfType<Control>().First(control => ToolTip.GetTip(control) as string == tip);

    // A popover row: the text inside it takes the press, and the row takes it as its own.
    private static Control Row(AvaloniaPanel panel, string text) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == text);

    private static void WaitForGround(GroundTiles ground)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            // Every landblock the window can draw is built once the nine are there.
            if (Enumerable.Range(0, 9).All(i => ground.Tile(Neighbour(0xC6A9, i)) != null)) return;
            System.Threading.Thread.Sleep(10);
        }
        Assert.Fail("The fake ground did not build in time.");
    }

    private static ushort Neighbour(ushort centre, int index)
    {
        var x = (centre >> 8) + index / 3 - 1;
        var y = (centre & 0xFF) + index % 3 - 1;
        return (ushort)((x << 8) | y);
    }

    /// <summary>A ground with flat green terrain everywhere, so every landblock builds.</summary>
    private sealed class FakeGround : IGroundSource
    {
        public GroundLandblock? ReadLandblock(int x, int y) =>
            new(x, y, new ushort[81], new float[81], Array.Empty<GroundFootprint>());

        public GameImage? TerrainTexture(uint terrainType) => new(4, 4, Enumerable.Range(0, 64).Select(i => (byte)(i % 4 == 3 ? 0xFF : 0x40)).ToArray());
    }
}
