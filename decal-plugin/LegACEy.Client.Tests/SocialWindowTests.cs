using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using Avalonia.LogicalTree;
using LegACEy.Client.PanelHost;
using Xunit;
using Writer = LegACEy.Client.Tests.CharacterWindowTests.Writer;

namespace LegACEy.Client.Tests;

public sealed class SocialWindowTests
{
    private const int GameEvent = 0xF7B0;
    private const uint Me = 0x50000002, Monarch = 0x50000010, Patron = 0x50000011, Vassal = 0x50000020, Pal = 0x50000030, Pest = 0x50000040;

    private static Writer Event(uint type) => new Writer().U32(GameEvent).U32(Me).U32(1).U32(type);

    // AllegianceData as ACE writes it: packed level and allegiance age, so a level and two 32-bit times.
    private static Writer Member(Writer w, uint id, string name, uint produced, bool online, uint level = 20) =>
        w.U32(id).U32(0).U32(produced).U32(0x4 | 0x8 | (online ? 0x1u : 0)).U8(1).U8(1).U16(3).U32(level).U16(0).U16(0).U32(0).U32(0).String(name);

    /// <summary>An AllegianceUpdate as ACE writes it: monarch, patron, the player, and one vassal.</summary>
    private static byte[] Allegiance()
    {
        var w = Event(SocialState.AllegianceUpdateEvent).U32(4).U32(12).U32(7);
        w.U16(4).U16(0x0B).U16(0).U16(256).U32(0).U32(0).U32(0).U32(0).U32(0).String("").String("").U32(9).Bytes(32).String("Order").U32(0).U32(0).U32(0);
        Member(w, Monarch, "Lord Asheron", 0, online: false, level: 126);
        Member(w.U32(Monarch), Patron, "Sir Patron", 9000, online: true);
        Member(w.U32(Patron), Me, "Testerman", 500, online: true);
        Member(w.U32(Me), Vassal, "Squire", 1234, online: false, level: 8);
        return w.ToArray();
    }

    private static Writer Fellow(Writer w, uint id, string name, uint level) =>
        w.U32(id).U32(0).U32(0).U32(level).U32(41).U32(81).U32(19).U32(22).U32(64).U32(19).U32(0x10).String(name);

    private static byte[] FellowshipOf(uint leader, bool open, params (uint Id, string Name, uint Level)[] fellows) => FellowshipOf(leader, open, true, fellows);

    private static byte[] FellowshipOf(uint leader, bool open, bool even, params (uint Id, string Name, uint Level)[] fellows)
    {
        var w = Event(SocialState.FellowshipEvent).U16((ushort)fellows.Length).U16(16);
        foreach (var (id, name, level) in fellows) Fellow(w, id, name, level);
        return w.String("Sda").U32(leader).U32(1).U32(even ? 1u : 0).U32(open ? 1u : 0).U32(0).ToArray();
    }

    private static byte[] Friends(uint kind, params (uint Id, string Name, bool Online)[] friends)
    {
        var w = Event(SocialState.FriendsEvent).U32(friends.Length);
        foreach (var (id, name, online) in friends) w.U32(id).U32(online ? 1u : 0).U32(0).String(name).U32(0).U32(0);
        return w.U32(kind).ToArray();
    }

    private static byte[] Squelches()
    {
        var w = Event(SocialState.SquelchEvent).U16(0).U16(0).U16(2).U16(32);
        w.U32(Pest).U32(4).U32(1).U32(1).U32(1).U32(1).String("Pest").U32(0);
        w.U32(Pal).U32(1).U32(1).String("Spammer").U32(1);
        return w.U32(0).String("").U32(0).ToArray();
    }

    [Fact]
    public void The_state_reads_the_allegiance_with_the_patron_and_vassals()
    {
        var state = new SocialState();
        state.Receive(GameEvent, Allegiance());

        Assert.Equal(4u, state.Rank);
        Assert.Equal(7u, state.Followers);
        Assert.Equal("Lord Asheron", state.Monarch?.Name);
        Assert.Equal(126u, state.Monarch?.Level);
        Assert.Equal(new AllegianceMember(Patron, "Sir Patron", 20, 3, 9000, true), state.Patron);
        Assert.Equal(new[] { "Squire" }, state.Vassals.Select(v => v.Name));
        Assert.Equal(1234u, state.Vassals[0].Produced);

        state.Receive(GameEvent, Event(SocialState.AllegianceLoginEvent).U32(Vassal).U32(1).ToArray());
        Assert.True(state.Vassals[0].Online);
    }

