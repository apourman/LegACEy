using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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
        Assert.Equal("fellows", window.Target.Id);
        var chip = ChipText(panel, "Fellows");
        Assert.Equal(DerethChatPalette.Fellowship, ((SolidColorBrush)chip.Foreground!).Color);

        Click(panel, TabView(window, "Tells"));
        panel.Tick();
        Assert.Equal("tell:Mirra", window.Target.Id);
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
        Assert.Equal("local", window.Target.Id);
        Click(panel, MenuItem(panel, "General"));
        panel.Tick();
        Assert.Equal("general", window.Target.Id);
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
        Assert.Equal("/s hi", port.Sent.Last());

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

        Click(panel, NameView(panel, "Mirra"));
        Assert.Equal("tell:Mirra", window.Target.Id);
        TypeAndEnter(panel, window, "yo");
        Assert.Equal("/t Mirra, yo", port.Sent.Last());

        // Tab in the empty box answers the last tell, whoever it was from.
        Click(panel, TabView(window, "Speech"));
        port.Say(Line(RetailChatKind.TellIn, "Saro tells you, \"ready\"", "Saro"));
        Click(panel, window.Input);
        panel.KeyDown(Key.Tab);
        Assert.Equal("tell:Saro", window.Target.Id);
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
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(); // the log; the text box has its own in its template
        scroll.Offset = new Vector(0, 0);
        panel.Tick();

        port.Say(Line(RetailChatKind.System, "line 0041 of the log"));
        panel.Tick();
        Assert.Equal(0, scroll.Offset.Y);
        Assert.True(Shows(panel, "1 new"));
        Assert.Equal(1, window.MissedLines);

        Click(panel, PillView(panel));
        panel.Tick();
        Assert.False(Shows(panel, "new ↓"));
        Assert.True(scroll.Offset.Y > 0);
    });

    [Fact]
    public void The_log_keeps_the_last_1000_lines() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out _);
        for (var n = 1; n <= 1005; n++) port.Say(Line(RetailChatKind.System, $"line {n:D4} of the log"));
        panel.Tick();

        Assert.False(Shows(panel, "line 0005 of the log"));
        Assert.True(Shows(panel, "line 0006 of the log"));
        Assert.True(Shows(panel, "line 1005 of the log"));
    });

    [Fact]
    public void Keys_typed_into_the_box_are_taken_by_the_box_and_never_reach_the_game() => RenderThread.Run(() =>
    {
        var port = new FakeChatPort();
        using var panel = Open(port, out var window);
        Click(panel, window.Input);
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

    // ---- helpers ----

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

    private static AvaloniaPanel Open(FakeChatPort port, out RetailChatWindow window)
    {
        RetailChatWindow? created = null;
        var panel = AvaloniaPanel.Create(() => created = new RetailChatWindow(port), Width, Height);
        window = created!;
        return panel;
    }

    /// <summary>The tab's label on the tab strip: the first label with that text, the strip being first in the window.</summary>
    private static TextBlock TabView(RetailChatWindow window, string name) =>
        window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == name);

    private static TextBlock ChipText(AvaloniaPanel panel, string label) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == label);

    private static TextBlock MenuItem(AvaloniaPanel panel, string label) => ChipText(panel, label);

    private static TextBlock NameView(AvaloniaPanel panel, string name) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Text == name && text.Cursor != null).Last();

    private static Control PillView(AvaloniaPanel panel) =>
        panel.Content.GetVisualDescendants().OfType<Border>().First(border => border.Child is TextBlock { Text: { } t } && t.Contains("new"));

    /// <summary>Whether text the player can see is on screen: a visible text block whose text contains the fragment.</summary>
    private static bool Shows(AvaloniaPanel panel, string fragment) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text?.Contains(fragment) == true)
        || panel.Content.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.IsEffectivelyVisible && text.Text?.Contains(fragment) == true);

    private static IEnumerable<string> ShownFragments(AvaloniaPanel panel, IEnumerable<string> fragments) =>
        fragments.Where(fragment => Shows(panel, fragment));

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
        Click(panel, window.Input);
        panel.TextInput(text);
        panel.KeyDown(Key.Enter);
    }

    private static void TypeAndSend(AvaloniaPanel panel, RetailChatWindow window, string text)
    {
        Click(panel, window.Input);
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

        public void Send(string text) => Sent.Add(text);

        public void Say(RetailChatLine line) => LineReceived?.Invoke(line);

        public void SetTimestamps(bool on)
        {
            TimestampsOn = on;
            TimestampsChanged?.Invoke();
        }
    }
}
