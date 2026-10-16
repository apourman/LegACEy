using System;
using System.Drawing;
using LegACEy.Client.Demo;

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
}