    [Fact]
    public void The_state_follows_the_fellowship_from_the_full_update_to_quitting()
    {
        var state = new SocialState();
        state.Receive(GameEvent, FellowshipOf(Me, open: false, (Me, "Testerman", 5)));
        Assert.Equal("Sda", state.Fellowship);
        Assert.Equal(Me, state.Leader);
        Assert.Equal(new Fellow(Me, "Testerman", 5, 22, 41, 64, 81, 19, 19), state.Fellows.Single());

        state.Receive(GameEvent, Fellow(Event(SocialState.FellowEvent), Pal, "Billdozer", 9).U32(1).ToArray());
        Assert.Equal(new[] { "Testerman", "Billdozer" }, state.Fellows.Select(f => f.Name));

        state.Receive(GameEvent, Event(SocialState.FellowDismissEvent).U32(Pal).ToArray());
        Assert.Equal(new[] { "Testerman" }, state.Fellows.Select(f => f.Name));

        state.Receive(GameEvent, Event(SocialState.FellowQuitEvent).U32(Me).ToArray());
        Assert.Null(state.Fellowship);
        Assert.Empty(state.Fellows);
    }

    [Fact]
    public void The_state_keeps_friends_and_squelches_current()
    {
        var state = new SocialState();
        state.Receive(GameEvent, Friends(0, (Pal, "Billdozer", true)));
        state.Receive(GameEvent, Friends(1, (Pest, "Pest", false)));
        Assert.Equal(new[] { "Billdozer", "Pest" }, state.Friends.Select(f => f.Name));
        state.Receive(GameEvent, Friends(4, (Pal, "Billdozer", false)));
        Assert.False(state.Friends.Single(f => f.Id == Pal).Online);
        state.Receive(GameEvent, Friends(2, (Pest, "Pest", false)));
        Assert.Equal(new[] { "Billdozer" }, state.Friends.Select(f => f.Name));

        state.Receive(GameEvent, Squelches());
        Assert.Equal(new[] { new Squelch(Pest, "Pest", false), new Squelch(Pal, "Spammer", true) }, state.Squelches);
    }

    [Fact]
    public void A_message_cut_short_changes_nothing()
    {
        var state = new SocialState();
        var allegiance = Allegiance();
        state.Receive(GameEvent, allegiance.AsSpan(0, allegiance.Length - 6));
        Assert.Empty(state.Vassals);
    }

