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
    [Fact]
    public void While_the_UI_is_locked_no_window_moves_or_resizes_and_none_shows_resize_edges()
    {
        var locked = true;
        var windows = new WindowManager(new Size(1000, 800), new MemoryWindowPositionStore(), "Thistledown", "Alice", uiLocked: () => locked);
        var window = windows.Open(new WindowDefinition("chat", "Chat", 300, 200, resizing: new WindowResizing(new Size(100, 100))), new Point(100, 100));
        var title = new Point(200, 110);
        var corner = new Point(window.Bounds.Right - 2, window.Bounds.Bottom - 2);

        Assert.True(windows.Press(title));
        windows.Move(new Point(400, 300));
        windows.Release();
        Assert.True(windows.Press(corner));
        windows.Move(new Point(corner.X + 100, corner.Y + 100));
        windows.Release();
        Assert.False(windows.BeginMove("chat", title));
        Assert.Equal(WindowEdges.None, windows.HoverAt(corner).Edges);
        Assert.Null(windows.MoveEdgeAt(new Point(200, 101)));
        Assert.Equal(new Rectangle(100, 100, 300, 200), window.Bounds);

        locked = false;
        Assert.NotEqual(WindowEdges.None, windows.HoverAt(corner).Edges);
        windows.Press(title);
        windows.Move(new Point(250, 110));
        windows.Release();
        Assert.Equal(new Point(150, 100), window.Location);
    }

    // The Vault's sizing: 294 is the narrowest grid-aligned width that keeps the header at about 290.
    private static readonly WindowResizing VaultSizing = new(new Size(290, 306));

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
    public void A_scaled_manager_shows_windows_smaller_and_saves_their_size_at_design_size()
    {
        var store = new MemoryWindowPositionStore();
        var manager = new WindowManager(new Size(800, 600), store, "server", "character", scale: 0.5);
        var resizing = new WindowResizing(new Size(100, 60));
        var window = manager.Open(new WindowDefinition("one", "One", 200, 120, titleBarHeight: 20, resizing: resizing), new Point(10, 10));

        Assert.Equal(new Size(100, 60), window.Size);
        // The title bar is 10 pixels tall now: a press 8 pixels down moves the window, one 15 pixels down is in the content.
        manager.Press(new Point(60, 25));
        Assert.Null(manager.Moving);
        manager.Release();
        manager.Press(new Point(60, 18));
        Assert.Same(window, manager.Moving);
        manager.Release();
        // The minimum shrinks too: the bottom-right corner can pull the window down to 50×30.
        Assert.True(manager.Press(new Point(109, 69)));
        manager.Move(new Point(0, 0));
        manager.Release();
        Assert.Equal(new Size(50, 30), window.Size);
        manager.Close("one");

        Assert.Equal(new Size(100, 60), store.Get("server", "character", "one")!.Value.Size);
        Assert.Equal(new Size(50, 30), manager.Open(new WindowDefinition("one", "One", 200, 120, titleBarHeight: 20, resizing: resizing), new Point(10, 10)).Size);
    }

    [Fact]
    public void A_window_rescales_on_its_own_on_top_of_the_manager_scale_and_keeps_its_size_for_the_character()
    {
        var store = new MemoryWindowPositionStore();
        var manager = new WindowManager(new Size(800, 600), store, "server", "character", scale: 0.5);
        var resizing = new WindowResizing(new Size(100, 60));
        WindowDefinition One() => new("one", "One", 200, 120, titleBarHeight: 20, resizing: resizing);
        var window = manager.Open(One(), new Point(10, 10));
        manager.Open(new WindowDefinition("two", "Two", 200, 120), new Point(300, 10));

        Assert.True(manager.Rescale("one", 5));
        Assert.Equal(150, window.SizePercent);
        Assert.Equal(0.75, window.Scale);
        Assert.Equal(new Size(150, 90), window.Size);
        Assert.Equal(new Point(10, 10), window.Location);
        Assert.Equal(0.5, manager.ScaleOf("two"));
        // The design size is still what is saved, and the window opens again at its own scale.
        manager.Close("one");
        Assert.Equal(new Size(200, 120), store.Get("server", "character", "one")!.Value.Size);
        Assert.Equal(0.75, manager.ScaleOf("one"));
        Assert.Equal(new Size(150, 90), manager.Open(One(), new Point(10, 10)).Size);
        // It stops at the largest size.
        Assert.True(manager.Rescale("one", 99));
        Assert.Equal(WindowManager.LargestSize, manager.Get("one")!.SizePercent);
        Assert.False(manager.Rescale("one", 1));
    }

    [Fact]
    public void A_locked_ui_does_not_rescale_windows()
    {
        var manager = new WindowManager(new Size(800, 600), new MemoryWindowPositionStore(), "server", "character", uiLocked: () => true);
        manager.Open(new WindowDefinition("one", "One", 200, 120), new Point(10, 10));

        Assert.False(manager.Rescale("one", 1));
        Assert.Equal(1, manager.ScaleOf("one"));
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
    public void A_moved_window_snaps_to_nearby_window_and_screen_edges()
    {
        var manager = NewManager();
        manager.Open(new WindowDefinition("one", "One", 120, 80), new Point(100, 100));
        manager.Open(new WindowDefinition("two", "Two", 120, 80), new Point(400, 300));
        var one = manager.Get("one")!;
        Assert.True(manager.Press(new Point(110, 110)));

        // Five pixels short of two's left edge and five below its top: it snaps against two, tops lined up.
        manager.Move(new Point(285, 315));
        Assert.Equal(new Point(280, 300), one.Location);
        manager.Move(new Point(14, 525));
        Assert.Equal(new Point(0, 520), one.Location);
        manager.Move(new Point(210, 210));
        Assert.Equal(new Point(200, 200), one.Location);
    }

    [Fact]
    public void Windows_and_a_toolbar_drawn_outside_the_manager_snap_to_each_other()
    {
        var toolbar = new Rectangle(300, 500, 200, 40);
        var manager = new WindowManager(new Size(800, 600), new MemoryWindowPositionStore(), "server", "character", snapTargets: () => new[] { toolbar });
        var window = manager.Open(new WindowDefinition("one", "One", 120, 80), new Point(100, 100));
        Assert.True(manager.Press(new Point(110, 110)));

        // Six pixels above the toolbar and four right of its left edge: it sits on the toolbar, lefts lined up.
        manager.Move(new Point(314, 424));
        Assert.Equal(new Point(300, 420), window.Location);
        manager.Release();

        // The toolbar, which the manager does not move, snaps to the window: dropped five right of its edge, it sits against it.
        Assert.Equal(new Point(420, 430), manager.Snap(new Point(425, 430), toolbar.Size));
    }

    [Fact]
    public void A_window_without_a_title_bar_moves_only_when_its_content_begins_the_move()
    {
        var manager = NewManager(300, 200);
        manager.Open(new WindowDefinition("bar", "Bar", 120, 48, titleBarHeight: 48, dragsFromContent: true), new Point(10, 10));

        // A press on the window is a click for its content: it does not start a move.
        Assert.True(manager.Press(new Point(25, 25)));
        manager.Move(new Point(200, 150));
        Assert.Equal(new Point(10, 10), manager.Get("bar")!.Location);

        Assert.True(manager.BeginMove("bar", new Point(25, 25)));
        manager.Move(new Point(125, 65));
        manager.Release();
        Assert.Equal(new Point(110, 50), manager.Get("bar")!.Location);
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
    public void Dragging_the_right_edge_resizes_the_window_by_the_drag()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(100, 100));

        Assert.True(manager.Press(new Point(443, 300)));
        manager.Move(new Point(473, 300));
        Assert.Same(manager.Get("vault"), manager.Resizing);
        manager.Release();
        Assert.Null(manager.Resizing);

        var window = manager.Get("vault")!;
        Assert.Equal(new Size(374, 606), window.Size);
        Assert.Equal(new Point(100, 100), window.Location);
    }

    [Fact]
    public void A_maximum_holds_when_the_left_edge_is_dragged_and_the_right_edge_stays_put()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(new WindowDefinition("vitals", "Vitals", 220, 64, resizing: new WindowResizing(new Size(150, 64), new Size(460, 64))),
            new Point(500, 100));

        Assert.True(manager.Press(new Point(503, 130)));
        manager.Move(new Point(-97, 130));
        manager.Release();

        var window = manager.Get("vitals")!;
        Assert.Equal(new Size(460, 64), window.Size);
        Assert.Equal(new Point(260, 100), window.Location);
        Assert.Equal(720, window.Bounds.Right);
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
        Assert.Equal(new Size(384, 676), window.Size);
        Assert.Equal(new Point(460, 230), window.Location);
    }

    [Fact]
    public void A_resize_stops_at_the_minimum_size_on_both_axes()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(100, 100));

        Assert.True(manager.Press(new Point(443, 703)));
        manager.Move(new Point(-57, 203));
        manager.Release();

        Assert.Equal(new Size(290, 306), manager.Get("vault")!.Size);
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
        // 420 px are left to the screen's edge.
        Assert.Equal(new Size(420, 606), window.Size);
        Assert.True(window.Bounds.Right <= 1920);
    }

    [Fact]
    public void A_title_drag_still_moves_a_resizable_window_and_keeps_its_size()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(100, 100));

        Assert.True(manager.Press(new Point(200, 110)));
        manager.Move(new Point(260, 150));
        Assert.Same(manager.Get("vault"), manager.Moving);
        Assert.Null(manager.Resizing);
        manager.Release();
        Assert.Null(manager.Moving);

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
    [InlineData(200, 101, WindowEdges.None)]
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
    public void A_press_on_the_top_edge_moves_any_window_and_a_top_corner_still_resizes()
    {
        var manager = NewManager(1920, 1080);
        manager.Open(Resizable("vault"), new Point(100, 100));
        manager.Open(new WindowDefinition("bar", "Bar", 120, 40, dragsFromContent: true), new Point(600, 100));

        Assert.Equal("vault", manager.MoveEdgeAt(new Point(200, 101)));
        Assert.Null(manager.MoveEdgeAt(new Point(101, 101)));
        Assert.Equal("bar", manager.MoveEdgeAt(new Point(650, 102)));
        Assert.Null(manager.MoveEdgeAt(new Point(650, 120)));

        manager.Press(new Point(650, 102));
        manager.Move(new Point(700, 202));
        manager.Release();
        Assert.Equal(new Point(650, 200), manager.Get("bar")!.Location);
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

            Assert.Equal(new Size(374, 606), window.Size);
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

    [Fact]
    public void Forgetting_places_drops_one_characters_window_rows_and_keeps_its_options_and_every_other_row()
    {
        var path = TempPath();
        try
        {
            var store = new FileWindowPositionStore(path);
            store.Save("server-a", "character-a", "vault", (new Point(10, 20), new Size(300, 400)));
            store.Save("server-a", "character-a", "dereth-chat-text-size", (new Point(140, 0), null));
            store.Save("server-a", "character-a", "dereth-chat-fade", (new Point(30, 0), null));
            store.Save("server-a", "character-b", "vault", (new Point(30, 40), null));
            store.Save("server-b", "character-a", "vault", (new Point(50, 60), null));

            Assert.True(new FileWindowPositionStore(path).ForgetPlaces("server-a", "character-a", new[] { "dereth-chat-text-size", "dereth-chat-fade" }));

            var reloaded = new FileWindowPositionStore(path);
            Assert.Null(reloaded.Load("server-a", "character-a", "vault"));
            Assert.Equal(140, reloaded.Load("server-a", "character-a", "dereth-chat-text-size")!.Value.Location.X);
            Assert.Equal(30, reloaded.Load("server-a", "character-a", "dereth-chat-fade")!.Value.Location.X);
            Assert.Equal(new Point(30, 40), reloaded.Load("server-a", "character-b", "vault")!.Value.Location);
            Assert.Equal(new Point(50, 60), reloaded.Load("server-b", "character-a", "vault")!.Value.Location);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void A_place_that_cannot_be_written_still_loads_this_session()
    {
        var path = TempPath();
        try
        {
            // A directory where the file should be: no write can succeed, and nothing is read.
            Directory.CreateDirectory(path);
            var store = new FileWindowPositionStore(path);
            store.Save("server-a", "character-a", "vault", (new Point(10, 20), new Size(300, 400)));

            Assert.Equal((new Point(10, 20), new Size(300, 400)), store.Load("server-a", "character-a", "vault"));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path);
        }
    }

    [Fact]
    public void A_failed_forget_hides_the_forgotten_places_this_session_and_keeps_the_options()
    {
        var path = TempPath();
        try
        {
            // A directory where the file should be: nothing can be written, so the rows live in the session.
            Directory.CreateDirectory(path);
            var store = new FileWindowPositionStore(path);
            store.Save("server-a", "character-a", "vault", (new Point(10, 20), null));
            store.Save("server-a", "character-a", "dereth-chat-fade", (new Point(30, 0), null));
            store.Save("server-a", "character-b", "vault", (new Point(30, 40), null));

            Assert.False(store.ForgetPlaces("server-a", "character-a", new[] { "dereth-chat-fade" }));
            Assert.Null(store.Load("server-a", "character-a", "vault"));
            Assert.Equal(new Point(30, 0), store.Load("server-a", "character-a", "dereth-chat-fade")!.Value.Location);
            Assert.Equal(new Point(30, 40), store.Load("server-a", "character-b", "vault")!.Value.Location);

            // Writable again: the next save persists the session's rows and the forget, and clears the overlay.
            Directory.Delete(path);
            store.Save("server-a", "character-c", "vault", (new Point(1, 1), null));
            var reloaded = new FileWindowPositionStore(path);
            Assert.Null(reloaded.Load("server-a", "character-a", "vault"));
            Assert.Equal(new Point(30, 0), reloaded.Load("server-a", "character-a", "dereth-chat-fade")!.Value.Location);
            Assert.Equal(new Point(30, 40), reloaded.Load("server-a", "character-b", "vault")!.Value.Location);
            // Another client's change to the file now shows through the store, since nothing is held back.
            new FileWindowPositionStore(path).Save("server-a", "character-b", "vault", (new Point(99, 99), null));
            Assert.Equal(new Point(99, 99), store.Load("server-a", "character-b", "vault")!.Value.Location);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path);
        }
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_not_rewritten_by_a_forget_or_a_save()
    {
        var path = TempPath();
        try
        {
            new FileWindowPositionStore(path).Save("server-a", "character-a", "vault", (new Point(10, 20), null));
            var before = File.ReadAllBytes(path);

            // Another process holding the file open exclusively makes reading it fail; the rows must survive that.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var store = new FileWindowPositionStore(path);
                Assert.False(store.ForgetPlaces("server-a", "character-a", Array.Empty<string>()));
                store.Save("server-a", "character-b", "vault", (new Point(1, 1), null));
            }

            Assert.Equal(before, File.ReadAllBytes(path));
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

    [Fact]
    public void A_window_asking_to_open_centred_is_centred_on_the_screen_when_it_has_no_saved_place()
    {
        var windows = NewManager(1920, 1080);

        var window = windows.Open(new WindowDefinition("market", "Market", 980, 640), WindowManager.CentredLocation);

        Assert.Equal(new Rectangle(470, 220, 980, 640), window.Bounds);
    }

    private static WindowManager NewManager(int width = 800, int height = 600) =>
        new(new Size(width, height), new MemoryWindowPositionStore(), "server", "character");
}
