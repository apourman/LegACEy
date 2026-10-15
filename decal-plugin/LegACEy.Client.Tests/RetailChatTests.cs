using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
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
    public void Scrolled_up_new_lines_raise_the_jump_pill_and_leave_the_log_in_place() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        for (var n = 1; n <= 40; n++) port.Say(Line(RetailChatKind.System, $"line {n:D4} of the log"));
        panel.Tick();
        // Scroll the log to its top: the first line is on screen and the last is not.
        window.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = new Vector(0, 0);
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

    // ---- helpers ----

    private const string Server = "server";

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

    private static RetailChatLine Line(RetailChatKind kind, string text, string? sender = null, string? channel = null) =>
        new(kind, text, Stamp, sender, channel);

    private static AvaloniaPanel Open(FakeChatPort port, out RetailChatWindow window, FileRetailChatTabStore? store = null, string character = "Alice")
    {
        RetailChatWindow? created = null;
        var tabs = store?.Load(Server, character);
        var panel = AvaloniaPanel.Create(() => created = new RetailChatWindow(port, null, tabs, saved => store?.Save(Server, character, saved)), Width, Height);
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

    /// <summary>The message box: the first text box, as the input row comes before any dialog in the window.</summary>
    private static TextBox InputBox(RetailChatWindow window) => window.GetVisualDescendants().OfType<TextBox>().First();

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
        Click(panel, panel.Content.GetVisualDescendants().OfType<Button>().First(button => button.Content as string == "Send"));
    }

    /// <summary>A fake of retail's chat: lines are raised by the test, sends and timestamp changes are recorded.</summary>
    private sealed class FakeChatPort : IRetailChatPort
    {
        public List<string> Sent { get; } = new();
        public RetailChatMembership Membership { get; set; } = Alone;
        public string? SelectedName { get; set; }
        public bool TimestampsOn { get; private set; } = true;
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

        public void Say(RetailChatLine line) => LineReceived?.Invoke(line);

        public void SetTimestamps(bool on)
        {
            TimestampsOn = on;
            TimestampsChanged?.Invoke();
        }
    }
}