    [Fact]
    public void Actions_carry_their_fields_as_the_server_reads_them()
    {
        Assert.Equal(new byte[] { 3, 0, (byte)'B', (byte)'o', (byte)'b', 0, 0, 0 }, SocialAction.AddFriend("Bob").Payload);
        var squelch = SocialAction.SquelchCharacter(true, 0, "Al");
        Assert.Equal(0x0058u, squelch.Opcode);
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 2, 0, (byte)'A', (byte)'l', 1, 0, 0, 0 }, squelch.Payload);
        Assert.Equal(new byte[] { 2, 0, (byte)'F', (byte)'o', 1, 0, 0, 0 }, SocialAction.CreateFellowship("Fo", true).Payload);
    }

    [Fact]
    public void Swear_break_and_kick_ask_for_a_second_press() => RenderThread.Run(() =>
    {
        var state = new SocialState();
        state.Receive(GameEvent, Allegiance());
        var game = new FakeGame { Selection = 0x50000099 };
        SocialWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new SocialWindow(state, game, () => CharacterTables.None)).Window, SocialWindow.Width, SocialWindow.Height);
        panel.Tick();
        Assert.Equal(0x001Fu, game.Sent.Single().Opcode);

        Press(view, "Break");
        Assert.Single(game.Sent);
        Press(view, "Confirm");
        Assert.Equal(SocialAction.Break(Patron).Payload, game.Sent.Last().Payload);

        view.SelectVassal(Vassal);
        Press(view, "Kick");
        Press(view, "Confirm");
        Assert.Equal((0x001Eu, Vassal), (game.Sent.Last().Opcode, BitConverter.ToUInt32(game.Sent.Last().Payload, 0)));
    });

    [Fact]
    public void The_fellowship_tab_creates_one_and_leads_it() => RenderThread.Run(() =>
    {
        var state = new SocialState();
        var game = new FakeGame { Selection = Pal };
        SocialWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new SocialWindow(state, game, () => CharacterTables.None)).Window, SocialWindow.Width, SocialWindow.Height);
        view.ShowTab(1);
        panel.Tick();
        Assert.Equal(SocialAction.FellowshipPanel(true).Payload, game.Sent.Last().Payload);

        view.FellowshipNameBox.Text = "Sda";
        view.CreateFellowship();
        Assert.Equal(SocialAction.CreateFellowship("Sda", true).Payload, game.Sent.Last().Payload);

        state.Receive(GameEvent, FellowshipOf(Me, open: false, (Me, "Testerman", 5), (Pest, "Pest", 50)));
        view.Refresh();
        panel.Tick();
        Assert.Equal(new int?[] { 75, 75 }, view.Shares());
        Press(view, "Recruit");
        Assert.Equal(SocialAction.Recruit(Pal).Payload, game.Sent.Last().Payload);
        view.SelectFellow(Pest);
        Assert.Equal(Pest, game.Selection);
        Press(view, "Leader");
        Assert.Equal(SocialAction.Leader(Pest).Payload, game.Sent.Last().Payload);
        Press(view, "Open");
        Assert.Equal(SocialAction.Openness(true).Payload, game.Sent.Last().Payload);

        // Selecting a fellow in the world selects their row, as in retail.
        view.SelectFellow(0);
        game.Selection = Pal;
        view.Refresh();
        game.Selection = Pest;
        view.Refresh();
        Press(view, "Dismiss");
        Assert.Equal(SocialAction.Dismiss(Pest).Payload, game.Sent.Last().Payload);
    });

    [Fact]
    public void Shares_split_as_the_server_does()
    {
        var state = new SocialState();
        var levels = new ulong[] { 0, 0, 100, 300, 600 };
        var tables = new CharacterTables(new Dictionary<uint, SkillDefinition>(), new CharacterTables.Xp { Level = levels }, new Dictionary<uint, uint>(), new Dictionary<uint, uint>());
        RenderThread.Run(() =>
        {
            var view = new SocialWindow(state, new FakeGame(), () => tables);
            state.Receive(GameEvent, FellowshipOf(Me, false, false, (Me, "A", 1), (Pal, "B", 3)));
            // Uneven: level 1 needs 100 to its next, level 3 needs 300.
            Assert.Equal(new int?[] { 25, 75 }, view.Shares());
        });
    }

    [Fact]
    public void Friends_and_squelches_send_their_actions_and_switches_set_retail_options() => RenderThread.Run(() =>
    {
        var state = new SocialState();
        state.Receive(GameEvent, Friends(0, (Pal, "Billdozer", true)));
        state.Receive(GameEvent, Squelches());
        var game = new FakeGame();
        SocialWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new SocialWindow(state, game, () => CharacterTables.None)).Window, SocialWindow.Width, SocialWindow.Height);
        view.ShowTab(2);
        view.SelectFriend(Pal);
        view.TellFriend();
        Assert.Equal(("Billdozer", Pal), game.Told);
        view.FriendNameBox.Text = "Newpal";
        view.AddFriend();
        Assert.Equal(SocialAction.AddFriend("Newpal").Payload, game.Sent.Last().Payload);
        Assert.Equal(string.Empty, view.FriendNameBox.Text);

        view.ShowTab(3);
        view.SelectSquelch(new Squelch(Pal, "Spammer", true));
        view.RemoveSquelch();
        Assert.Equal(SocialAction.SquelchAccount(false, "Spammer").Payload, game.Sent.Last().Payload);
        view.SquelchNameBox.Text = "Rude";
        view.SquelchTyped(account: false);
        Assert.Equal(SocialAction.SquelchCharacter(true, 0, "Rude").Payload, game.Sent.Last().Payload);
    });

    [Fact]
    public void The_social_window_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_SOCIAL_RENDER names the folder.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_SOCIAL_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        foreach (var (name, tab, inFellowship) in new[] { ("allegiance", 0, false), ("fellowship", 1, false), ("fellowship-in", 1, true), ("friends", 2, false), ("squelch", 3, false) })
        {
            var state = new SocialState();
            state.Receive(GameEvent, Allegiance());
            state.Receive(GameEvent, Friends(0, (Pal, "Billdozer", true), (Pest, "Pest", false)));
            state.Receive(GameEvent, Squelches());
            if (inFellowship) state.Receive(GameEvent, FellowshipOf(Me, false, (Me, "Dgfdzf", 5), (Pal, "Billdozer", 7)));
            SocialWindow view = null!;
            using var panel = AvaloniaPanel.Create(() => (view = new SocialWindow(state, new FakeGame(), () => CharacterTables.None)).Window, SocialWindow.Width, SocialWindow.Height);
            view.ShowTab(tab);
            view.SelectFriend(Pal);
            panel.Tick();
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, name + ".png"));
        }
    });

    private static void Press(SocialWindow view, string text)
    {
        var button = view.Window.GetLogicalDescendants().OfType<LegACEy.Client.Themes.DerethButton>()
            .First(b => b.IsEffectivelyVisible && (b.Content as Avalonia.Controls.TextBlock)?.Text == text);
        Assert.True(button.IsEnabled, $"{text} is disabled");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    }

    private sealed class FakeGame : ISocialGame
    {
        public readonly List<SocialAction> Sent = new();
        public readonly Dictionary<SocialOption, bool> Options = new() { [SocialOption.ShareFellowshipExperience] = true };
        public (string, uint)? Told;
        public uint Selection { get; set; }
        public void Select(uint id) => Selection = id;
        public bool? Option(SocialOption option) => Options.TryGetValue(option, out var on) && on;
        public void SetOption(SocialOption option, bool on) => Options[option] = on;
        public bool Send(SocialAction action) { Sent.Add(action); return true; }
        public void Tell(string name, uint id) => Told = (name, id);
    }
}
