using System;
using System.Drawing;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class WindowManagerTests
{
    [Fact]
    public void Pressing_a_window_brings_it_to_the_front_and_hit_testing_uses_front_to_back_order()
    {
        var manager = NewManager();
        manager.Open(new WindowDefinition("one", "One", 120, 80), new Point(10, 10));
        manager.Open(new WindowDefinition("two", "Two", 120, 80), new Point(40, 30));

        Assert.Equal(new[] { "two", "one" }, manager.ZOrder.Select(window => window.Id));
        Assert.Equal("two", manager.HitTest(new Point(50, 40))!.Id);

        manager.Press(new Point(20, 20));

        Assert.Equal(new[] { "one", "two" }, manager.ZOrder.Select(window => window.Id));
        Assert.Equal("one", manager.HitTest(new Point(50, 40))!.Id);
    }

    [Fact]
    public void Dragging_the_title_bar_moves_the_window_and_clamps_it_to_the_screen()
    {
        var manager = NewManager(300, 200);
        manager.Open(new WindowDefinition("one", "One", 120, 80), new Point(10, 10));

        Assert.True(manager.Press(new Point(25, 25)));
        manager.Move(new Point(280, 190));
        manager.Release();

        Assert.Equal(new Point(180, 120), manager.Get("one")!.Location);
        Assert.False(manager.Press(new Point(50, 100)));
        Assert.Equal(new Point(180, 120), manager.Get("one")!.Location);
    }

    [Fact]
    public void Positions_are_restored_only_for_the_same_server_character_and_window()
    {
        var path = Path.Combine(Path.GetTempPath(), "legacey-window-tests-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var store = new FileWindowPositionStore(path);
            var first = new WindowManager(new Size(800, 600), store, "server-a", "character-a");
            first.Open(new WindowDefinition("one", "One", 120, 80), new Point(10, 10));
            Assert.True(first.Press(new Point(20, 20)));
            first.Move(new Point(300, 200));
            first.Release();

            var same = new WindowManager(new Size(800, 600), store, "server-a", "character-a");
            same.Open(new WindowDefinition("one", "One", 120, 80), new Point(0, 0));
            Assert.Equal(new Point(290, 190), same.Get("one")!.Location);

            var otherCharacter = new WindowManager(new Size(800, 600), store, "server-a", "character-b");
            otherCharacter.Open(new WindowDefinition("one", "One", 120, 80), new Point(0, 0));
            Assert.Equal(new Point(0, 0), otherCharacter.Get("one")!.Location);

            var otherWindow = new WindowManager(new Size(800, 600), store, "server-a", "character-a");
            otherWindow.Open(new WindowDefinition("two", "Two", 120, 80), new Point(0, 0));
            Assert.Equal(new Point(0, 0), otherWindow.Get("two")!.Location);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Closing_a_window_removes_it_and_saves_its_last_position()
    {
        var store = new MemoryWindowPositionStore();
        var manager = new WindowManager(new Size(300, 200), store, "server", "character");
        manager.Open(new WindowDefinition("one", "One", 120, 80), new Point(10, 10));
        Assert.True(manager.Press(new Point(20, 20)));
        manager.Move(new Point(100, 100));
        manager.Release();

        Assert.True(manager.Close("one"));
        Assert.Empty(manager.ZOrder);
        Assert.Equal(new Point(90, 90), store.Get("server", "character", "one"));
    }

    private static WindowManager NewManager(int width = 800, int height = 600) =>
        new(new Size(width, height), new MemoryWindowPositionStore(), "server", "character");
}
