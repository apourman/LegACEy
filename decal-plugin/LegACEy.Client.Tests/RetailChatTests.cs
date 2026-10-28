using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.InputRouter;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

/// <summary>The Dereth chat window, driven as a player does, against a fake of retail's chat.</summary>
public sealed class RetailChatTests
{
    private const int Width = 460;
    private const int Height = 200;
    private static readonly DateTime Stamp = new(2026, 10, 15, 21, 2, 0);

    [Fact]
    public void Each_default_tab_shows_its_kinds_and_All_shows_combat_and_plugin_lines() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        foreach (var line in SampleLines()) port.Say(line);
        panel.Tick();

        // Each tab, by the fragment its lines carry (a sender's name is split out of the line, so fragments avoid names).
        var expected = new (string Tab, string[] Shown)[]
        {
            ("All", new[] { "nice cloak", "bows deeply", "still need", "yes please", "pull the next", "monarch is on", "anyone running", "Welcome", "Disconnected", "42 points" }),
            ("Speech", new[] { "nice cloak", "bows deeply", "still need", "yes please" }),
            ("Fellowship", new[] { "pull the next" }),
            ("Allegiance", new[] { "monarch is on" }),
            ("Channels", new[] { "anyone running" }),
            ("Tells", new[] { "still need", "yes please" })
        };
        foreach (var (tab, shown) in expected)
        {
            Click(panel, TabView(window, tab));
            panel.Tick();
            Assert.Equal(shown.OrderBy(s => s), ShownFragments(panel, SampleFragments).OrderBy(s => s));
        }
    });

    [Fact]
    public void A_long_log_keeps_a_wide_thumb_long_enough_to_grab() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out _);
        panel.ApplyTheme(new DerethClientTheme());
        for (var i = 0; i < 500; i++) port.Say(Line(RetailChatKind.Local, $"Line {i}", "Mirra"));
        panel.Tick();

        var bar = panel.Content!.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical && b.IsVisible);
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
        Assert.Equal(14, bar.Bounds.Width);
        Assert.True(thumb.Bounds.Height >= 28, $"Thumb is {thumb.Bounds.Height} px tall.");
    });

    [Fact]
    public void Picking_a_tab_sets_its_talk_target_and_the_chip_shows_it_in_its_colour() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"still need the Vault key?\"", "Mirra"));
        panel.Tick();

        Click(panel, TabView(window, "Fellowship"));
        panel.Tick();
        var chip = ChipText(panel, "Fellows");
        Assert.Equal(DerethChatPalette.Fellowship, ((SolidColorBrush)chip.Foreground!).Color);

        Click(panel, TabView(window, "Tells"));
        panel.Tick();
        Assert.Equal(DerethChatPalette.Tell, ((SolidColorBrush)ChipText(panel, "Tell Mirra").Foreground!).Color);
    });

    [Fact]
    public void The_menu_groups_the_targets_dims_the_unusable_with_the_reason_and_lists_recent_tells() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort { Membership = Alone with { Open = ChatChannels.General } };
        using var panel = Open(port, out var window);
        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"on my way\"", "Mirra"));
        panel.Tick();

        Click(panel, ChipText(panel, "Local"));
        panel.Tick();

        Assert.True(Shows(panel, "NEARBY"));
        Assert.True(Shows(panel, "RECENT TELLS"));
        Assert.True(Shows(panel, "Tell Mirra"));
        Assert.True(Shows(panel, "not in a fellowship"));
        Assert.True(Shows(panel, "not in a society"));
        Assert.True(Shows(panel, "Olthoi only"));
        Assert.True(Shows(panel, "nothing selected"));
        Assert.True(Shows(panel, "channel is off"));

        // A dimmed target does nothing when picked; a usable one is picked and closes the menu.
        Click(panel, MenuItem(panel, "Fellows"));
        panel.Tick();
        Assert.Equal("Local", ChipText(panel, "Local").Text);
        Click(panel, MenuItem(panel, "General"));
        panel.Tick();
        Assert.Equal("General", ChipText(panel, "General").Text);
        Assert.False(Shows(panel, "NEARBY"));
    });

    [Fact]
    public void Enter_and_Send_send_the_command_each_target_needs_and_typed_commands_pass_as_typed() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort { SelectedName = "Drudge Skulker", Membership = Alone with { Fellowship = true } };
        using var panel = Open(port, out var window);
        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"hi\"", "Mirra"));
        panel.Tick();

        TypeAndEnter(panel, window, "hi");
        Assert.Equal("hi", port.Sent.Last());

        Click(panel, TabView(window, "Fellowship"));
        TypeAndSend(panel, window, "hi");
        Assert.Equal("/f hi", port.Sent.Last());

        TypeAndEnter(panel, window, "/vt go");
        Assert.Equal("/vt go", port.Sent.Last());

        TypeAndEnter(panel, window, "@help");
        Assert.Equal("@help", port.Sent.Last());

        Click(panel, TabView(window, "Tells"));
        TypeAndEnter(panel, window, "hi back");
        Assert.Equal("/t Mirra, hi back", port.Sent.Last());

        Click(panel, ChipText(panel, "Tell Mirra"));
        Click(panel, MenuItem(panel, "Selected"));
        TypeAndEnter(panel, window, "hi selected");
        Assert.Equal("/t Drudge Skulker, hi selected", port.Sent.Last());

        var count = port.Sent.Count;
        TypeAndEnter(panel, window, "   ");
        Assert.Equal(count, port.Sent.Count);
    });

    [Fact]
    public void Up_and_Down_bring_back_what_was_sent_and_then_the_unsent_text() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        TypeAndEnter(panel, window, "/vt go");
        TypeAndEnter(panel, window, "hi");
        TypeAndEnter(panel, window, "hi");
        var box = InputBox(window);
        Click(panel, box);
        panel.TextInput("draft");

        panel.KeyDown(Key.Up);
        Assert.Equal("hi", box.Text);
        panel.KeyDown(Key.Up);
        Assert.Equal("/vt go", box.Text);
        panel.KeyDown(Key.Up);
        Assert.Equal("/vt go", box.Text);
        panel.KeyDown(Key.Down);
        panel.KeyDown(Key.Down);
        Assert.Equal("draft", box.Text);

        panel.KeyDown(Key.Up);
        panel.KeyDown(Key.Up);
        panel.KeyDown(Key.Enter);
        Assert.Equal("/vt go", port.Sent.Last());
        Click(panel, box);
        panel.KeyDown(Key.Up);
        Assert.Equal("/vt go", box.Text);
    });

    [Fact]
    public void Inserted_text_goes_in_at_the_caret_apart_from_the_words_beside_it() => RenderThread.Run(() =>
    {
        using var panel = Open(new FakeChatPort(), out var window);
        var box = InputBox(window);
        Click(panel, box);
        panel.TextInput("meet at");

        window.Insert("33.6N, 56.7E");
        Assert.Equal("meet at 33.6N, 56.7E", box.Text);
        box.CaretIndex = 4;
        window.Insert("me");
        Assert.Equal("meet me at 33.6N, 56.7E", box.Text);
        Assert.Equal(7, box.CaretIndex);
    });

    [Fact]
    public void Ctrl_V_pastes_the_clipboard_into_the_box_and_Ctrl_C_and_Ctrl_X_copy_its_selection() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        var clipboard = "the Vault key\r\nat Yaraq";
        using var panel = Open(port, out var window, copy: text => clipboard = text, paste: () => clipboard);
        var box = InputBox(window);
        Click(panel, box);
        panel.TextInput("bring ");

        panel.KeyDown(Key.V, KeyModifiers.Control);
        Assert.Equal("bring the Vault key at Yaraq", box.Text);
        Assert.Equal(box.Text.Length, box.CaretIndex);

        // "Vault" selected: Ctrl+C copies it, Ctrl+X copies it and takes it out, and Ctrl+V puts it back over a selection.
        box.SelectionStart = 10;
        box.SelectionEnd = 15;
        panel.KeyDown(Key.C, KeyModifiers.Control);
        Assert.Equal("Vault", clipboard);
        panel.KeyDown(Key.X, KeyModifiers.Control);
        Assert.Equal("bring the  key at Yaraq", box.Text);
        box.SelectionStart = 0;
        box.SelectionEnd = 5;
        panel.KeyDown(Key.V, KeyModifiers.Control);
        Assert.Equal("Vault the  key at Yaraq", box.Text);
        Assert.Empty(port.Sent);
    });

    [Fact]
    public void A_rebuilt_chat_keeps_the_log_and_what_was_sent_from_the_history_it_carries() => RenderThread.Run(() =>
    {
        // The chat before a UI size change: a line in the log and a line sent.
        var before = new FakeChatPort();
        var old = new RetailChatHistory(before);
        before.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"still need the Vault key?\"", "Mirra"));
        old.AddSent("/vt go");
        old.Dispose();

        // After it: a new port, and windows on a history that carries the old one.
        var port = new FakeChatPort();
        var history = new RetailChatHistory(port, old);
        RetailChatWindow? window = null;
        using var panel = AvaloniaPanel.Create(() => window = new RetailChatWindow(port, history, new SingleWindowHost(null, "Alice"), clock: () => Stamp), Width, Height);
        panel.Tick();
        Assert.True(InLog(panel, "still need the Vault key"));

        var box = InputBox(window!);
        Click(panel, box);
        panel.KeyDown(Key.Up);
        Assert.Equal("/vt go", box.Text);
    });

    [Fact]
    public void The_chat_window_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_CHAT_RENDER names the folder the PNGs are written to.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_CHAT_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        foreach (var scale in new[] { 0.75, 1.0, 1.5 })
        {
            var port = new FakeChatPort();
            var history = new RetailChatHistory(port);
            using var panel = AvaloniaPanel.Create(() => new RetailChatWindow(port, history, new SingleWindowHost(null, "Alice"), clock: () => Stamp),
                (int)Math.Round(Width * scale), (int)Math.Round(Height * scale), scale);
            port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"still need the Vault key?\"", "Mirra"));
            panel.Tick();
            var frame = panel.Frame;
            using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var locked = bitmap.Lock())
                System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, locked.Address, frame.Pixels.Length);
            bitmap.Save(Path.Combine(folder, $"chat-{scale * 100:0}.png"));
        }
    });

    [Fact]
    public void A_name_click_and_Tab_in_an_empty_input_set_a_tell_target() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"hi\"", "Mirra"));
        panel.Tick();

        ClickName(panel, "Mirra tells you", "Mirra");
        Assert.Equal("Tell Mirra", ChipText(panel, "Tell Mirra").Text);
        TypeAndEnter(panel, window, "yo");
        Assert.Equal("/t Mirra, yo", port.Sent.Last());

        // Tab in the empty box answers the last tell, whoever it was from.
        Click(panel, TabView(window, "Speech"));
        port.Say(Line(RetailChatKind.TellIn, "Saro tells you, \"ready\"", "Saro"));
        Click(panel, InputBox(window));
        panel.KeyDown(Key.Tab);
        Assert.Equal("Tell Saro", ChipText(panel, "Tell Saro").Text);
    });

    [Fact]
    public void A_link_click_goes_to_Decal_s_plugins_and_a_name_a_plugin_takes_sets_no_tell() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        port.Say(LegACEy.Client.DecalPlugin.NativeRetailChatPort.RetailChatTypes.Parse(RetailChatKind.Local,
            "Meet at <Tell:IIDString:110011:10.7N, 52.3E>10.7N, 52.3E</Tell>", Stamp));
        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"hi\"", "Mirra"));
        panel.Tick();

        // A plugin's link is drawn in retail's link green; the sender's name keeps the line's colour.
        Assert.Equal(DerethChatPalette.Link, ((SolidColorBrush)LineOf(panel, "Meet at").Inlines!.OfType<Run>().Last().Foreground!).Color);
        ClickName(panel, "Meet at", "10.7N");
        Assert.Equal((110011u, "10.7N, 52.3E"), port.Clicks.Last());

        port.Eats = true;
        ClickName(panel, "Mirra tells you", "Mirra");
        Assert.Equal((0u, "Mirra"), port.Clicks.Last());
        TypeAndEnter(panel, window, "yo");
        Assert.NotEqual("/t Mirra, yo", port.Sent.Last());
    });

    [Fact]
    public void An_NPC_is_told_by_its_object_id_and_a_player_by_name() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        port.Say(new RetailChatLine(RetailChatKind.TellIn, "Professor tells you, \"which level?\"", Stamp, "Professor", SenderId: 0x7A9B4012));
        port.Say(new RetailChatLine(RetailChatKind.TellIn, "Mirra tells you, \"hi\"", Stamp, "Mirra", SenderId: 0x50000004));
        panel.Tick();

        ClickName(panel, "Professor tells you", "Professor");
        TypeAndEnter(panel, window, "5");
        Assert.Equal("direct 0x7A9B4012 5", port.Sent.Last());
        // A typed command still goes as typed.
        TypeAndEnter(panel, window, "/e waves");
        Assert.Equal("/e waves", port.Sent.Last());

        ClickName(panel, "Mirra tells you", "Mirra");
        TypeAndEnter(panel, window, "yo");
        Assert.Equal("/t Mirra, yo", port.Sent.Last());
    });

    [Fact]
    public void A_line_draws_in_retail_s_colour_for_its_chat_type() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out _);
        port.Say(new RetailChatLine(RetailChatKind.Channel, "[General] Saro says, \"hi\"", Stamp, Type: 27));
        port.Say(new RetailChatLine(RetailChatKind.Channel, "Saro says to Healers, \"heal\"", Stamp, Type: 8));
        panel.Tick();

        Assert.Equal(Color.Parse("#B4DCF0"), Ink(LineOf(panel, "[General] Saro")));
        Assert.Equal(Color.Parse("#FF9696"), Ink(LineOf(panel, "to Healers")));

        static Color Ink(TextBlock line) => ((SolidColorBrush)line.Inlines!.OfType<Run>().Last().Foreground!).Color;
    });

    [Fact]
    public void A_tell_tag_s_object_id_names_the_sender_even_past_0x7FFFFFFF() =>
        Assert.Equal(0x80000123u, LegACEy.Client.DecalPlugin.NativeRetailChatPort.RetailChatTypes.Parse(RetailChatKind.TellIn,
            "<Tell:IIDString:-2147483357:Guard>Guard<\\Tell> tells you, \"halt\"", Stamp).SenderId);

    [Fact]
    public void A_drag_across_a_name_selects_text_and_does_not_answer_with_a_tell() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out _);
        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"hi\"", "Mirra"));
        panel.Tick();

        var press = NameLetter(panel, "Mirra tells you", "Mirra");
        panel.PointerDown(press.X, press.Y);
        panel.PointerMove(press.X + 40, press.Y);
        panel.PointerUp(press.X + 40, press.Y);
        panel.PointerLeave();
        panel.Tick();
        Assert.Equal("Local", ChipText(panel, "Local").Text);
    });

    [Fact]
    public void Timestamps_follow_the_game_option_and_are_on_by_default() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        port.Say(Line(RetailChatKind.System, "Welcome to Asheron's Call."));
        panel.Tick();
        Assert.True(Shows(panel, "21:02"));

        port.SetTimestamps(false);
        panel.Tick();
        Assert.False(Shows(panel, "21:02"));
        Assert.True(Shows(panel, "Welcome to Asheron"));

        port.SetTimestamps(true);
        panel.Tick();
        Assert.True(Shows(panel, "21:02"));
    });

    [Fact]
    public void A_player_with_timestamps_off_sees_none_from_the_first_line() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort { TimestampsOn = false };
        using var panel = Open(port, out _);
        port.Say(Line(RetailChatKind.System, "Welcome to Asheron's Call."));
        panel.Tick();

        Assert.True(Shows(panel, "Welcome to Asheron"));
        Assert.False(Shows(panel, "21:02"));
    });

    [Fact]
    public void Allegiance_and_Olthoi_are_dimmed_like_the_other_rooms_when_turbine_chat_is_down() => RenderThread.Run(() =>
    {
        // Turbine chat is down: no room is open, though the player is in an allegiance with a monarch, and is an Olthoi.
        var port = new FakeChatPort { Membership = Alone with { Allegiance = true, Monarch = true, Olthoi = true, Open = ChatChannels.None } };
        using var panel = Open(port, out _);
        Click(panel, ChipText(panel, "Local"));
        panel.Tick();

        Assert.Equal("channel is off", MenuNote(panel, "Allegiance"));
        Assert.Equal("channel is off", MenuNote(panel, "Olthoi"));
        Assert.Null(MenuNote(panel, "Monarch"));

        // Picking a dimmed room does nothing; the monarch, who is not a room, is still picked.
        Click(panel, MenuLabel(panel, "Allegiance"));
        Assert.Equal("Local", ChipText(panel, "Local").Text);
        Click(panel, MenuLabel(panel, "Monarch"));
        Assert.Equal("Monarch", ChipText(panel, "Monarch").Text);
    });

    [Fact]
    public void The_Tells_tab_with_no_tell_yet_sends_nothing_until_a_name_is_picked() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort { Membership = Alone with { Fellowship = true } };
        using var panel = Open(port, out var window);

        // Fellowship first: its target must not carry over to the Tells tab.
        Click(panel, TabView(window, "Fellowship"));
        Click(panel, TabView(window, "Tells"));
        panel.Tick();
        Assert.Equal("Tell…", ChipText(panel, "Tell…").Text);
        TypeAndEnter(panel, window, "hi");
        Assert.Empty(port.Sent);
        Assert.Equal("hi", InputBox(window).Text);

        // A tell arrives; a click on its name names the target, and the waiting text goes to that name.
        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"there?\"", "Mirra"));
        ClickName(panel, "Mirra tells you", "Mirra");
        panel.KeyDown(Key.Enter);
        Assert.Equal("/t Mirra, hi", port.Sent.Single());
    });

    [Fact]
    public void Enter_in_a_blank_box_leaves_it_and_sends_nothing() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        Click(panel, InputBox(window));
        panel.Tick();
        Assert.True(panel.WantsKeyboard);

        panel.KeyDown(Key.Enter);
        panel.Tick();

        Assert.False(panel.WantsKeyboard);
        Assert.Empty(port.Sent);
    });

    [Fact]
    public void Sending_a_line_leaves_the_box_so_the_next_line_starts_with_Enter() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        TypeAndEnter(panel, window, "hello");
        panel.Tick();

        Assert.Equal("hello", port.Sent.Last());
        Assert.False(panel.WantsKeyboard);
        Assert.Equal(string.Empty, InputBox(window).Text);
    });

    [Fact]
    public void Selecting_log_text_copies_it_and_a_right_click_over_it_copies_it_again() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        var copied = new List<string>();
        using var panel = Open(port, out _, copy: copied.Add);
        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"still need the Vault key?\"", "Mirra"));
        panel.Tick();

        // Nothing selected: a right click copies nothing.
        var press = NameLetter(panel, "Mirra tells you", "Mirra");
        panel.RightClick(press.X, press.Y);
        Assert.Empty(copied);

        // A drag across the name selects it and copies it when the button goes up; a right click over it copies it again.
        panel.PointerDown(press.X, press.Y);
        panel.PointerMove(press.X + 40, press.Y);
        panel.PointerUp(press.X + 40, press.Y);
        var text = Assert.Single(copied);
        Assert.StartsWith("Mir", text);

        panel.RightClick(press.X + 20, press.Y);
        Assert.Equal(new[] { text, text }, copied);
    });

    [Fact]
    public void A_drag_selects_across_lines_and_Ctrl_C_in_the_box_copies_it() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        var copied = new List<string>();
        using var panel = Open(port, out var window, copy: copied.Add);
        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"still need the Vault key?\"", "Mirra"));
        port.Say(Line(RetailChatKind.TellIn, "Thorne tells you, \"meet at Yaraq\"", "Thorne"));

        // From Mirra's name, down past the end of Thorne's line: the rest of the first line and all of the second, its time included.
        var from = NameLetter(panel, "Mirra tells you", "Mirra");
        var to = NameLetter(panel, "Thorne tells you", "Thorne");
        panel.PointerDown(from.X, from.Y);
        panel.PointerMove(from.X + 20, to.Y);
        panel.PointerMove(Width - 20, to.Y);
        panel.PointerUp(Width - 20, to.Y);
        var text = Assert.Single(copied);
        Assert.Equal("Mirra tells you, \"still need the Vault key?\"\r\n" + Stamp.ToString("HH:mm") + " Thorne tells you, \"meet at Yaraq\"", text);
        Assert.Equal(text, window.LogSelection);

        // Ctrl+C in the box, with nothing selected there, copies the log's selection; a click in the log lets it go.
        Click(panel, InputBox(window));
        panel.KeyDown(Key.C, KeyModifiers.Control);
        Assert.Equal(new[] { text, text }, copied);
        panel.PointerDown(from.X, from.Y);
        panel.PointerUp(from.X, from.Y);
        Assert.Equal(string.Empty, window.LogSelection);
    });

    [Fact]
    public void The_wheel_over_the_log_scrolls_it() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        // At a text size other than the design size, as the chat windows draw.
        window.TextScale = 1.2;
        for (var n = 1; n <= 40; n++) port.Say(Line(RetailChatKind.System, $"line {n:D4} of the log"));
        panel.Tick();
        var log = window.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => scroll.Content is StackPanel { Orientation: Orientation.Vertical });
        var before = log.Offset.Y;
        var centre = log.TranslatePoint(new Point(log.Bounds.Width / 2, log.Bounds.Height / 2), panel.Content)!.Value;

        panel.MouseWheel(centre.X, centre.Y, 0, 3);
        panel.Tick();

        Assert.True(log.Offset.Y < before, $"offset {before} -> {log.Offset.Y}");
    });

    [Fact]
    public void Scrolled_up_new_lines_raise_the_jump_pill_and_leave_the_log_in_place() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        for (var n = 1; n <= 40; n++) port.Say(Line(RetailChatKind.System, $"line {n:D4} of the log"));
        panel.Tick();
        // Scroll the log to its top: the first line is on screen and the last is not.
        window.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => scroll.Content is StackPanel { Orientation: Orientation.Vertical }).Offset = new Vector(0, 0);
        panel.Tick();
        Assert.True(Shows(panel, "line 0001 of the log"));
        Assert.False(Shows(panel, "line 0040 of the log"));

        port.Say(Line(RetailChatKind.System, "line 0041 of the log"));
        panel.Tick();
        Assert.True(Shows(panel, "line 0001 of the log"));
        Assert.True(Shows(panel, "1 new ↓"));

        Click(panel, PillView(panel));
        panel.Tick();
        Assert.False(Shows(panel, "line 0001 of the log"));
        Assert.True(Shows(panel, "line 0041 of the log"));
        Assert.False(Shows(panel, "new ↓"));
    });

    [Fact]
    public void A_send_that_retail_cannot_take_keeps_the_text_in_the_box() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort { CanSend = false };
        using var panel = Open(port, out var window);
        TypeAndEnter(panel, window, "hi");
        Assert.Empty(port.Sent);
        Assert.Equal("hi", InputBox(window).Text);

        port.CanSend = true;
        panel.KeyDown(Key.Enter);
        Assert.Equal("hi", port.Sent.Single());
        Assert.Equal(string.Empty, InputBox(window).Text);
    });

    [Fact]
    public void The_log_keeps_the_last_1000_lines() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out _);
        for (var n = 1; n <= 1005; n++) port.Say(Line(RetailChatKind.System, $"line {n:D4} of the log"));
        panel.Tick();

        Assert.False(InLog(panel, "line 0005 of the log"));
        Assert.True(InLog(panel, "line 0006 of the log"));
        Assert.True(InLog(panel, "line 1005 of the log"));
    });

    [Fact]
    public void Keys_typed_into_the_box_are_taken_by_the_box_and_never_reach_the_game() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        Click(panel, InputBox(window));
        panel.Tick();

        // The box has the keyboard, so the input router eats the key rather than handing it to the game.
        Assert.True(panel.WantsKeyboard);
        var router = new InputRouterService();
        var surfaces = new[] { new InputSurface("dereth-chat", 0, 0, Width, Height, 0, panel.WantsKeyboard) };
        router.FocusSurface("dereth-chat");
        var route = router.Route(new NativeInputMessage(InputRouterService.WmKeyDown, (nint)'A', 0), surfaces);
        Assert.True(route.Eat);
        Assert.Equal(InputAction.KeyDown, route.Action);
    });

    [Fact]
    public void Other_tabs_count_the_lines_they_take_and_a_visit_clears_the_count() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        Click(panel, TabView(window, "Fellowship"));
        port.Say(Line(RetailChatKind.Local, "Orrin says, \"nice cloak\"", "Orrin"));
        port.Say(Line(RetailChatKind.Emote, "Orrin bows deeply.", "Orrin"));
        port.Say(Line(RetailChatKind.Fellowship, "[Fellowship] Big Dog says, \"pull the next room\"", "Big Dog", "Fellowship"));
        port.Say(Line(RetailChatKind.Allegiance, "[Allegiance] Thorne says, \"monarch is on\"", "Thorne", "Allegiance"));
        panel.Tick();

        // The tab the player is on counts nothing; each other tab counts the lines it shows.
        Assert.Equal("All 4", TabText(window, "All"));
        Assert.Equal("Speech 2", TabText(window, "Speech"));
        Assert.Equal("Fellowship", TabText(window, "Fellowship"));
        Assert.Equal("Allegiance 1", TabText(window, "Allegiance"));
        Assert.Equal("Channels", TabText(window, "Channels"));

        Click(panel, TabView(window, "Speech"));
        panel.Tick();
        Assert.Equal("Speech", TabText(window, "Speech"));
        Assert.Equal("All 4", TabText(window, "All"));
    });

    [Fact]
    public void A_new_tell_from_someone_turns_its_tab_badge_gold_and_a_visit_clears_it() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        Click(panel, TabView(window, "Fellowship"));
        port.Say(Line(RetailChatKind.TellOut, "You tell Mirra, \"yes please\"", "Mirra"));
        panel.Tick();
        // A tell you sent is counted, but it is not a new tell from someone, so the badge keeps the plain colour.
        Assert.Equal("Tells 1", TabText(window, "Tells"));
        Assert.Equal(DerethChatPalette.GoldDark, BadgeFill(window, "Tells"));

        port.Say(Line(RetailChatKind.TellIn, "Mirra tells you, \"still need the Vault key?\"", "Mirra"));
        panel.Tick();
        Assert.Equal("Tells 2", TabText(window, "Tells"));
        Assert.Equal(DerethChatPalette.TellFill, BadgeFill(window, "Tells"));
        // Speech and All count the tell too, but only the tell-only Tells tab goes gold.
        Assert.Equal("Speech 2", TabText(window, "Speech"));
        Assert.Equal(DerethChatPalette.GoldDark, BadgeFill(window, "Speech"));
        Assert.Equal("All 2", TabText(window, "All"));

        Click(panel, TabView(window, "Tells"));
        panel.Tick();
        Assert.Equal("Tells", TabText(window, "Tells"));
        Assert.Equal("Tell Mirra", ChipText(panel, "Tell Mirra").Text);
    });

    [Fact]
    public void The_plus_adds_a_tab_with_its_own_kinds_single_channels_and_default_target() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        Click(panel, TabView(window, "+"));
        panel.Tick();
        Assert.True(Shows(panel, "New tab"));

        // A tab needs a name and at least one kind: Add does nothing until both are there.
        Click(panel, DialogButton(panel, "Add"));
        Assert.True(Shows(panel, "New tab"));
        Assert.Equal(6, Strip(window).Length);

        Click(panel, DialogName(panel));
        panel.TextInput("Trade watch");
        // A new tab starts with Local, Tells, Fellowship, System and Combat on. Switch those off and take only the Trade channel.
        foreach (var kind in new[] { "Local", "Tells", "Fellowship", "System", "Combat" }) Click(panel, DialogText(panel, kind));
        Click(panel, DialogText(panel, "Trade"));
        // Its default target is the Trade channel: eight presses of the target arrow reach it from Local.
        for (var press = 0; press < 8; press++) Click(panel, DialogButton(panel, "›"));
        Click(panel, DialogButton(panel, "Add"));
        panel.Tick();
        Assert.False(Shows(panel, "New tab"));
        Assert.Equal("Trade watch", Strip(window)[^1]);

        port.Say(Line(RetailChatKind.Channel, "[General] Kaelith says, \"anyone running tonight?\"", "Kaelith", "General"));
        port.Say(Line(RetailChatKind.Channel, "[Trade] Orrin says, \"selling a Sword\"", "Orrin", "Trade"));
        port.Say(Line(RetailChatKind.Local, "Orrin says, \"nice cloak\"", "Orrin"));
        Click(panel, TabView(window, "Trade watch"));
        panel.Tick();
        Assert.True(Shows(panel, "selling a Sword"));
        Assert.False(Shows(panel, "anyone running tonight"));
        Assert.False(Shows(panel, "nice cloak"));
        Assert.Equal("Trade", ChipText(panel, "Trade").Text);
    });

    [Fact]
    public void A_tab_is_edited_from_its_right_click_reordered_and_removed_and_All_stays_put() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        RightClick(panel, TabView(window, "All"));
        Assert.False(Shows(panel, "Edit tab"));

        RightClick(panel, TabView(window, "Allegiance"));
        Assert.True(Shows(panel, "Edit tab"));
        Click(panel, DialogButton(panel, "Left"));
        Assert.Equal(new[] { "All", "Speech", "Allegiance", "Fellowship", "Channels", "Tells" }, Strip(window));

        RightClick(panel, TabView(window, "Allegiance"));
        Click(panel, DialogName(panel));
        panel.KeyDown(Key.End);
        panel.TextInput(" chat");
        Click(panel, DialogButton(panel, "Save"));
        Assert.Equal(new[] { "All", "Speech", "Allegiance chat", "Fellowship", "Channels", "Tells" }, Strip(window));

        RightClick(panel, TabView(window, "Allegiance chat"));
        Click(panel, DialogButton(panel, "Remove"));
        Assert.Equal(new[] { "All", "Speech", "Fellowship", "Channels", "Tells" }, Strip(window));
    });

    [Fact]
    public void Escape_closes_the_tab_dialog_and_keeps_the_tabs() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        RightClick(panel, TabView(window, "Allegiance"));
        Click(panel, DialogName(panel));
        panel.KeyDown(Key.Escape);
        panel.Tick();
        Assert.False(Shows(panel, "Edit tab"));
        Assert.Equal(new[] { "All", "Speech", "Fellowship", "Allegiance", "Channels", "Tells" }, Strip(window));
    });

    [Fact]
    public void A_damaged_row_for_one_character_leaves_the_other_characters_tabs_alone() => RenderThread.Run(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "legacey-chat-tabs-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var port = new FakeChatPort();
            using (var panel = Open(port, out var window, new FileRetailChatTabStore(path), "Alice"))
                AddTab(panel, window, "Hunting");

            // Bob's row is damaged: its tab list is not base64, so it cannot be read.
            File.AppendAllText(path, Convert.ToBase64String(Encoding.UTF8.GetBytes(Server)) + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Bob"))
                + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(RetailChatWindows.MainId)) + "|not tabs\n");
            // A line that is not a row, and a row whose window id is not base64, are not windows: they stay in the file as they are.
            var unreadable = "a line this store does not read";
            var badWindow = Convert.ToBase64String(Encoding.UTF8.GetBytes(Server)) + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Alice"))
                + "|not base64!|" + Convert.ToBase64String(Encoding.UTF8.GetBytes("x"));
            File.AppendAllText(path, unreadable + "\n" + badWindow + "\n");
            Assert.Equal(new[] { RetailChatWindows.MainId }, new FileRetailChatTabStore(path).Windows(Server, "Alice"));
            using (var panel = Open(port, out var window, new FileRetailChatTabStore(path), "Bob"))
            {
                Assert.Equal(new[] { "All", "Speech", "Fellowship", "Allegiance", "Channels", "Tells" }, Strip(window));
                AddTab(panel, window, "Trade watch");
            }

            // Saving Bob's tabs keeps Alice's row, and Bob's new tabs are there after a restart.
            using (var panel = Open(port, out var window, new FileRetailChatTabStore(path), "Alice"))
                Assert.Equal(new[] { "All", "Speech", "Fellowship", "Allegiance", "Channels", "Tells", "Hunting" }, Strip(window));
            using (var panel = Open(port, out var window, new FileRetailChatTabStore(path), "Bob"))
                Assert.Equal(new[] { "All", "Speech", "Fellowship", "Allegiance", "Channels", "Tells", "Trade watch" }, Strip(window));

            // The lines the store does not read are still in the file, as they were written.
            var lines = File.ReadAllLines(path);
            Assert.Contains(unreadable, lines);
            Assert.Contains(badWindow, lines);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void Tabs_are_kept_per_character_and_restored_after_a_restart() => RenderThread.Run(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "legacey-chat-tabs-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var port = new FakeChatPort();
            using (var panel = Open(port, out var window, new FileRetailChatTabStore(path), "Alice"))
            {
                AddTab(panel, window, "Hunting");
                RightClick(panel, TabView(window, "Channels"));
                Click(panel, DialogButton(panel, "Remove"));
            }

            // A restart: a new store and window on the same file restore Alice's tabs.
            using (var panel = Open(port, out var window, new FileRetailChatTabStore(path), "Alice"))
                Assert.Equal(new[] { "All", "Speech", "Fellowship", "Allegiance", "Tells", "Hunting" }, Strip(window));

            // Bob has no saved tabs, so he has the six defaults.
            using (var panel = Open(port, out var window, new FileRetailChatTabStore(path), "Bob"))
                Assert.Equal(new[] { "All", "Speech", "Fellowship", "Allegiance", "Channels", "Tells" }, Strip(window));
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void A_window_left_alone_goes_idle_and_fades_its_frame_tabs_and_scrollbar_in_a_quarter_second_and_keeps_its_input_and_new_lines() => RenderThread.Run(() =>
    {
        var clock = new TestClock();
        var port = new FakeChatPort();
        using var panel = Open(port, out var window, clock: () => clock.Now);
        panel.Tick();
        window.Refresh();
        // Left alone, it shows whole until it goes idle.
        clock.Advance(RetailChatFade.DefaultIdleAfterSeconds);
        for (var n = 1; n <= 30; n++) port.Say(Line(RetailChatKind.System, $"line {n:D2} of the log", at: clock.Now));
        panel.Tick();
        window.Refresh();
        // The first frame is the window's own; the Send button draws its frame too.
        var frame = window.GetVisualDescendants().OfType<DerethFrame>().First();
        var scrollBar = window.GetVisualDescendants().OfType<ScrollBar>().First(bar => bar.Orientation == Orientation.Vertical);
        Assert.Equal(Awake, Seen(scrollBar));

        clock.Advance(0.125);
        window.Refresh();
        Assert.InRange(Seen(TabView(window, "All")), 0.2, 0.8);

        clock.Advance(0.125);
        window.Refresh();
        Assert.Equal(0.0, Seen(frame));
        Assert.Equal(0.0, Seen(TabView(window, "All")));
        Assert.Equal(1.0, Seen(InputBox(window)));
        Assert.Equal(0.0, Seen(scrollBar));
        Assert.Equal(1.0, Seen(LineOf(panel, "line 30 of the log")));
    });

    [Fact]
    public void Hover_typing_or_an_open_menu_brings_an_idle_window_back() => RenderThread.Run(() =>
    {
        var clock = new TestClock();
        var port = new FakeChatPort();
        using var panel = Open(port, out var window, clock: () => clock.Now);
        panel.Tick();
        window.Refresh();
        clock.Advance(RetailChatFade.DefaultIdleAfterSeconds + RetailChatFade.ChromeFadeSeconds);
        window.Refresh();
        Assert.Equal(0.0, Seen(TabView(window, "All")));

        panel.PointerMove(Width / 2, Height / 2);
        window.Refresh();
        Assert.Equal(Awake, Seen(TabView(window, "All")));
        panel.PointerLeave();
        window.Refresh();
        clock.Advance(RetailChatFade.DefaultIdleAfterSeconds + RetailChatFade.ChromeFadeSeconds);
        window.Refresh();
        Assert.Equal(0.0, Seen(TabView(window, "All")));

        Click(panel, InputBox(window));
        clock.Advance(1);
        window.Refresh();
        Assert.Equal(1.0, Seen(InputBox(window)));
        Assert.Equal(Awake, Seen(TabView(window, "All")));

        Click(panel, ChipText(panel, "Local"));
        panel.PointerLeave();
        clock.Advance(1);
        window.Refresh();
        Assert.Equal(Awake, Seen(TabView(window, "All")));
        Click(panel, ChipText(panel, "Local"));
        window.Refresh();
        clock.Advance(RetailChatFade.DefaultIdleAfterSeconds + RetailChatFade.ChromeFadeSeconds);
        window.Refresh();
        Assert.Equal(0.0, Seen(TabView(window, "All")));
    });

    [Fact]
    public void Lines_fade_out_ten_seconds_after_they_arrive_while_idle_and_all_show_while_awake() => RenderThread.Run(() =>
    {
        var clock = new TestClock();
        var port = new FakeChatPort();
        using var panel = Open(port, out var window, clock: () => clock.Now);
        panel.Tick();
        window.Refresh();
        clock.Advance(RetailChatFade.DefaultIdleAfterSeconds);
        port.Say(Line(RetailChatKind.System, "first line of the log", at: clock.Now));
        clock.Advance(9);
        port.Say(Line(RetailChatKind.System, "second line of the log", at: clock.Now));
        clock.Advance(2);
        window.Refresh();
        Assert.Equal(0.0, Seen(LineOf(panel, "first line")));
        Assert.Equal(1.0, Seen(LineOf(panel, "second line")));

        panel.PointerMove(Width / 2, Height / 2);
        window.Refresh();
        Assert.Equal(1.0, Seen(LineOf(panel, "first line")));
    });

    [Fact]
    public void Fading_is_off_while_the_host_keeps_the_window_awake() => RenderThread.Run(() =>
    {
        var clock = new TestClock();
        using var panel = Open(new FakeChatPort(), out var window, clock: () => clock.Now);
        panel.Tick();
        clock.Advance(5);
        // Held by the host, the window shows whole, whatever its active opacity.
        window.Refresh(keepAwake: true);
        Assert.Equal(1.0, Seen(TabView(window, "All")));

        window.Refresh();
        clock.Advance(RetailChatFade.DefaultIdleAfterSeconds + RetailChatFade.ChromeFadeSeconds);
        window.Refresh();
        Assert.Equal(0.0, Seen(TabView(window, "All")));
    });

    [Fact]
    public void The_active_window_shows_whole_until_it_is_no_longer_active() => RenderThread.Run(() =>
    {
        var clock = new TestClock();
        using var panel = Open(new FakeChatPort(), out var window, clock: () => clock.Now);
        panel.Tick();
        window.Active = true;
        clock.Advance(RetailChatFade.DefaultIdleAfterSeconds + RetailChatFade.ChromeFadeSeconds);
        window.Refresh();
        Assert.Equal(1.0, Seen(TabView(window, "All")));

        window.Active = false;
        window.Refresh();
        clock.Advance(RetailChatFade.DefaultIdleAfterSeconds + RetailChatFade.ChromeFadeSeconds);
        window.Refresh();
        Assert.Equal(0.0, Seen(TabView(window, "All")));
    });

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public void A_window_fades_once_it_has_been_left_alone_for_its_chosen_delay(int seconds) => RenderThread.Run(() =>
    {
        var clock = new TestClock();
        var port = new FakeChatPort();
        using var panel = Open(port, out var window, clock: () => clock.Now);
        window.IdleAfterSeconds = seconds;
        panel.Tick();
        window.Refresh();
        clock.Advance(seconds - 1);
        window.Refresh();
        Assert.Equal(Awake, Seen(TabView(window, "All")));

        clock.Advance(1 + RetailChatFade.ChromeFadeSeconds);
        window.Refresh();
        Assert.Equal(0.0, Seen(TabView(window, "All")));
    });

    [Fact]
    public void Changing_the_delay_while_idle_applies_the_rule_at_once_so_lines_faded_under_the_old_delay_fade_again_from_the_new_moment() => RenderThread.Run(() =>
    {
        var clock = new TestClock();
        var port = new FakeChatPort();
        using var panel = Open(port, out var window, clock: () => clock.Now);
        window.IdleAfterSeconds = 10;
        port.Say(Line(RetailChatKind.System, "an old line of the log", at: clock.Now));
        panel.Tick();
        window.Refresh();
        clock.Advance(10.5);
        window.Refresh();
        Assert.Equal(0.0, Seen(LineOf(panel, "an old line")));

        // Still idle, the delay goes to 15: the window's idle moment is now 15 seconds in, 0.1 s before this frame, so the line and the
        // chrome fade from it again (0.75 and 0.6 of awake), not stay gone as they were under 10.
        window.IdleAfterSeconds = 15;
        clock.Advance(4.6);
        window.Refresh();
        Assert.Equal(0.75, Seen(LineOf(panel, "an old line")), 2);
        Assert.Equal(0.6 * Awake, Seen(TabView(window, "All")), 2);
    });

    [Fact]
    public void Never_keeps_a_window_fully_shown_however_long_it_is_left_alone_and_a_change_to_it_shows_an_idle_window_at_once() => RenderThread.Run(() =>
    {
        var clock = new TestClock();
        var port = new FakeChatPort();
        using var panel = Open(port, out var window, clock: () => clock.Now);
        panel.Tick();
        window.Refresh();
        clock.Advance(RetailChatFade.DefaultIdleAfterSeconds + RetailChatFade.ChromeFadeSeconds);
        window.Refresh();
        Assert.Equal(0.0, Seen(TabView(window, "All")));

        window.IdleAfterSeconds = null;
        window.Refresh();
        Assert.Equal(Awake, Seen(TabView(window, "All")));
        clock.Advance(3600);
        window.Refresh();
        Assert.Equal(Awake, Seen(TabView(window, "All")));
    });

    // ---- helpers ----

    /// <summary>How much of an awake window's frame, tabs and input shows at the default active opacity.</summary>
    internal const double Awake = RetailChatWindows.DefaultActiveOpacity / 100.0;

    private const string Server = "server";

    /// <summary>The window's clock: the test moves it, so the fade runs without sleeping.</summary>
    private sealed class TestClock
    {
        public DateTime Now { get; private set; } = Stamp;

        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    /// <summary>How opaque a part shows on screen: its opacity times its ancestors'.</summary>
    private static double Seen(Visual part) => part.GetSelfAndVisualAncestors().OfType<Visual>().Aggregate(1.0, (seen, visual) => seen * visual.Opacity);

    private static TextBlock LineOf(AvaloniaPanel panel, string fragment) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().First(text => TextOf(text).Contains(fragment));

    private static readonly RetailChatMembership Alone = new(Fellowship: false, Allegiance: false, Monarch: false, Patron: false, Vassals: false,
        Society: false, Olthoi: false, Open: ChatChannels.General | ChatChannels.Trade | ChatChannels.Lfg | ChatChannels.Roleplay);

    private static readonly string[] SampleFragments =
    {
        "nice cloak", "bows deeply", "still need", "yes please", "pull the next", "monarch is on", "anyone running", "Welcome", "Disconnected", "42 points"
    };

    private static IEnumerable<RetailChatLine> SampleLines()
    {
        yield return Line(RetailChatKind.Local, "Orrin says, \"nice cloak\"", "Orrin");
        yield return Line(RetailChatKind.Emote, "Orrin bows deeply.", "Orrin");
        yield return Line(RetailChatKind.TellIn, "Mirra tells you, \"still need the Vault key?\"", "Mirra");
        yield return Line(RetailChatKind.TellOut, "You tell Mirra, \"yes please\"", "Mirra");
        yield return Line(RetailChatKind.Fellowship, "[Fellowship] Big Dog says, \"pull the next room\"", "Big Dog", "Fellowship");
        yield return Line(RetailChatKind.Allegiance, "[Allegiance] Thorne says, \"monarch is on\"", "Thorne", "Allegiance");
        yield return Line(RetailChatKind.Channel, "[General] Kaelith says, \"anyone running tonight?\"", "Kaelith", "General");
        yield return Line(RetailChatKind.System, "Welcome to Asheron's Call.");
        yield return Line(RetailChatKind.Plugin, "[VI] Disconnected, retry in 470 seconds.");
        yield return Line(RetailChatKind.Combat, "You hit the Drudge Skulker for 42 points of slashing damage!");
    }

    private static RetailChatLine Line(RetailChatKind kind, string text, string? sender = null, string? channel = null, DateTime? at = null) =>
        new(kind, text, at ?? Stamp, sender, channel);

    private static AvaloniaPanel Open(FakeChatPort port, out RetailChatWindow window, FileRetailChatTabStore? store = null, string character = "Alice",
        Func<DateTime>? clock = null, Action<string>? copy = null, Func<string?>? paste = null)
    {
        RetailChatWindow? created = null;
        var tabs = store?.Load(Server, character, RetailChatWindows.MainId);
        var history = new RetailChatHistory(port);
        var panel = AvaloniaPanel.Create(() => created = new RetailChatWindow(port, history, new SingleWindowHost(store, character), tabs,
            clock: clock ?? (() => Stamp), copy: copy, paste: paste), Width, Height);
        window = created!;
        return panel;
    }

    /// <summary>The tab's label on the tab strip: the first label with that text, the strip being first in the window.</summary>
    private static TextBlock TabView(RetailChatWindow window, string name) =>
        window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == name);

    /// <summary>The tab as the player reads it: its name, then its unread count when it has one.</summary>
    private static string TabText(RetailChatWindow window, string name) => TabText(TabBorder(window, name));

    private static string TabText(Border tab) => string.Join(" ", tab.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));

    private static Border TabBorder(RetailChatWindow window, string name) => TabView(window, name).GetVisualAncestors().OfType<Border>().First();

    /// <summary>The tab names left to right, as the strip shows them: the plus is not a tab.</summary>
    private static string[] Strip(RetailChatWindow window) =>
        ((StackPanel)TabBorder(window, "All").GetVisualParent()!).Children.OfType<Border>().Select(TabText).Where(text => text != "+").ToArray();

    /// <summary>The fill of a tab's unread badge: the badge is the only border in a tab with a text child.</summary>
    private static Color BadgeFill(RetailChatWindow window, string name) =>
        ((SolidColorBrush)TabBorder(window, name).GetVisualDescendants().OfType<Border>().Single().Background!).Color;

    /// <summary>The dialog for a new or an edited tab, found from its title: the first border above the title holds the dialog's controls.</summary>
    private static Border Dialog(AvaloniaPanel panel) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text is "New tab" or "Edit tab").GetVisualAncestors().OfType<Border>().First();

    private static TextBlock DialogText(AvaloniaPanel panel, string text) => Dialog(panel).GetVisualDescendants().OfType<TextBlock>().First(view => view.Text == text);

    private static Button DialogButton(AvaloniaPanel panel, string content) => Dialog(panel).GetVisualDescendants().OfType<Button>().First(button => button.Content as string == content);

    private static TextBox DialogName(AvaloniaPanel panel) => Dialog(panel).GetVisualDescendants().OfType<TextBox>().Single();

    private static void AddTab(AvaloniaPanel panel, RetailChatWindow window, string name)
    {
        Click(panel, TabView(window, "+"));
        Click(panel, DialogName(panel));
        panel.TextInput(name);
        Click(panel, DialogButton(panel, "Add"));
    }

    private static void RightClick(AvaloniaPanel panel, Control control)
    {
        panel.Tick();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;
        panel.RightClick(centre.X, centre.Y);
        panel.PointerLeave();
    }

    private static TextBlock ChipText(AvaloniaPanel panel, string label) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == label);

    private static TextBlock MenuItem(AvaloniaPanel panel, string label) => ChipText(panel, label);

    /// <summary>A target's label in the open menu: a menu row starts with its colour dot, which sets it apart from a tab of the same name.</summary>
    private static TextBlock MenuLabel(AvaloniaPanel panel, string label) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == label && text.GetVisualParent() is Grid row
            && row.Children.OfType<Avalonia.Controls.Shapes.Ellipse>().Any());

    /// <summary>The note beside a target in the open menu (why it is dimmed), or null when it has none.</summary>
    private static string? MenuNote(AvaloniaPanel panel, string label) =>
        ((Grid)MenuLabel(panel, label).GetVisualParent()!).Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text;

    private static Control PillView(AvaloniaPanel panel) =>
        panel.Content.GetVisualDescendants().OfType<Border>().First(border => border.Child is TextBlock { Text: { } t } && t.Contains("new"));

    /// <summary>The text a text block shows: its runs when it has them (a log line is one block of runs), its text otherwise.</summary>
    private static string TextOf(TextBlock text) =>
        text.Inlines is { Count: > 0 } runs ? string.Concat(runs.OfType<Run>().Select(run => run.Text)) : text.Text ?? string.Empty;

    /// <summary>Whether text the player can see is on screen: a visible text block whose text contains the fragment, and that lies in view.</summary>
    private static bool Shows(AvaloniaPanel panel, string fragment) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && TextOf(text).Contains(fragment) && InView(panel, text));

    /// <summary>In view: the block overlaps the window and every scroll viewport it sits in. Scrolled-out lines are still in the tree.</summary>
    private static bool InView(AvaloniaPanel panel, TextBlock text)
    {
        var origin = text.TranslatePoint(default, panel.Content);
        if (origin == null) return false;
        var rect = new Rect(origin.Value, text.Bounds.Size);
        if (!rect.Intersects(new Rect(panel.Content.Bounds.Size))) return false;
        foreach (var scroll in text.GetVisualAncestors().OfType<ScrollViewer>())
        {
            var top = scroll.TranslatePoint(default, panel.Content);
            if (top == null || !rect.Intersects(new Rect(top.Value, scroll.Bounds.Size))) return false;
        }
        return true;
    }

    /// <summary>The message box, found by its watermark: it fails loudly if the box moves or changes.</summary>
    private static TextBox InputBox(RetailChatWindow window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "Say something…");

    /// <summary>The point of a name's first letter in a log line, where the player presses it.</summary>
    private static Point NameLetter(AvaloniaPanel panel, string lineStart, string name)
    {
        panel.Tick();
        var line = panel.Content.GetVisualDescendants().OfType<SelectableTextBlock>().First(text => TextOf(text).Contains(lineStart));
        var letter = line.TextLayout.HitTestTextPosition(TextOf(line).IndexOf(name, StringComparison.Ordinal));
        return line.TranslatePoint(new Point(letter.Left + 1, letter.Center.Y), panel.Content)!.Value;
    }

    /// <summary>A click on a name in a log line: a press and release where the name is.</summary>
    private static void ClickName(AvaloniaPanel panel, string lineStart, string name)
    {
        var point = NameLetter(panel, lineStart, name);
        panel.PointerDown(point.X, point.Y);
        panel.PointerUp(point.X, point.Y);
        panel.PointerLeave();
    }

    /// <summary>Whether the log holds a line with the fragment: on screen, or scrolled out of view and one scroll away.</summary>
    private static bool InLog(AvaloniaPanel panel, string fragment) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && TextOf(text).Contains(fragment));

    private static IEnumerable<string> ShownFragments(AvaloniaPanel panel, IEnumerable<string> fragments) =>
        fragments.Where(fragment => InLog(panel, fragment));

    private static void Click(AvaloniaPanel panel, Control control)
    {
        panel.Tick();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
        panel.PointerLeave();
    }

    private static void TypeAndEnter(AvaloniaPanel panel, RetailChatWindow window, string text)
    {
        Click(panel, InputBox(window));
        panel.TextInput(text);
        panel.KeyDown(Key.Enter);
    }

    private static void TypeAndSend(AvaloniaPanel panel, RetailChatWindow window, string text)
    {
        Click(panel, InputBox(window));
        panel.TextInput(text);
        Click(panel, panel.Content.GetVisualDescendants().OfType<Button>().First(button => (button.Content as TextBlock)?.Text == "Send"));
    }

    /// <summary>The host of a window on its own: its tab list is kept under the main window's id; the other requests are not used.</summary>
    private sealed class SingleWindowHost : IRetailChatWindowHost
    {
        private readonly FileRetailChatTabStore? _store;
        private readonly string _character;

        public SingleWindowHost(FileRetailChatTabStore? store, string character)
        {
            _store = store;
            _character = character;
        }

        public void TabsChanged(RetailChatWindow window) => _store?.Save(Server, _character, RetailChatWindows.MainId, window.Tabs);

        public void TabDraggedOut(RetailChatWindow window, int index) { }

        public void MoveRequested(RetailChatWindow window) { }

        public void CloseRequested(RetailChatWindow window) { }
    }

    /// <summary>A fake of retail's chat: lines are raised by the test, sends and timestamp changes are recorded.</summary>
    internal sealed class FakeChatPort : IRetailChatPort
    {
        public List<string> Sent { get; } = new();
        public RetailChatMembership Membership { get; set; } = Alone;
        public string? SelectedName { get; set; }
        public bool TimestampsOn { get; set; } = true;
        public event Action<RetailChatLine>? LineReceived;
        public event Action? TimestampsChanged;

        /// <summary>False makes the send fail, as when retail's chat has no receiver: nothing is recorded.</summary>
        public bool CanSend { get; set; } = true;

        public bool Send(string text)
        {
            if (!CanSend) return false;
            Sent.Add(text);
            return true;
        }

        public uint SelectedId { get; set; }

        /// <summary>A direct tell is recorded as "direct 0xID text".</summary>
        public bool TellDirect(uint objectId, string text)
        {
            if (!CanSend) return false;
            Sent.Add($"direct 0x{objectId:X8} {text}");
            return true;
        }

        public void Say(RetailChatLine line) => LineReceived?.Invoke(line);

        /// <summary>Each link click, as Decal's ChatNameClicked would get it.</summary>
        public List<(uint Id, string Tag)> Clicks { get; } = new();

        /// <summary>True makes every click eaten, as a plugin that takes it does.</summary>
        public bool Eats { get; set; }

        public bool Click(uint id, string tag)
        {
            Clicks.Add((id, tag));
            return Eats;
        }

        public void SetTimestamps(bool on)
        {
            TimestampsOn = on;
            TimestampsChanged?.Invoke();
        }
    }
}
