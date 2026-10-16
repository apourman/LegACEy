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

        window.Refresh();
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

    // A radar window over the port and ground, shown at its size, with its frame drawn once.
    private static AvaloniaPanel Open(FakeRadarPort port, out RetailRadarWindow window, GroundTiles? ground = null, RadarOptions? options = null,
        Action<RadarOptions>? saved = null, Action? startMove = null)
    {
        RetailRadarWindow? created = null;
        var panel = AvaloniaPanel.Create(() => created = new RetailRadarWindow(port, () => ground, options ?? new RadarOptions(), saved ?? (_ => { }),
            () => 0, startMove ?? (() => { })), RetailRadarWindow.Width, RetailRadarWindow.Height);
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
        return new Point(RetailRadarWindow.ScopeOrigin.X + dot.At.X, RetailRadarWindow.ScopeOrigin.Y + dot.At.Y);
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
        var centre = new Point(RetailRadarWindow.ScopeOrigin.X + 59, RetailRadarWindow.ScopeOrigin.Y + 59);
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
