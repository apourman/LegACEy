using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class WindowManagerTests
{
    // The Vault's sizing: 294 is the narrowest grid-aligned width that keeps the header at about 290.
    private static readonly WindowResizing VaultSizing = new(new Size(290, 306), new Size(50, 50), new Size(44, 6));

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
        Assert.Equal(new Point(90, 90), store.Get("server", "character", "one")!.Value.Location);
    }

    [Fact]
    public void Resize_round_trip_restores_preferred_position_even_after_closing_while_small()
    {
        var store = new MemoryWindowPositionStore();
        var manager = new WindowManager(new Size(1920, 1080), store, "server", "character");
        var original = new Point(1500, 900);
        var definition = new WindowDefinition("one", "One", 300, 150);
        manager.Open(definition, original);
        manager.ResizeScreen(new Size(800, 600));
        Assert.Equal(new Point(500, 450), manager.Get("one")!.Location);
        manager.Close("one");
        Assert.Equal(original, store.Get("server", "character", "one")!.Value.Location);
        manager.Open(definition, Point.Empty);
        manager.ResizeScreen(new Size(1920, 1080));
        Assert.Equal(original, manager.Get("one")!.Location);
    }

    [Fact]
    public void Drag_while_small_replaces_the_preferred_position()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(new WindowDefinition("one", "One", 300, 150), new Point(1500, 900));
        manager.ResizeScreen(new Size(800, 600));
        Assert.True(manager.Press(new Point(510, 460)));
        manager.Move(new Point(110, 110));
        manager.Release();
        manager.ResizeScreen(new Size(1920, 1080));
        Assert.Equal(new Point(100, 100), manager.Get("one")!.Location);
    }

    [Fact]
    public void Dragging_the_right_edge_resizes_the_window_to_whole_cells()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(100, 100));

        Assert.True(manager.Press(new Point(443, 300)));
        manager.Move(new Point(473, 300));
        manager.Release();

        var window = manager.Get("vault")!;
        // 344 + 30 = 374 snaps to 44 + 7 × 50.
        Assert.Equal(new Size(394, 606), window.Size);
        Assert.Equal(new Point(100, 100), window.Location);
    }

    [Fact]
    public void Dragging_the_top_left_corner_moves_the_left_and_top_edges_and_keeps_the_opposite_corner()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(500, 300));

        // The corner is also inside the title bar; the resize wins.
        Assert.True(manager.Press(new Point(502, 302)));
        manager.Move(new Point(462, 232));
        manager.Release();

        var window = manager.Get("vault")!;
        Assert.Equal(new Size(394, 656), window.Size);
        Assert.Equal(new Point(450, 250), window.Location);
    }

    [Fact]
    public void A_resize_stops_at_the_minimum_size_on_both_axes()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(100, 100));

        Assert.True(manager.Press(new Point(443, 703)));
        manager.Move(new Point(-57, 203));
        manager.Release();

        Assert.Equal(new Size(294, 306), manager.Get("vault")!.Size);
    }

    [Fact]
    public void A_resize_stops_at_the_screen_edge()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(1500, 100));

        Assert.True(manager.Press(new Point(1843, 300)));
        manager.Move(new Point(2143, 300));
        manager.Release();

        var window = manager.Get("vault")!;
        // 420 px are left to the screen's edge, so the largest whole-cell width is 394.
        Assert.Equal(new Size(394, 606), window.Size);
        Assert.True(window.Bounds.Right <= 1920);
    }

    [Fact]
    public void A_title_drag_still_moves_a_resizable_window_and_keeps_its_size()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(100, 100));

        Assert.True(manager.Press(new Point(200, 110)));
        manager.Move(new Point(260, 150));
        manager.Release();

        var window = manager.Get("vault")!;
        Assert.Equal(new Point(160, 140), window.Location);
        Assert.Equal(new Size(344, 606), window.Size);
    }

    [Fact]
    public void A_press_low_in_a_taller_title_bar_moves_the_window()
    {
        var manager = NewManager(1920, 1080);
        // The Dereth header is 60 px from the window's top, not the 28 px default.
        manager.Open(new WindowDefinition("vault", "Vault", 344, 606, titleBarHeight: 60, resizing: VaultSizing), new Point(100, 100));

        Assert.True(manager.Press(new Point(200, 145)));
        manager.Move(new Point(260, 185));
        manager.Release();

        var window = manager.Get("vault")!;
        Assert.Equal(new Point(160, 140), window.Location);
        Assert.Equal(new Size(344, 606), window.Size);
    }

    [Fact]
    public void A_window_without_resizing_does_not_resize_from_its_edge()
    {
        var manager = NewManager(800, 600);
        manager.Open(new WindowDefinition("one", "One", 120, 80), new Point(100, 100));

        Assert.True(manager.Press(new Point(218, 140)));
        manager.Move(new Point(260, 140));
        manager.Release();

        var window = manager.Get("one")!;
        Assert.Equal(new Size(120, 80), window.Size);
        Assert.Equal(new Point(100, 100), window.Location);
    }

    [Theory]
    [InlineData(443, 300, WindowEdges.Right)]
    [InlineData(101, 300, WindowEdges.Left)]
    [InlineData(200, 703, WindowEdges.Bottom)]
    [InlineData(200, 101, WindowEdges.Top)]
    [InlineData(101, 101, WindowEdges.Left | WindowEdges.Top)]
    [InlineData(443, 703, WindowEdges.Right | WindowEdges.Bottom)]
    [InlineData(443, 101, WindowEdges.Right | WindowEdges.Top)]
    [InlineData(101, 703, WindowEdges.Left | WindowEdges.Bottom)]
    [InlineData(112, 112, WindowEdges.Left | WindowEdges.Top)]
    [InlineData(112, 300, WindowEdges.None)]
    [InlineData(200, 110, WindowEdges.None)]
    [InlineData(200, 400, WindowEdges.None)]
    public void The_hover_edges_are_the_edges_and_corners_under_the_pointer_and_none_over_the_title_and_body(int x, int y, WindowEdges expected)
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(100, 100));

        var hover = manager.HoverAt(new Point(x, y));

        Assert.Equal("vault", hover.WindowId);
        Assert.Equal(expected, hover.Edges);
    }

    [Fact]
    public void A_resized_size_is_saved_with_the_position_and_restored_on_reopen()
    {
        var path = TempPath();
        try
        {
            var store = new FileWindowPositionStore(path);
            var first = new WindowManager(new Size(1920, 1080), store, "server-a", "character-a");
            first.Open(Resizable("vault"), new Point(100, 100));
            Assert.True(first.Press(new Point(443, 300)));
            first.Move(new Point(473, 300));
            first.Release();

            var reopened = new WindowManager(new Size(1920, 1080), new FileWindowPositionStore(path), "server-a", "character-a");
            var window = reopened.Open(Resizable("vault"), new Point(0, 0));

            Assert.Equal(new Size(394, 606), window.Size);
            Assert.Equal(new Point(100, 100), window.Location);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void A_position_only_row_from_before_sizes_were_saved_opens_at_the_default_size()
    {
        var path = TempPath();
        try
        {
            File.WriteAllLines(path, new[] { Row("server-a", "character-a", "vault", 300, 200) });
            var manager = new WindowManager(new Size(1920, 1080), new FileWindowPositionStore(path), "server-a", "character-a");

            var window = manager.Open(Resizable("vault"), new Point(0, 0));

            Assert.Equal(new Point(300, 200), window.Location);
            Assert.Equal(new Size(344, 606), window.Size);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void A_row_with_a_damaged_size_keeps_its_position_and_opens_at_the_default_size()
    {
        var path = TempPath();
        try
        {
            File.WriteAllLines(path, new[] { Row("server-a", "character-a", "vault", 300, 200) + "|wide|9" });
            var manager = new WindowManager(new Size(1920, 1080), new FileWindowPositionStore(path), "server-a", "character-a");

            var window = manager.Open(Resizable("vault"), new Point(0, 0));

            Assert.Equal(new Point(300, 200), window.Location);
            Assert.Equal(new Size(344, 606), window.Size);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static WindowDefinition Resizable(string id) => new(id, "Vault", 344, 606, resizing: VaultSizing);

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "legacey-window-tests-" + Guid.NewGuid().ToString("N") + ".txt");

    /// <summary>A row as the position file wrote it before window sizes: base64 names, then x and y.</summary>
    private static string Row(string server, string character, string window, int x, int y) =>
        string.Join("|", new[] { server, character, window }.Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)))) + "|" + x + "|" + y;

    private static WindowManager NewManager(int width = 800, int height = 600) =>
        new(new Size(width, height), new MemoryWindowPositionStore(), "server", "character");
}
