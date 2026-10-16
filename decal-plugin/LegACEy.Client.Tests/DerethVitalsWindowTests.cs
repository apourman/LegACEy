using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.LogicalTree;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class DerethVitalsWindowTests
{
    private const string Id = "dereth-vitals";
    private const string Server = "Thistledown";
    private static readonly Size Screen = new(1000, 800);

    private static WindowManager Windows(IWindowPositionStore store, string character) => new(Screen, store, Server, character);

    private static GameStatePort Game(int health = 86) =>
        new(new GameStateSnapshot("Alice", Server, health, 100, 64, 100, 37, 100));

    /// <summary>A player's drag of the window's right edge: press just inside it, move, let go.</summary>
    private static void StretchBy(WindowManager windows, ManagedWindow window, int dx)
    {
        var grab = new Point(window.Bounds.Right - 3, window.Bounds.Top + window.Height / 2);
        windows.Press(grab);
        windows.Move(new Point(grab.X + dx, grab.Y));
        windows.Release();
    }

    [Fact]
    public void The_window_opens_at_220_by_64_and_stretches_only_wider_from_150_to_460() => RenderThread.Run(() =>
    {
        var windows = Windows(new MemoryWindowPositionStore(), "Alice");
        var window = windows.Open(DerethVitalsWindow.Definition(Id, () => false), new Point(100, 100));
        Assert.Equal(new Size(220, 64), window.Size);

        StretchBy(windows, window, 400);
        Assert.Equal(new Size(460, 64), window.Size);

        StretchBy(windows, window, -600);
        Assert.Equal(new Size(150, 64), window.Size);
    });

    [Fact]
    public void Pressing_the_bottom_edge_and_dragging_leaves_the_fixed_height_at_64() => RenderThread.Run(() =>
    {
        var windows = Windows(new MemoryWindowPositionStore(), "Alice");
        var window = windows.Open(DerethVitalsWindow.Definition(Id, () => false), new Point(100, 100));
        var placed = window.Location;

        windows.Press(new Point(window.Bounds.Left + 110, window.Bounds.Bottom - 2));
        windows.Move(new Point(window.Bounds.Left + 110, window.Bounds.Bottom + 60));
        windows.Release();

        Assert.Equal(64, window.Height);
        Assert.Equal(placed, window.Location);
    });

    [Fact]
    public void While_the_UI_is_locked_the_grip_is_hidden_and_the_window_does_not_stretch() => RenderThread.Run(() =>
    {
        var locked = true;
        var windows = Windows(new MemoryWindowPositionStore(), "Alice");
        var window = windows.Open(DerethVitalsWindow.Definition(Id, () => locked), new Point(100, 100));
        var view = new DerethVitalsWindow(Game(), () => locked, () => { });

        view.Refresh(TimeSpan.Zero);
        StretchBy(windows, window, 300);

        Assert.False(view.GripVisible);
        Assert.Equal(220, window.Width);

        locked = false;
        view.Refresh(TimeSpan.Zero);
        StretchBy(windows, window, 300);

        Assert.True(view.GripVisible);
        Assert.Equal(460, window.Width);
    });

    [Fact]
    public void The_stretched_size_and_place_come_back_for_the_same_character_and_no_other() => RenderThread.Run(() =>
    {
        var store = new MemoryWindowPositionStore();
        var before = Windows(store, "Alice");
        var window = before.Open(DerethVitalsWindow.Definition(Id, () => false), new Point(100, 100));
        StretchBy(before, window, 80);
        // A drag of the top band moves the window with no title bar.
        before.Press(new Point(window.Location.X + 100, window.Location.Y + 2));
        before.Move(new Point(window.Location.X + 150, window.Location.Y + 60));
        before.Release();
        var placed = window.Location;

        var again = Windows(store, "Alice").Open(DerethVitalsWindow.Definition(Id, () => false), new Point(0, 0));
        Assert.Equal(placed, again.Location);
        Assert.Equal(300, again.Width);
        Assert.Equal(64, again.Height);

        var other = Windows(store, "Bob").Open(DerethVitalsWindow.Definition(Id, () => false), new Point(0, 0));
        Assert.Equal(new Size(220, 64), other.Size);
    });

    [Fact]
    public void Hovering_a_bar_names_the_vital_and_gives_its_value() => RenderThread.Run(() =>
    {
        var view = new DerethVitalsWindow(Game(), () => false, () => { });

        view.Refresh(TimeSpan.Zero);

        Assert.Equal("Health 86 / 100", view.TooltipOf(Vital.Health));
        Assert.Equal("Mana 37 / 100", view.TooltipOf(Vital.Mana));
    });

    [Fact]
    public void The_bars_follow_the_published_values_a_drop_leaves_a_trail_that_drains_and_low_health_pulses() => RenderThread.Run(() =>
    {
        var game = new GameStatePort(new GameStateSnapshot("Alice", Server, 300, 412, 250, 380, 150, 298));
        var view = new DerethVitalsWindow(game, () => false, () => { });
        view.Refresh(TimeSpan.Zero);
        var bars = Bars(view);
        Assert.Equal(new[] { "300 / 412", "250 / 380", "150 / 298" }, Labels(view));
        Assert.Equal(300.0 / 412, bars[0].Fraction, 3);
        Assert.Equal(250.0 / 380, bars[1].Fraction, 3);
        Assert.DoesNotContain(bars, bar => bar.Pulsing);

        // Health drops to 80 of 412: the lost part stays pale for a moment, and health is under a quarter, so it pulses.
        game.Publish(new GameStateSnapshot("Alice", Server, 80, 412, 250, 380, 150, 298));
        view.Refresh(TimeSpan.FromSeconds(1));
        Assert.Equal("80 / 412", Labels(view)[0]);
        Assert.Equal(80.0 / 412, bars[0].Fraction, 3);
        Assert.Equal(300.0 / 412, bars[0].TrailFraction, 3);
        Assert.True(bars[0].Pulsing);
        Assert.False(bars[1].Pulsing);
        Assert.False(bars[2].Pulsing);

        // The trail has held and drained down to the value by four seconds.
        view.Refresh(TimeSpan.FromSeconds(4));
        Assert.Equal(80.0 / 412, bars[0].TrailFraction, 3);
    });

    [Fact]
    public void The_vitals_window_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_VITALS_RENDER names the folder the PNGs are written to.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_VITALS_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var full = new GameStateSnapshot("Alice", Server, 300, 412, 250, 380, 150, 298);
        var low = new GameStateSnapshot("Alice", Server, 80, 412, 250, 380, 298, 298);
        foreach (var width in new[] { 220, 300 })
            foreach (var scale in new[] { 1.0, 2.0 })
            {
                var name = $"{width}-{scale:0}x.png";
                // The low state is health dropping from full at one second, so its trail shows.
                Render(Path.Combine(folder, "full-" + name), width, scale, (full, 0));
                Render(Path.Combine(folder, "low-" + name), width, scale, (full, 0), (low, 1));
            }
    });

    /// <summary>Lays the window out in an in-game panel at <paramref name="width"/> by its height, at <paramref name="scale"/>, and saves its frame.</summary>
    private static void Render(string path, int width, double scale, params (GameStateSnapshot State, double Seconds)[] frames)
    {
        var game = new GameStatePort(frames[0].State);
        var view = new DerethVitalsWindow(game, () => false, () => { });
        using var panel = AvaloniaPanel.Create(() => view, (int)Math.Round(width * scale), (int)Math.Round(DerethVitalsWindow.DesignHeight * scale), scale);
        foreach (var (state, seconds) in frames)
        {
            game.Publish(state);
            view.Refresh(TimeSpan.FromSeconds(seconds));
        }
        panel.Tick();
        var frame = panel.Frame;
        using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(frame.Width, frame.Height), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var locked = bitmap.Lock())
        {
            Assert.Equal(frame.Stride, locked.RowBytes);
            Marshal.Copy(frame.Pixels, 0, locked.Address, frame.Pixels.Length);
        }
        bitmap.Save(path);
    }

    private static List<DerethVitalBar> Bars(DerethVitalsWindow view) => view.GetLogicalDescendants().OfType<DerethVitalBar>().ToList();

    /// <summary>The value labels, in retail's order.</summary>
    private static List<string> Labels(DerethVitalsWindow view) =>
        view.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Where(text => text.Text?.Contains(" / ") == true).Select(text => text.Text!).ToList();
}
