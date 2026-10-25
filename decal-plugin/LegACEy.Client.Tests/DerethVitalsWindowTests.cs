using System;
using System.Drawing;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class DerethVitalsWindowTests
{
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

    /// <summary>A player's drag of the stretch grip in the bottom right corner, by <paramref name="dx"/> and <paramref name="dy"/>.</summary>
    private static void Grip(WindowManager windows, ManagedWindow window, int dx, int dy)
    {
        var grab = new Point(window.Bounds.Right - 3, window.Bounds.Bottom - 3);
        windows.Press(grab);
        windows.Move(new Point(grab.X + dx, grab.Y + dy));
        windows.Release();
    }

    [Fact]
    public void The_window_opens_at_252_by_84_and_stretches_wider_up_to_480_then_draws_larger() => RenderThread.Run(() =>
    {
        var windows = Windows(new MemoryWindowPositionStore(), "Alice");
        var window = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.Stacked, () => false), new Point(100, 100));
        Assert.Equal(new Size(252, 84), window.Size);

        StretchBy(windows, window, 228);
        Assert.Equal(new Size(480, 84), window.Size);

        // Past 480 at this scale the height follows, so the window draws larger instead of its bars growing longer.
        StretchBy(windows, window, 80);
        Assert.Equal(new Size(560, 98), window.Size);

        StretchBy(windows, window, -1000);
        Assert.Equal(new Size(135, 63), window.Size);
    });

    [Fact]
    public void The_grip_and_the_bottom_edge_draw_the_window_larger_up_to_twice_its_size() => RenderThread.Run(() =>
    {
        var windows = Windows(new MemoryWindowPositionStore(), "Alice");
        var window = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.Stacked, () => false), new Point(100, 100));
        var placed = window.Location;

        Grip(windows, window, 252, 84);
        Assert.Equal(new Size(504, 168), window.Size);
        Assert.Equal(placed, window.Location);

        Grip(windows, window, -252, -84);
        // A taller window needs a wider one to keep its bars 180 DIPs long at its scale: the bottom edge carries the width along.
        windows.Press(new Point(window.Bounds.Left + 110, window.Bounds.Bottom - 2));
        windows.Move(new Point(window.Bounds.Left + 110, window.Bounds.Bottom + 40));
        windows.Release();
        Assert.Equal(new Size(270, 126), window.Size);
        Assert.Equal(placed, window.Location);
    });

    [Fact]
    public void The_content_draws_at_the_windows_height_over_84() => RenderThread.Run(() =>
    {
        var view = new DerethVitalsWindow(Game(), () => false, () => { });
        using var panel = AvaloniaPanel.Create(() => view, 504, 168, 1);
        panel.Tick();
        var bar = Bars(view)[0];
        var left = Avalonia.VisualExtensions.TranslatePoint(bar, new Avalonia.Point(0, 0), panel.Content)!.Value;
        var right = Avalonia.VisualExtensions.TranslatePoint(bar, new Avalonia.Point(bar.Bounds.Width, bar.Bounds.Height), panel.Content)!.Value;
        Assert.Equal(DerethVitalBar.BarHeight * 2, right.Y - left.Y, 3);
    });

    [Fact]
    public void While_the_UI_is_locked_the_grip_is_hidden_and_the_window_does_not_stretch() => RenderThread.Run(() =>
    {
        var locked = true;
        var windows = Windows(new MemoryWindowPositionStore(), "Alice");
        var window = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.Stacked, () => locked), new Point(100, 100));
        var view = new DerethVitalsWindow(Game(), () => locked, () => { });

        view.Refresh(TimeSpan.Zero);
        StretchBy(windows, window, 228);

        Assert.False(view.GripVisible);
        Assert.Equal(252, window.Width);

        locked = false;
        view.Refresh(TimeSpan.Zero);
        StretchBy(windows, window, 228);

        Assert.True(view.GripVisible);
        Assert.Equal(480, window.Width);
    });

    [Fact]
    public void The_stretched_size_and_place_come_back_for_the_same_character_and_no_other() => RenderThread.Run(() =>
    {
        var store = new MemoryWindowPositionStore();
        var before = Windows(store, "Alice");
        var window = before.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.Stacked, () => false), new Point(100, 100));
        StretchBy(before, window, 80);
        // A drag of the top band moves the window with no title bar.
        before.Press(new Point(window.Location.X + 100, window.Location.Y + 2));
        before.Move(new Point(window.Location.X + 150, window.Location.Y + 60));
        before.Release();
        var placed = window.Location;

        var again = Windows(store, "Alice").Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.Stacked, () => false), new Point(0, 0));
        Assert.Equal(placed, again.Location);
        Assert.Equal(332, again.Width);
        Assert.Equal(84, again.Height);

        var other = Windows(store, "Bob").Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.Stacked, () => false), new Point(0, 0));
        Assert.Equal(new Size(252, 84), other.Size);
    });

    [Theory]
    [InlineData(VitalsLayout.Stacked, false)]
    [InlineData(VitalsLayout.SideBySide, false)]
    [InlineData(VitalsLayout.Stacked, true)]
    [InlineData(VitalsLayout.SideBySide, true)]
    public void Hovering_a_bar_names_the_vital_and_gives_its_value(VitalsLayout layout, bool retailBars) => RenderThread.Run(() =>
    {
        var view = new DerethVitalsWindow(Game(), () => false, () => { }, layout, retailBars: retailBars);

        view.Refresh(TimeSpan.Zero);

        Assert.Equal("Health 86 / 100", view.TooltipOf(Vital.Health));
        Assert.Equal("Mana 37 / 100", view.TooltipOf(Vital.Mana));
    });

    // Both layouts, with Dereth or retail-style bars, show the same bars the same way: values, a trail behind a drop, and the health pulse.
    [Theory]
    [InlineData(VitalsLayout.Stacked, false)]
    [InlineData(VitalsLayout.SideBySide, false)]
    [InlineData(VitalsLayout.Stacked, true)]
    [InlineData(VitalsLayout.SideBySide, true)]
    public void The_bars_follow_the_published_values_a_drop_leaves_a_trail_that_drains_and_low_health_pulses(VitalsLayout layout, bool retailBars) => RenderThread.Run(() =>
    {
        var game = new GameStatePort(new GameStateSnapshot("Alice", Server, 300, 412, 250, 380, 150, 298));
        var view = new DerethVitalsWindow(game, () => false, () => { }, layout, retailBars: retailBars);
        view.Refresh(TimeSpan.Zero);
        var bars = Bars(view);
        Assert.Equal(new[] { "300/412", "250/380", "150/298" }, Labels(view));
        Assert.Equal(300.0 / 412, bars[0].Fraction, 3);
        Assert.Equal(250.0 / 380, bars[1].Fraction, 3);
        Assert.DoesNotContain(bars, bar => bar.Pulsing);

        // Health drops to 80 of 412: the lost part stays pale for a moment, and health is under a quarter, so it pulses.
        game.Publish(new GameStateSnapshot("Alice", Server, 80, 412, 250, 380, 150, 298));
        view.Refresh(TimeSpan.FromSeconds(1));
        Assert.Equal("80/412", Labels(view)[0]);
        Assert.Equal(80.0 / 412, bars[0].Fraction, 3);
        Assert.Equal(300.0 / 412, bars[0].TrailFraction, 3);
        Assert.True(bars[0].Pulsing);
        Assert.False(bars[1].Pulsing);
        Assert.False(bars[2].Pulsing);
        // The hit runs a light along health once; the bars that did not move stay still.
        Assert.True(bars[0].Shining);
        Assert.False(bars[1].Shining);

        // The trail has held and drained down to the value by four seconds, and the light has stopped.
        view.Refresh(TimeSpan.FromSeconds(4));
        Assert.Equal(80.0 / 412, bars[0].TrailFraction, 3);
        Assert.False(bars[0].Shining);

        // A point of regeneration moves the bar without running the light again.
        game.Publish(new GameStateSnapshot("Alice", Server, 81, 412, 250, 380, 150, 298));
        view.Refresh(TimeSpan.FromSeconds(4.1));
        Assert.False(bars[0].Shining);
    });

    [Fact]
    public void The_options_gear_switches_between_stacked_and_side_by_side_and_the_choice_is_kept_per_character() => RenderThread.Run(() =>
    {
        var store = new MemoryWindowPositionStore();
        VitalsLayout? picked = null;
        using (var stacked = AvaloniaPanel.Create(() => new DerethVitalsWindow(Game(), () => false, () => { }, VitalsLayout.Stacked, layout => picked = layout),
                   DerethVitalsWindow.DesignWidth, DerethVitalsWindow.DesignHeight))
        {
            Press(stacked, Medallion(stacked, "Switch to side by side"));
        }
        Assert.Equal(VitalsLayout.SideBySide, picked);
        new VitalsLayoutChoice(store).Save(Server, "Alice", picked!.Value);

        using (var side = AvaloniaPanel.Create(() => new DerethVitalsWindow(Game(), () => false, () => { }, VitalsLayout.SideBySide, layout => picked = layout),
                   DerethVitalsWindow.SideDesignWidth, DerethVitalsWindow.SideDesignHeight))
        {
            Press(side, Medallion(side, "Switch to stacked"));
        }
        Assert.Equal(VitalsLayout.Stacked, picked);

        // Kept for Alice on this server, and only for her: another character, or the same name on another server, starts stacked.
        var kept = new VitalsLayoutChoice(store);
        Assert.Equal(VitalsLayout.SideBySide, kept.Load(Server, "Alice"));
        Assert.Equal(VitalsLayout.Stacked, kept.Load(Server, "Bob"));
        Assert.Equal(VitalsLayout.Stacked, kept.Load("Other", "Alice"));
    });

    [Theory]
    [InlineData(VitalsLayout.Stacked)]
    [InlineData(VitalsLayout.SideBySide)]
    public void Retail_style_bars_are_retails_window_with_numbers_in_the_middle_of_each_bar(VitalsLayout layout) => RenderThread.Run(() =>
    {
        var side = layout == VitalsLayout.SideBySide;
        var view = new DerethVitalsWindow(Game(), () => false, () => { }, layout, retailBars: true);
        using var panel = AvaloniaPanel.Create(() => view, side ? DerethVitalsWindow.SideDesignWidth : DerethVitalsWindow.DesignWidth,
            side ? DerethVitalsWindow.SideDesignHeight : DerethVitalsWindow.DesignHeight);
        panel.Tick();

        Assert.Empty(panel.Content.GetVisualDescendants().OfType<DerethVitalGlyph>());
        Assert.Empty(panel.Content.GetVisualDescendants().OfType<DerethGildedPlate>());
        Assert.Single(panel.Content.GetVisualDescendants().OfType<RetailVitalsFrame>());
        var bar = Bars(view)[0];
        var number = view.GetLogicalDescendants().OfType<OutlinedText>().First();
        Assert.Equal("86/100", number.Text);
        Assert.Equal(bar.Bounds, number.Bounds);
    });

    [Fact]
    public void Retail_style_bars_are_kept_per_character() => RenderThread.Run(() =>
    {
        var kept = new VitalsLayoutChoice(new MemoryWindowPositionStore());
        kept.SaveRetailBars(Server, "Alice", false);

        Assert.False(kept.RetailBars(Server, "Alice"));
        // On until a character turns it off.
        Assert.True(kept.RetailBars(Server, "Bob"));
        // The layout is kept apart from it.
        Assert.Equal(VitalsLayout.Stacked, kept.Load(Server, "Alice"));
    });

    [Fact]
    public void The_side_by_side_strip_stretches_wider_and_draws_larger_as_it_grows_taller() => RenderThread.Run(() =>
    {
        var windows = Windows(new MemoryWindowPositionStore(), "Alice");
        var strip = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.SideBySide, () => false), new Point(100, 100));
        Assert.Equal(new Size(440, 26), strip.Size);

        Grip(windows, strip, 200, 10);
        Assert.Equal(new Size(640, 36), strip.Size);

        // As tall as twice its height; as wide as the screen lets it.
        Grip(windows, strip, 1000, 100);
        Assert.Equal(new Size(900, 52), strip.Size);

        Grip(windows, strip, -1000, -100);
        Assert.Equal(new Size(200, 20), strip.Size);
    });

    // At a larger UI size the strip is taller, and must still have no corners to catch the first bar's glyph.
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void The_strip_resizes_only_from_its_grip_and_its_edges_so_a_press_on_its_glyph_moves_it(double uiScale) => RenderThread.Run(() =>
    {
        var windows = new WindowManager(Screen, new MemoryWindowPositionStore(), Server, "Alice", uiScale);
        var strip = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.SideBySide, () => false), new Point(100, 100));

        // The first bar's glyph sits inside the strip's left 16 DIPs: a press there is the content's move, not a corner resize.
        var at = new Point(strip.Location.X + 12, strip.Location.Y + 11);
        Assert.True(windows.Press(at));
        Assert.Null(windows.Resizing);
        windows.Release();
        // The content starts the move, as the view does on a press that should drag it.
        Assert.True(windows.BeginMove(strip.Id, at));
        Assert.Same(strip, windows.Moving);
        windows.Release();
        Assert.Equal(strip.Size, new Size((int)Math.Round(440 * uiScale), (int)Math.Round(26 * uiScale)));
    });

    // A stretched strip is taller in pixels, so a height-based corner rule would give it corner squares over the glyph again.
    [Theory]
    [InlineData(1.5, 440, 30)]
    [InlineData(1.5, 880, 40)]
    [InlineData(2.0, 440, 22)]
    public void A_press_on_the_strips_glyph_moves_it_at_any_scale_and_height(double uiScale, int width, int height) => RenderThread.Run(() =>
    {
        var windows = new WindowManager(new Size(2000, 1000), new MemoryWindowPositionStore(), Server, "Alice", uiScale);
        var strip = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.SideBySide, () => false), new Point(100, 100));
        Grip(windows, strip, (int)Math.Round(width * uiScale) - strip.Width, (int)Math.Round(height * uiScale) - strip.Height);
        Assert.Equal(new Size((int)Math.Round(width * uiScale), (int)Math.Round(height * uiScale)), strip.Size);

        var glyph = GlyphOf(uiScale, width, height);
        foreach (var point in new[] { glyph.Location, new Point(glyph.X + glyph.Width / 2, glyph.Y + glyph.Height / 2) })
        {
            var at = new Point(strip.Location.X + point.X, strip.Location.Y + point.Y);
            Assert.True(windows.Press(at));
            Assert.Null(windows.Resizing);
            windows.Release();
            Assert.True(windows.BeginMove(strip.Id, at));
            Assert.Same(strip, windows.Moving);
            windows.Release();
        }
    });

    [Fact]
    public void Each_layout_keeps_its_own_size_and_switching_keeps_the_place() => RenderThread.Run(() =>
    {
        var store = new MemoryWindowPositionStore();
        var windows = Windows(store, "Alice");
        var stacked = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.Stacked, () => false), new Point(300, 200));
        StretchBy(windows, stacked, 180);
        // A drag of the top band moves the window with no title bar.
        windows.Press(new Point(stacked.Location.X + 100, stacked.Location.Y + 2));
        windows.Move(new Point(stacked.Location.X + 120, stacked.Location.Y + 32));
        windows.Release();
        var placed = stacked.Location;
        Assert.Equal(new Size(432, 84), stacked.Size);

        // The player switches to the strip: it opens where the stacked window was left, at its own default size.
        windows.Close(stacked.Id);
        var strip = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.SideBySide, () => false), new Point(0, 0));
        Assert.Equal(placed, strip.Location);
        Assert.Equal(new Size(440, 26), strip.Size);
        Grip(windows, strip, 160, 8);
        Assert.Equal(new Size(600, 34), strip.Size);

        // And back: the stacked window keeps its 432 wide size, and the strip keeps its own when it is shown again.
        windows.Close(strip.Id);
        var back = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.Stacked, () => false), new Point(0, 0));
        Assert.Equal(placed, back.Location);
        Assert.Equal(new Size(432, 84), back.Size);
        windows.Close(back.Id);
        var again = windows.Open(DerethVitalsWindow.DefinitionOf(VitalsLayout.SideBySide, () => false), new Point(0, 0));
        Assert.Equal(placed, again.Location);
        Assert.Equal(new Size(600, 34), again.Size);
    });

    [Theory]
    [InlineData(VitalsLayout.Stacked, 252, 84)]
    [InlineData(VitalsLayout.SideBySide, 440, 26)]
    public void At_a_fractional_UI_size_the_plate_fills_the_window_to_its_edges(VitalsLayout layout, int width, int height) => RenderThread.Run(() =>
    {
        // Layout rounding snaps the plate to the window's pixels before the height scale applies; scaled as laid out, it ran up to 2 px past
        // the right and bottom edges at 85%, cutting its gold rule there.
        foreach (var scale in new[] { 0.85, 0.9, 0.95 })
        {
            using var panel = AvaloniaPanel.Create(() => new DerethVitalsWindow(Game(), () => false, () => { }, layout),
                (int)Math.Round(width * scale), (int)Math.Round(height * scale), scale);
            panel.Tick();
            var plate = panel.Content.GetVisualDescendants().OfType<DerethGildedPlate>().Single();
            var corner = Avalonia.VisualExtensions.TranslatePoint(plate, new Avalonia.Point(plate.Bounds.Width, plate.Bounds.Height), panel.Content)!.Value;
            Assert.Equal(panel.Content.Bounds.Width, corner.X, 3);
            Assert.Equal(panel.Content.Bounds.Height, corner.Y, 3);
        }
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
        foreach (var scale in new[] { 1.0, 2.0 })
        {
            // The stacked window at its default, stretched wider, and drawn larger.
            foreach (var (width, height) in new[] { (252, 84), (332, 84), (504, 168) })
            {
                var name = $"{width}x{height}-{scale:0}x.png";
                // The low state is health dropping from full at one second, so its trail shows.
                Render(Path.Combine(folder, "full-" + name), VitalsLayout.Stacked, width, height, scale, (full, 0));
                Render(Path.Combine(folder, "low-" + name), VitalsLayout.Stacked, width, height, scale, (full, 0), (low, 1));
            }
            // The side-by-side strip at its smallest, its default and drawn larger.
            foreach (var (width, height) in new[] { (200, 20), (440, 26), (880, 52) })
            {
                var name = $"{width}x{height}-{scale:0}x.png";
                Render(Path.Combine(folder, "side-full-" + name), VitalsLayout.SideBySide, width, height, scale, (full, 0));
                Render(Path.Combine(folder, "side-low-" + name), VitalsLayout.SideBySide, width, height, scale, (full, 0), (low, 1));
            }
            // The pointer over each default window: the grip and the layout gear show.
            Render(Path.Combine(folder, $"hover-stacked-252x84-{scale:0}x.png"), VitalsLayout.Stacked, 252, 84, scale, hover: true, (full, 0));
            Render(Path.Combine(folder, $"hover-side-440x26-{scale:0}x.png"), VitalsLayout.SideBySide, 440, 26, scale, hover: true, (full, 0));
            // Retail-style bars in each layout, at its default size and drawn larger.
            foreach (var (layout, width, height) in new[] { (VitalsLayout.Stacked, 252, 84), (VitalsLayout.Stacked, 504, 168), (VitalsLayout.SideBySide, 440, 26), (VitalsLayout.SideBySide, 880, 52) })
            {
                var name = $"retail-{(layout == VitalsLayout.Stacked ? "stacked" : "side")}-{width}x{height}-{scale:0}x.png";
                Render(Path.Combine(folder, "full-" + name), layout, width, height, scale, hover: false, retailBars: true, (full, 0));
                Render(Path.Combine(folder, "low-" + name), layout, width, height, scale, hover: false, retailBars: true, (full, 0), (low, 1));
            }
        }
    });

    /// <summary>
    /// Lays the window out in an in-game panel at <paramref name="width"/> by <paramref name="height"/>, at <paramref name="scale"/>, and saves its
    /// frame. With <paramref name="hover"/> the pointer is over the window first.
    /// </summary>
    private static void Render(string path, VitalsLayout layout, int width, int height, double scale, params (GameStateSnapshot State, double Seconds)[] frames) =>
        Render(path, layout, width, height, scale, hover: false, retailBars: false, frames);

    private static void Render(string path, VitalsLayout layout, int width, int height, double scale, bool hover, params (GameStateSnapshot State, double Seconds)[] frames) =>
        Render(path, layout, width, height, scale, hover, retailBars: false, frames);

    private static void Render(string path, VitalsLayout layout, int width, int height, double scale, bool hover, bool retailBars, params (GameStateSnapshot State, double Seconds)[] frames)
    {
        var game = new GameStatePort(frames[0].State);
        var view = new DerethVitalsWindow(game, () => false, () => { }, layout, retailBars: retailBars);
        using var panel = AvaloniaPanel.Create(() => view, (int)Math.Round(width * scale), (int)Math.Round(height * scale), scale);
        foreach (var (state, seconds) in frames)
        {
            game.Publish(state);
            view.Refresh(TimeSpan.FromSeconds(seconds));
        }
        if (hover) panel.PointerMove(width * scale / 2, height * scale / 2);
        panel.Tick();
        PanelPng.Save(panel, path);
    }

    private static List<DerethVitalBar> Bars(DerethVitalsWindow view) => view.GetLogicalDescendants().OfType<DerethVitalBar>().ToList();

    /// <summary>The first bar's glyph in the strip's pixels, as a panel at <paramref name="uiScale"/> lays it out.</summary>
    private static Rectangle GlyphOf(double uiScale, int width, int height)
    {
        var view = new DerethVitalsWindow(Game(), () => false, () => { }, VitalsLayout.SideBySide);
        using var panel = AvaloniaPanel.Create(() => view, (int)Math.Round(width * uiScale), (int)Math.Round(height * uiScale), uiScale);
        panel.Tick();
        var glyph = panel.Content.GetVisualDescendants().OfType<DerethVitalGlyph>().First();
        var origin = Avalonia.VisualExtensions.TranslatePoint(glyph, new Avalonia.Point(0, 0), panel.Content)!.Value;
        var end = Avalonia.VisualExtensions.TranslatePoint(glyph, new Avalonia.Point(glyph.Bounds.Width, glyph.Bounds.Height), panel.Content)!.Value;
        return new Rectangle((int)Math.Round(origin.X * uiScale), (int)Math.Round(origin.Y * uiScale),
            (int)Math.Round((end.X - origin.X) * uiScale), (int)Math.Round((end.Y - origin.Y) * uiScale));
    }

    /// <summary>The value labels, in retail's order.</summary>
    private static List<string> Labels(DerethVitalsWindow view) =>
        view.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Select(text => text.Inlines?.Text)
            .Concat(view.GetLogicalDescendants().OfType<OutlinedText>().Select(text => text.Text)).OfType<string>().Where(text => text.Contains('/')).ToList();

    /// <summary>A press and release on a control, as a player's click.</summary>
    private static void Press(AvaloniaPanel panel, Control control)
    {
        panel.Tick();
        var centre = Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
        panel.Tick();
    }

    /// <summary>The control the window shows under a tooltip, such as the layout gear.</summary>
    private static Control Medallion(AvaloniaPanel panel, string tip) =>
        panel.Content.GetVisualDescendants().OfType<Control>().First(control => Avalonia.Controls.ToolTip.GetTip(control) as string == tip);
}
