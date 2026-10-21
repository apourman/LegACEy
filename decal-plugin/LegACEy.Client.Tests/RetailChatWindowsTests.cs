using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;
using ScreenPoint = System.Drawing.Point;

namespace LegACEy.Client.Tests;

/// <summary>
/// The Dereth chat windows on one desktop, driven as a player drives them: tabs dragged out, dropped on a strip, merged back by the close
/// medallion, and restored after a restart, against a fake of retail's chat.
/// </summary>
public sealed class RetailChatWindowsTests
{
    private const string Character = "Alice";
    private static readonly DateTime Stamp = new(2026, 10, 15, 21, 2, 0);
    private static readonly RetailChatMembership InFellowship = new(Fellowship: true, Allegiance: false, Monarch: false, Patron: false, Vassals: false,
        Society: false, Olthoi: false, Open: ChatChannels.General | ChatChannels.Trade);

    [Fact]
    public void Dragging_a_tab_out_makes_a_window_holding_it_where_it_was_dropped() => RenderThread.Run(() => WithStore(path =>
    {
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character);
        var main = desk.Main;
        var drop = desk.Place(main, new System.Drawing.Point(600, 110));

        desk.Drag(desk.At(main, TabView(main, "Fellowship")), drop);
        desk.Tick();

        Assert.Equal(2, desk.Chat.Windows.Count);
        var torn = desk.Chat.Windows[1];
        Assert.Equal(new[] { "Fellowship" }, Strip(torn));
        Assert.DoesNotContain("Fellowship", Strip(main));
        Assert.True(desk.BoundsOf(torn).Contains(drop));

        // The torn-off window has its own input, and its tab talks to the fellowship.
        desk.TypeAndEnter(torn, "hi");
        Assert.Equal("/f hi", port.Sent.Last());
    }));

    [Fact]
    public void A_press_that_stays_near_the_strip_is_a_click_and_tears_nothing() => RenderThread.Run(() => WithStore(path =>
    {
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character);
        var main = desk.Main;
        var tab = TabView(main, "Fellowship");

        var press = desk.At(main, tab);
        desk.Drag(press, new System.Drawing.Point(press.X, press.Y + 3));
        desk.Tick();

        Assert.Single(desk.Chat.Windows);
        Assert.Equal("Fellows", ChipText(main, "Fellows").Text);
    }));

    [Fact]
    public void Dragging_a_tab_onto_another_windows_strip_moves_it_there() => RenderThread.Run(() => WithStore(path =>
    {
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character);
        var main = desk.Main;
        desk.Drag(desk.At(main, TabView(main, "Fellowship")), desk.Place(main, new System.Drawing.Point(600, 110)));
        var torn = desk.Chat.Windows[1];

        // Allegiance is dragged out of the main window and dropped on the torn-off window's strip, on its Fellowship tab.
        desk.Drag(desk.At(main, TabView(main, "Allegiance")), desk.At(torn, TabView(torn, "Fellowship")));
        desk.Tick();

        Assert.Equal(2, desk.Chat.Windows.Count);
        Assert.Equal(new[] { "Fellowship", "Allegiance" }, Strip(torn));
        Assert.DoesNotContain("Allegiance", Strip(main));
    }));

    [Fact]
    public void Dragging_a_torn_off_tab_back_onto_the_main_strip_merges_it() => RenderThread.Run(() => WithStore(path =>
    {
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character);
        var main = desk.Main;
        desk.Drag(desk.At(main, TabView(main, "Fellowship")), desk.Place(main, new System.Drawing.Point(600, 110)));
        var torn = desk.Chat.Windows[1];

        desk.Drag(desk.At(torn, TabView(torn, "Fellowship")), desk.At(main, TabView(main, "Speech")));
        desk.Tick();

        Assert.Single(desk.Chat.Windows);
        Assert.Equal(new[] { "All", "Speech", "Allegiance", "Channels", "Tells", "Fellowship" }, Strip(main));
    }));

    [Fact]
    public void Closing_a_torn_off_window_merges_its_tabs_back_into_the_main_window() => RenderThread.Run(() => WithStore(path =>
    {
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character);
        var main = desk.Main;
        desk.Drag(desk.At(main, TabView(main, "Fellowship")), desk.Place(main, new System.Drawing.Point(600, 110)));
        var torn = desk.Chat.Windows[1];
        desk.Drag(desk.At(main, TabView(main, "Channels")), desk.At(torn, TabView(torn, "Fellowship")));
        Assert.Equal(new[] { "Fellowship", "Channels" }, Strip(torn));

        // The torn-off window has no add button; its close medallion merges it back.
        Assert.DoesNotContain("+", Strip(torn));
        desk.Click(torn, torn.GetVisualDescendants().OfType<DerethButton>().Single(button => button.Content is not (string or TextBlock { Text: "Send" })));
        desk.Tick();

        Assert.Single(desk.Chat.Windows);
        Assert.Equal(new[] { "All", "Speech", "Allegiance", "Tells", "Fellowship", "Channels" }, Strip(main));
    }));

    [Fact]
    public void Tabs_places_and_sizes_are_restored_per_character_after_a_restart() => RenderThread.Run(() => WithStore(path =>
    {
        var positions = new MemoryWindowPositionStore();
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        var first = ChatDesk.Open(port, path, Character, positions);
        var main = first.Main;
        first.Drag(first.At(main, TabView(main, "Fellowship")), first.Place(main, new System.Drawing.Point(600, 110)));
        var torn = first.Chat.Windows[1];
        first.Drag(first.At(main, TabView(main, "Channels")), first.At(torn, TabView(torn, "Fellowship")));

        // Resized from its bottom right corner, as the player does.
        var bounds = first.BoundsOf(torn);
        first.Drag(new ScreenPoint(bounds.Right - 4, bounds.Bottom - 4), new ScreenPoint(bounds.Right + 60, bounds.Bottom + 30));
        first.Tick();
        var placed = first.BoundsOf(torn);
        // The press is 4 DIPs inside the right edge, and the pointer moves 64 across.
        Assert.Equal(bounds.Width + 64, placed.Width);
        first.Dispose();

        // A restart: a new desk on the same files restores the windows, their tabs, and their place and size.
        using var second = ChatDesk.Open(port, path, Character, positions);
        Assert.Equal(2, second.Chat.Windows.Count);
        Assert.Equal(new[] { "All", "Speech", "Allegiance", "Tells" }, Strip(second.Main));
        var restored = second.Chat.Windows[1];
        Assert.Equal(new[] { "Fellowship", "Channels" }, Strip(restored));
        Assert.Equal(placed, second.BoundsOf(restored));

        // Another character has the six tabs and no torn-off windows.
        using var other = ChatDesk.Open(port, path, "Bob", positions);
        Assert.Single(other.Chat.Windows);
        Assert.Equal(new[] { "All", "Speech", "Fellowship", "Allegiance", "Channels", "Tells" }, Strip(other.Main));
    }));

    [Fact]
    public void The_text_size_steps_every_window_and_is_kept_per_character() => RenderThread.Run(() => WithStore(path =>
    {
        var positions = new MemoryWindowPositionStore();
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        var first = ChatDesk.Open(port, path, Character, positions);
        var main = first.Main;
        first.Drag(first.At(main, TabView(main, "Fellowship")), first.Place(main, new System.Drawing.Point(600, 110)));
        Assert.Equal(RetailChatWindows.DefaultTextSize / 100.0, first.Chat.Windows[1].TextScale);

        first.Chat.TextSizeStepped(1);
        first.Chat.TextSizeStepped(1);
        Assert.All(first.Chat.Windows, window => Assert.Equal(1.4, window.TextScale, 3));
        first.Dispose();

        using var second = ChatDesk.Open(port, path, Character, positions);
        Assert.Equal(140, second.Chat.TextSize);
        using var other = ChatDesk.Open(port, path, "Bob", positions);
        Assert.Equal(RetailChatWindows.DefaultTextSize, other.Chat.TextSize);
    }));

    [Fact]
    public void The_text_size_event_fires_once_per_change_and_not_past_the_top_bound() => RenderThread.Run(() => WithStore(path =>
    {
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character, new MemoryWindowPositionStore());
        var sizes = new List<int>();
        desk.Chat.TextSizeChanged += sizes.Add;

        desk.Chat.TextSizeStepped(1);
        desk.Chat.SetTextSize(RetailChatWindows.LargestTextSize);
        desk.Chat.TextSizeStepped(1);

        Assert.Equal(new[] { 130, RetailChatWindows.LargestTextSize }, sizes);
    }));

    [Fact]
    public void The_fade_delay_reaches_every_open_window_and_is_kept_per_character() => RenderThread.Run(() => WithStore(path =>
    {
        var clock = new DeskClock(Stamp);
        var positions = new MemoryWindowPositionStore();
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        var first = ChatDesk.Open(port, path, Character, positions, clock: () => clock.Now);
        var main = first.Main;
        first.Drag(first.At(main, TabView(main, "Fellowship")), first.Place(main, new System.Drawing.Point(600, 110)));
        first.Chat.SetFadeSeconds(RetailChatWindows.NeverFades);
        clock.Advance(TimeSpan.FromHours(1));
        first.Tick();
        Assert.All(first.Chat.Windows, window => Assert.Equal(1.0, Seen(Frame(window))));
        first.Dispose();

        // Never is kept for Alice, so her windows stay shown when they reopen; Bob has no choice saved, so he fades after 15 seconds.
        using var second = ChatDesk.Open(port, path, Character, positions, clock: () => clock.Now);
        second.Tick();
        clock.Advance(TimeSpan.FromHours(1));
        second.Tick();
        Assert.Equal(1.0, Seen(Frame(second.Main)));
        using var other = ChatDesk.Open(port, path, "Bob", positions, clock: () => clock.Now);
        other.Tick();
        clock.Advance(TimeSpan.FromSeconds(RetailChatFade.DefaultIdleAfterSeconds + 1));
        other.Tick();
        Assert.Equal(0.0, Seen(Frame(other.Main)));
    }));

    [Fact]
    public void Instant_fades_the_window_as_soon_as_it_is_left_alone() => RenderThread.Run(() => WithStore(path =>
    {
        var clock = new DeskClock(Stamp);
        var positions = new MemoryWindowPositionStore();
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var first = ChatDesk.Open(port, path, Character, positions, clock: () => clock.Now);
        Assert.Equal(1.0, Seen(Frame(first.Main)));
        first.Chat.SetFadeSeconds(RetailChatWindows.FadesInstantly);
        clock.Advance(TimeSpan.FromSeconds(0.3));
        first.Tick();
        Assert.Equal(0.0, Seen(Frame(first.Main)));
        first.Dispose();

        // Instant is kept for Alice, so her window fades at once when it reopens.
        using var second = ChatDesk.Open(port, path, Character, positions, clock: () => clock.Now);
        clock.Advance(TimeSpan.FromSeconds(0.3));
        second.Tick();
        Assert.Equal(0.0, Seen(Frame(second.Main)));
    }));

    [Fact]
    public void Unread_counts_work_in_a_torn_off_window_and_the_main_window() => RenderThread.Run(() => WithStore(path =>
    {
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character);
        var main = desk.Main;
        desk.Drag(desk.At(main, TabView(main, "Fellowship")), desk.Place(main, new System.Drawing.Point(600, 110)));
        var torn = desk.Chat.Windows[1];
        desk.Drag(desk.At(main, TabView(main, "Allegiance")), desk.At(torn, TabView(torn, "Fellowship")));
        desk.Click(torn, TabView(torn, "Fellowship"));

        port.Say(new RetailChatLine(RetailChatKind.Allegiance, "Kaelith tells the allegiance, \"pull the next\"", Stamp, "Kaelith"));
        port.Say(new RetailChatLine(RetailChatKind.Channel, "[General] Mirra: anyone running Hebian?", Stamp, "Mirra", "General"));
        desk.Tick();

        // The allegiance line counts on the torn-off window's Allegiance tab, which is not showing; the channel line counts on the main
        // window's Channels tab. The torn-off window's selected Fellowship tab shows its lines and counts none.
        Assert.Equal(new[] { "Fellowship", "Allegiance 1" }, Strip(torn));
        Assert.Contains("Channels 1", Strip(main));

        desk.Click(torn, TabView(torn, "Allegiance"));
        desk.Tick();
        Assert.Equal(new[] { "Fellowship", "Allegiance" }, Strip(torn));
    }));

    [Fact]
    public void Every_window_fades_when_idle_not_only_the_main_window() => RenderThread.Run(() => WithStore(path =>
    {
        var clock = new DeskClock(Stamp);
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character, clock: () => clock.Now);
        var main = desk.Main;
        desk.Drag(desk.At(main, TabView(main, "Fellowship")), desk.Place(main, new System.Drawing.Point(600, 110)));
        var torn = desk.Chat.Windows[1];

        // Nothing is over the windows: left alone, both go idle and fade their tab strip over a quarter of a second.
        clock.Advance(TimeSpan.FromSeconds(1));
        desk.Tick();
        clock.Advance(TimeSpan.FromSeconds(RetailChatFade.DefaultIdleAfterSeconds + 1));
        desk.Tick();

        foreach (var window in new[] { main, torn })
        {
            Assert.Equal(0.0, Seen(StripPanel(window)));
            Assert.Equal(0.0, Seen(Frame(window)));
            Assert.Equal(0.0, Seen(InputBox(window)));
        }
    }));

    [Fact]
    public void A_window_does_not_fade_while_it_is_dragged() => RenderThread.Run(() => WithStore(path =>
    {
        var clock = new DeskClock(Stamp);
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character, clock: () => clock.Now);
        var main = desk.Main;

        // A tab is dragged out and held: the new window follows the pointer, and seconds pass before the drop.
        var drop = desk.Place(main, new System.Drawing.Point(600, 110));
        desk.Hold(desk.At(main, TabView(main, "Fellowship")), drop);
        var torn = desk.Chat.Windows[1];
        for (var frame = 0; frame < 3; frame++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            desk.Tick();
        }
        Assert.Equal(1.0, Seen(StripPanel(torn)));
        Assert.Equal(1.0, Seen(Frame(torn)));
        desk.Release();

        // Let go and left alone, it fades as any window does.
        clock.Advance(TimeSpan.FromSeconds(1));
        desk.Tick();
        clock.Advance(TimeSpan.FromSeconds(RetailChatFade.DefaultIdleAfterSeconds + 1));
        desk.Tick();
        Assert.Equal(0.0, Seen(Frame(torn)));
    }));

    [Fact]
    public void A_window_covering_a_strip_keeps_the_tab_dropped_on_it() => RenderThread.Run(() => WithStore(path =>
    {
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using var desk = ChatDesk.Open(port, path, Character);
        var main = desk.Main;
        desk.Drag(desk.At(main, TabView(main, "Fellowship")), desk.Place(main, new System.Drawing.Point(600, 110)));
        var torn = desk.Chat.Windows[1];

        // A non-chat window covers the main window's Speech tab. A tab dropped there does not merge into the strip underneath.
        var speech = desk.At(main, TabView(main, "Speech"));
        desk.Cover(new System.Drawing.Point(speech.X - 100, speech.Y - 10));
        desk.Drag(desk.At(torn, TabView(torn, "Fellowship")), speech);
        desk.Tick();

        Assert.Equal(2, desk.Chat.Windows.Count);
        Assert.Equal(new[] { "Fellowship" }, Strip(torn));
        Assert.DoesNotContain("Fellowship", Strip(main));
    }));

    [Fact]
    public void A_tab_merged_on_release_does_not_reappear_when_the_chat_closes_before_the_next_frame() => RenderThread.Run(() => WithStore(path =>
    {
        var positions = new MemoryWindowPositionStore();
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        var desk = ChatDesk.Open(port, path, Character, positions);
        var main = desk.Main;
        desk.Drag(desk.At(main, TabView(main, "Fellowship")), desk.Place(main, new System.Drawing.Point(600, 110)));
        var torn = desk.Chat.Windows[1];

        // Dropped on the main strip: the torn-off window is retired at the next frame, and the chat closes before that frame.
        desk.Drag(desk.At(torn, TabView(torn, "Fellowship")), desk.At(main, TabView(main, "Speech")));
        desk.Dispose();

        using var again = ChatDesk.Open(port, path, Character, positions);
        Assert.Single(again.Chat.Windows);
        Assert.Equal(new[] { "All", "Speech", "Allegiance", "Channels", "Tells", "Fellowship" }, Strip(again.Main));
    }));

    [Fact]
    public void A_saved_window_row_whose_id_is_not_base64_is_ignored_and_the_other_windows_open() => RenderThread.Run(() => WithStore(path =>
    {
        var positions = new MemoryWindowPositionStore();
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        using (var desk = ChatDesk.Open(port, path, Character, positions))
        {
            var main = desk.Main;
            desk.Drag(desk.At(main, TabView(main, "Fellowship")), desk.Place(main, new System.Drawing.Point(600, 110)));
        }
        File.AppendAllText(path, Convert.ToBase64String(Encoding.UTF8.GetBytes("server")) + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Character))
            + "|not base64!|" + Convert.ToBase64String(Encoding.UTF8.GetBytes("x")) + "\n");

        using var again = ChatDesk.Open(port, path, Character, positions);
        Assert.Equal(2, again.Chat.Windows.Count);
        Assert.Equal(new[] { "Fellowship" }, Strip(again.Chat.Windows[1]));
    }));

    [Fact]
    public void Closing_the_chat_closes_every_window_and_keeps_the_torn_off_windows_for_next_time() => RenderThread.Run(() => WithStore(path =>
    {
        var positions = new MemoryWindowPositionStore();
        var port = new RetailChatTests.FakeChatPort { Membership = InFellowship };
        var desk = ChatDesk.Open(port, path, Character, positions);
        var main = desk.Main;
        desk.Drag(desk.At(main, TabView(main, "Fellowship")), desk.Place(main, new System.Drawing.Point(600, 110)));
        Assert.Equal(2, desk.OpenCount);

        desk.Dispose();
        Assert.Equal(0, desk.OpenCount);

        // Retail's chat gets its windows back at the same time; the next open has the torn-off window again.
        using var again = ChatDesk.Open(port, path, Character, positions);
        Assert.Equal(new[] { "Fellowship" }, Strip(again.Chat.Windows[1]));
    }));

    private static void WithStore(Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), "legacey-chat-windows-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            body(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The tab's label on a window's strip: the first text with that name, the strip being first in the window.</summary>
    private static TextBlock TabView(RetailChatWindow window, string name) =>
        window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == name);

    // The window's own frame is the first one in its tree: the window's child is the frame beside the content.
    private static DerethFrame Frame(RetailChatWindow window) => window.GetVisualDescendants().OfType<DerethFrame>().First();

    private static TextBox InputBox(RetailChatWindow window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "Say something…");

    private static StackPanel StripPanel(RetailChatWindow window) =>
        window.GetVisualDescendants().OfType<StackPanel>().First(strip => strip.Orientation == Orientation.Horizontal && strip.Height == 19);

    /// <summary>The tab labels left to right, each with its unread count when it has one, as the player reads them: the plus is not a tab.</summary>
    private static string[] Strip(RetailChatWindow window) =>
        StripPanel(window).Children.OfType<Border>().Select(tab => string.Join(" ", tab.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)))
            .Where(text => text != "+").ToArray();

    private static TextBlock ChipText(RetailChatWindow window, string label) =>
        window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == label);

    /// <summary>The window's visual opacity: its own and its ancestors', as the player sees it.</summary>
    private static double Seen(Visual part) => part.GetSelfAndVisualAncestors().OfType<Visual>().Aggregate(1.0, (seen, visual) => seen * visual.Opacity);

    private sealed class DeskClock
    {
        public DeskClock(DateTime start) => Now = start;

        public DateTime Now { get; private set; }

        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>
    /// A desktop of chat windows: each window is an Avalonia panel the window manager places, and the pointer is routed as the client
    /// routes it. A press is captured by the window it lands on, moves go to the window manager while a window is being dragged, and a
    /// release merges a dropped tab through the chat windows.
    /// </summary>
    private sealed class ChatDesk : IDisposable
    {
        private static readonly System.Drawing.Point Beside = new(12, 730);
        private readonly WindowManager _windows;
        private readonly Dictionary<string, Control> _contents = new();
        private readonly Dictionary<string, AvaloniaPanel> _panels = new();
        private System.Drawing.Point _pointer;
        private string? _captured;

        private ChatDesk(RetailChatTests.FakeChatPort port, string path, string character, MemoryWindowPositionStore positions, Func<DateTime> clock)
        {
            // Avalonia's platform services start with the first panel, and a chat window's cursors need them before its panel exists.
            AvaloniaPanel.Create(() => new Border(), 1, 1).Dispose();
            _windows = new WindowManager(new System.Drawing.Size(1920, 1080), positions, "server", character);
            Chat = new RetailChatWindows(port, _windows, positions, new FileRetailChatTabStore(path), "server", character,
                () => _pointer, Show, Close, clock);
        }

        public RetailChatWindows Chat { get; }

        public RetailChatWindow Main => Chat.Main!;

        public int OpenCount => _windows.ZOrder.Count;

        /// <summary>Opens a desk's windows from the saved files, as the client does when retail's chat is taken over.</summary>
        public static ChatDesk Open(RetailChatTests.FakeChatPort port, string path, string character, MemoryWindowPositionStore? positions = null,
            Func<DateTime>? clock = null)
        {
            var desk = new ChatDesk(port, path, character, positions ?? new MemoryWindowPositionStore(), clock ?? (() => Stamp));
            desk.Chat.Open(Beside);
            desk.Tick();
            return desk;
        }

        /// <summary>A point in the main window's panel, shifted onto the desktop: the drop places a dragged window's grab point there.</summary>
        public System.Drawing.Point Place(RetailChatWindow window, System.Drawing.Point offset)
        {
            var at = Windows.Get(IdOf(window))!.Location;
            return new System.Drawing.Point(at.X + offset.X, at.Y + offset.Y);
        }

        public System.Drawing.Rectangle BoundsOf(RetailChatWindow window) => Windows.Get(IdOf(window))!.Bounds;

        /// <summary>The desktop point at a control's centre, in its window.</summary>
        public System.Drawing.Point At(RetailChatWindow window, Control control)
        {
            Tick();
            var panel = _panels[IdOf(window)];
            var local = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;
            var at = Windows.Get(IdOf(window))!.Location;
            return new System.Drawing.Point(at.X + (int)local.X, at.Y + (int)local.Y);
        }

        /// <summary>Types into a window's box and presses Enter, as the player does.</summary>
        public void TypeAndEnter(RetailChatWindow window, string text)
        {
            var panel = _panels[IdOf(window)];
            Click(window, window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "Say something…"));
            panel.TextInput(text);
            panel.KeyDown(Key.Enter);
        }

        /// <summary>A click on a control: a press and a release where it is.</summary>
        public void Click(RetailChatWindow window, Control control)
        {
            var point = At(window, control);
            Drag(point, point);
        }

        /// <summary>
        /// A left press where <paramref name="from"/> is, moves through each of the points, and a release at the last: as a drag is routed.
        /// </summary>
        public void Drag(System.Drawing.Point from, params System.Drawing.Point[] through)
        {
            Hold(from, through);
            Release();
        }

        /// <summary>A left press where <paramref name="from"/> is and moves through each of the points, with the button still held.</summary>
        public void Hold(System.Drawing.Point from, params System.Drawing.Point[] through)
        {
            Tick();
            _pointer = from;
            Windows.Press(from);
            var owner = Windows.HitTest(from)!;
            _captured = owner.Id;
            var (x, y) = Local(owner, from);
            _panels[owner.Id].PointerDown(x, y);
            foreach (var point in through)
            {
                _pointer = point;
                if (Windows.IsDragging)
                {
                    Windows.Move(point);
                    continue;
                }
                var captured = Windows.Get(_captured)!;
                var (moveX, moveY) = Local(captured, point);
                _panels[_captured].PointerMove(moveX, moveY);
            }
        }

        /// <summary>Lets go of the held button where the pointer is.</summary>
        public void Release()
        {
            var end = Windows.Get(_captured!)!;
            var (endX, endY) = Local(end, _pointer);
            _panels[_captured!].PointerUp(endX, endY);
            if (Windows.IsDragging)
            {
                Windows.Release();
                Chat.Released();
            }
            if (_panels.TryGetValue(_captured, out var panel)) panel.PointerLeave();
            _captured = null;
        }

        /// <summary>A window of another kind, which covers the chat windows under it, as a LegACEy window that is not a chat does.</summary>
        public void Cover(System.Drawing.Point at) => _windows.Open(new WindowDefinition("cover", "Cover", 200, 60), at);

        /// <summary>Runs the chat's frame work and every panel's frame, as the client's post-UI draw does.</summary>
        public void Tick()
        {
            Chat.Refresh();
            foreach (var panel in _panels.Values.ToArray()) panel.Tick();
        }

        public void Dispose() => Chat.Dispose();

        private string IdOf(RetailChatWindow window) => _contents.Single(pair => pair.Value == window).Key;

        private WindowManager Windows => _windows;

        private void Show(WindowDefinition definition, Control content, System.Drawing.Point location)
        {
            _contents[definition.Id] = content;
            _panels[definition.Id] = AvaloniaPanel.Create(() => content, definition.Width, definition.Height);
            _windows.Open(definition, location);
        }

        private void Close(string id)
        {
            _windows.Close(id);
            _panels[id].Dispose();
            _panels.Remove(id);
            _contents.Remove(id);
        }

        private static (double X, double Y) Local(ManagedWindow window, System.Drawing.Point point) => (point.X - window.Location.X, point.Y - window.Location.Y);
    }
}
