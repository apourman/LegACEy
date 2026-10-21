using System;
using System.Drawing;
using LegACEy.Client.Demo;
using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

/// <summary>What the native chat port reads from retail's notice: its chat types, its sender tags and its room prefixes.</summary>
public sealed class RetailChatNativeTests
{
    private static readonly DateTime Time = new(2026, 10, 15, 21, 2, 0);

    [Theory]
    [InlineData(2u, RetailChatKind.Local)]
    [InlineData(12u, RetailChatKind.Emote)]
    [InlineData(3u, RetailChatKind.TellIn)]
    [InlineData(31u, RetailChatKind.TellIn)]
    [InlineData(4u, RetailChatKind.TellOut)]
    [InlineData(19u, RetailChatKind.Fellowship)]
    [InlineData(18u, RetailChatKind.Allegiance)]
    [InlineData(27u, RetailChatKind.Channel)]
    [InlineData(32u, RetailChatKind.Channel)]
    [InlineData(6u, RetailChatKind.Combat)]
    [InlineData(0u, RetailChatKind.System)]
    public void Retail_chat_types_map_to_the_kinds_the_tabs_show(uint chatType, RetailChatKind kind) =>
        Assert.Equal(kind, NativeRetailChatPort.RetailChatTypes.KindOf(chatType));

    [Fact]
    public void Type_26_is_errors_and_retail_main_chat_does_not_show_it() =>
        Assert.Null(NativeRetailChatPort.RetailChatTypes.KindOf(26));

    [Fact]
    public void A_sender_tag_becomes_the_plain_name_and_names_the_sender()
    {
        var line = NativeRetailChatPort.RetailChatTypes.Parse(RetailChatKind.TellIn, "<Tell:IIDString:4:Mirra>Mirra<\\Tell> tells you, \"hi\"", Time);

        Assert.Equal("Mirra tells you, \"hi\"", line.Text);
        Assert.Equal("Mirra", line.Sender);
        Assert.Null(line.Channel);
    }

    [Fact]
    public void Retail_s_text_escapes_show_as_the_characters_they_stand_for_and_a_plugin_link_shows_its_text()
    {
        Assert.Equal("[UB] Connected to UBNet", Parse(@"\[UB\] Connected to UBNet").Text);
        Assert.Equal("Welcome\n\nFor help", Parse(@"Welcome\n\nFor help").Text);
        Assert.Equal("The command \"myquests\" is off. C:\\ <{Mag-Tools}>", Parse(@"The command \qmyquests\q is off. C:\\ <\{Mag-Tools\}>").Text);

        var link = Parse(@"Update? <Tell:IIDString:0:GoArrow_ChatCommand>Update Now<\\Tell>");
        Assert.Equal("Update? Update Now", link.Text);
        Assert.Null(link.Sender);
        Assert.Equal("- +Seed Alpha", Parse(@"<Tell:IIDString:221112:1609564160>-<\\\\Tell> +Seed Alpha").Text);

        // GoArrow closes its links with <\Tell>, UtilityBelt with </Tell>; both are links with the tag's id and text.
        foreach (var close in new[] { @"<\Tell>", "</Tell>", "</Tell >" })
        {
            var coords = Parse($"Meet at <Tell:IIDString:110011:10.7N, 52.3E>10.7N, 52.3E{close} \\[UB\\]");
            Assert.Equal("Meet at 10.7N, 52.3E [UB]", coords.Text);
            Assert.Equal(new RetailChatLink(8, 12, 110011, "10.7N, 52.3E"), Assert.Single(coords.Links!));
            Assert.Null(coords.Sender);
        }

        static RetailChatLine Parse(string raw) => NativeRetailChatPort.RetailChatTypes.Parse(RetailChatKind.Plugin, raw, Time);
    }

    [Fact]
    public void A_direct_tell_is_the_text_as_the_server_reads_it_padded_to_four_bytes_then_the_object_id()
    {
        Assert.Equal(new byte[] { 1, 0, (byte)'5', 0, 0x12, 0x40, 0x9B, 0x7A }, TalkDirectAction.Payload(0x7A9B4012, "5"));
        // The server pads by characters: "é" is one character in two bytes, so one byte of padding, not none.
        Assert.Equal(new byte[] { 1, 0, 0xC3, 0xA9, 0, 0x12, 0x40, 0x9B, 0x7A }, TalkDirectAction.Payload(0x7A9B4012, "é"));
    }

    [Fact]
    public void A_channel_prefix_names_the_room_and_an_Olthoi_room_is_a_channel_even_when_typed_as_allegiance()
    {
        var general = NativeRetailChatPort.RetailChatTypes.Parse(RetailChatKind.Channel, "[General] <Tell:IIDString:0:Kaelith>Kaelith<\\Tell> says, \"hi\"", Time);
        Assert.Equal(RetailChatKind.Channel, general.Kind);
        Assert.Equal("General", general.Channel);
        Assert.Equal("Kaelith", general.Sender);

        var olthoi = NativeRetailChatPort.RetailChatTypes.Parse(RetailChatKind.Allegiance, "[Olthoi] <Tell:IIDString:0:Saro>Saro<\\Tell> says, \"LF2M\"", Time);
        Assert.Equal(RetailChatKind.Channel, olthoi.Kind);
        Assert.Equal("Olthoi", olthoi.Channel);
    }

    [Fact]
    public void Every_chat_root_that_shows_is_held_hidden_and_given_back_visible_on_release()
    {
        var roots = new[] { new FakeRoot(true), new FakeRoot(true), new FakeRoot(false) };
        var takeover = new RetailHeldWindows(roots);

        takeover.Tick(new Size(1000, 800));

        Assert.True(takeover.Holds);
        Assert.False(roots[0].Visible);
        Assert.False(roots[1].Visible);

        takeover.Release();

        Assert.False(takeover.Holds);
        Assert.True(roots[0].Visible);
        Assert.True(roots[1].Visible);
        // A root that was already hidden stays hidden: the takeover gives back what it took.
        Assert.False(roots[2].Visible);
    }

    [Fact]
    public void Of_two_vitals_windows_release_shows_only_the_one_retail_showed_last()
    {
        var stacked = new FakeRoot(true);
        var side = new FakeRoot(false);
        var takeover = new RetailHeldWindows(new[] { stacked, side }, oneShows: true);
        takeover.Tick(new Size(1000, 800));

        // The player turns on side-by-side vitals while both are held: retail hides the stacked one (already hidden) and shows the other.
        stacked.SetVisible(false);
        side.SetVisible(true);
        takeover.Tick(new Size(1000, 800));
        Assert.False(side.Visible);

        takeover.Release();

        Assert.False(stacked.Visible);
        Assert.True(side.Visible);
    }

    /// <summary>A retail chat root: its visibility is the state the takeover hides and restores.</summary>
    private sealed class FakeRoot : IRetailTakeoverPort
    {
        public FakeRoot(bool visible) => Visible = visible;

        public bool Visible { get; private set; }
        public bool Exists => true;
        public IntPtr ElementIdentity => new(1);
        public bool IsVisible => Visible;
        public Rectangle GetBounds() => new(0, 0, 10, 10);
        public void SetVisible(bool visible) => Visible = visible;
        public void SetSaveLocation(bool save) { }
        public void MoveTo(Point location) { }
        public bool IsUiLocked => false;
    }
}
