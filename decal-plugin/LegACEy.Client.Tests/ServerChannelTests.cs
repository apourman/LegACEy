using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;

namespace LegACEy.Client.Tests;

public sealed class ServerChannelTests
{
    // The same bytes are decoded by ACE.Server.Tests' ChannelWireTests: keep both in step.
    internal static readonly byte[] GoldenRequest =
    {
        0x01, 0x00,                         // version 1
        0x07, 0x00, 0x00, 0x00,             // request id 7
        0x0A, 0x00, (byte)'v', (byte)'a', (byte)'u', (byte)'l', (byte)'t', (byte)'.', (byte)'l', (byte)'i', (byte)'s', (byte)'t',
        0x02, 0x00, 0x00, 0x00, 0xAB, 0xCD  // body
    };

    [Fact]
    public void Request_encoding_matches_the_server_wire_format()
    {
        Assert.Equal(GoldenRequest, ChannelWire.EncodeRequest(7, "vault.list", new byte[] { 0xAB, 0xCD }));
    }

    [Fact]
    public void Replies_complete_their_own_request_with_round_trip_time()
    {
        var clock = new Clock();
        var transport = new RecordingTransport();
        var channel = new ServerChannelClient(transport, clock.Now);
        ChannelReply? first = null, second = null;
        channel.Request("a", Array.Empty<byte>(), reply => first = reply);
        channel.Request("b", Array.Empty<byte>(), reply => second = reply);
        var ids = transport.Sent.Select(RequestId).ToArray();

        clock.Advance(TimeSpan.FromMilliseconds(35));
        channel.Receive(ChannelWire.EncodeEvent(ChannelEventKind.Reply, ChannelStatus.Ok, ids[1], "b", new byte[] { 2 }));

        Assert.Null(first);
        Assert.NotNull(second);
        Assert.True(second!.Ok);
        Assert.Equal(new byte[] { 2 }, second.Body);
        Assert.Equal(TimeSpan.FromMilliseconds(35), second.RoundTrip);
        Assert.Equal(1, channel.PendingCount);
    }

    [Fact]
    public void Requests_time_out_and_late_replies_are_discarded()
    {
        var clock = new Clock();
        var transport = new RecordingTransport();
        var channel = new ServerChannelClient(transport, clock.Now);
        var replies = new List<ChannelReply>();
        channel.Request("slow", Array.Empty<byte>(), replies.Add, TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromSeconds(2));
        channel.Tick();
        channel.Receive(ChannelWire.EncodeEvent(ChannelEventKind.Reply, ChannelStatus.Ok, RequestId(transport.Sent[0]), "slow", null));

        var reply = Assert.Single(replies);
        Assert.Equal(ChannelStatus.TimedOut, reply.Status);
        Assert.Equal(1, channel.Discarded);
    }

    [Fact]
    public void Cancelled_requests_and_subscriptions_never_call_back_and_reset_fails_the_rest()
    {
        var transport = new RecordingTransport();
        var channel = new ServerChannelClient(transport);
        var calls = new List<string>();
        var cancelled = channel.Request("cancelled", Array.Empty<byte>(), _ => calls.Add("cancelled"));
        channel.Request("open", Array.Empty<byte>(), reply => calls.Add("open:" + reply.Status));
        var subscription = channel.Subscribe("topic", _ => calls.Add("push"));

        cancelled.Dispose();
        subscription.Dispose();
        channel.Receive(ChannelWire.EncodeEvent(ChannelEventKind.Reply, ChannelStatus.Ok, RequestId(transport.Sent[0]), "cancelled", null));
        channel.Receive(ChannelWire.EncodeEvent(ChannelEventKind.Push, ChannelStatus.Ok, 0, "topic", null));
        channel.Reset();

        Assert.Equal(new[] { "open:Disconnected" }, calls);
    }

    [Fact]
    public void Pushes_reach_topic_subscribers_and_garbage_is_ignored()
    {
        var channel = new ServerChannelClient(new RecordingTransport());
        var bodies = new List<byte[]>();
        channel.Subscribe("vault.changed", bodies.Add);
        channel.Subscribe("other", _ => throw new InvalidOperationException("wrong topic"));

        channel.Receive(ChannelWire.EncodeEvent(ChannelEventKind.Push, ChannelStatus.Ok, 0, "vault.changed", new byte[] { 9 }));
        channel.Receive(new byte[] { 0x02, 0x00, 0x01 });

        Assert.Equal(new byte[] { 9 }, Assert.Single(bodies));
        Assert.Equal(1, channel.Discarded);
    }

    [Fact]
    public void Unsent_requests_fail_at_once()
    {
        var channel = new ServerChannelClient(new RecordingTransport { Available = false });
        ChannelReply? reply = null;
        channel.Request("a", Array.Empty<byte>(), r => reply = r);
        Assert.Equal(ChannelStatus.NotSent, reply!.Status);
        Assert.Equal(0, channel.PendingCount);
    }

    [Fact]
    public void Live_vault_loads_withdraws_and_refreshes_on_the_server_push() => RenderThread.Run(() =>
    {
        var clock = new Clock();
        var server = new FakeVaultServer(clock.Now) { Latency = TimeSpan.FromMilliseconds(30), TransferTime = TimeSpan.FromSeconds(3), Balance = 1234 };
        var channel = new ServerChannelClient(server, clock.Now);
        server.Deliver = channel.Receive;
        var client = new VaultClient(channel);
        VaultShellPanel? vault = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(vault = new VaultShellPanel(new NoArt(), client)),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposeVault = vault!;

        Assert.Equal(VaultConnection.Connecting, client.Connection);
        Step(TimeSpan.FromMilliseconds(30)); // hello
        Step(TimeSpan.FromMilliseconds(30)); // list
        Assert.Equal(VaultConnection.Live, client.Connection);
        Assert.Equal(new[] { VaultProtocol.Hello, VaultProtocol.List }, server.Received);
        Assert.Equal(9, client.Snapshot!.Items.Count);
        Assert.Contains("9 items  /  1,000 capacity", Texts());
        Assert.Contains("1,234 MMD", Texts());
        Assert.Contains("Chainmail Shirt", Texts());
        Assert.Contains("Preview Character", Texts());
        Assert.Contains(Texts(), text => text.StartsWith("Live · 30 ms", StringComparison.Ordinal));

        // Selecting the second item and withdrawing it goes to the server for that item.
        var cells = host.Content.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("vault-cell")).ToArray();
        Assert.Equal(9, cells.Length);
        Click(cells[1]);
        Assert.Contains("Leather Boots", Texts());
        Click(Button("Withdraw item"));
        Step(TimeSpan.FromMilliseconds(30));
        Assert.True(client.TransferPending);
        Assert.False(Button("Withdraw item").IsEnabled);

        Step(TimeSpan.FromSeconds(3));       // the transfer finishes and is pushed
        Step(TimeSpan.FromMilliseconds(30)); // the refreshed list arrives
        Assert.Equal(1, client.PushesReceived);
        Assert.False(client.TransferPending);
        Assert.Equal(8, client.Snapshot.Items.Count);
        Assert.DoesNotContain(client.Snapshot.Items, item => item.Name == "Leather Boots");
        Assert.Contains("Your Leather Boots is back in your pack.", Texts());
        Assert.Contains(Texts(), text => text.Contains("1 push ·"));
        Assert.Null(host.LastError);

        void Step(TimeSpan time)
        {
            clock.Advance(time);
            server.Pump();
            channel.Tick();
            host.Tick();
        }

        string[] Texts() => host.Content.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();

        Button Button(string text) => host.Content.GetVisualDescendants().OfType<Button>()
            .Single(button => button.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == text));

        void Click(Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host.Content)!.Value;
            host.PointerDown(point.X, point.Y);
            host.PointerUp(point.X, point.Y);
            host.Tick();
        }
    });

    [Fact]
    public void Vault_without_a_channel_says_so_and_closing_the_window_cancels_requests() => RenderThread.Run(() =>
    {
        var unavailable = new VaultClient(UnavailableServerChannel.Instance);
        using (var panel = new VaultShellPanel(new NoArt(), unavailable))
            Assert.Equal(VaultConnection.Unavailable, unavailable.Connection);

        var transport = new RecordingTransport();
        var channel = new ServerChannelClient(transport);
        var vault = new VaultShellPanel(new NoArt(), new VaultClient(channel));
        Assert.Equal(1, channel.PendingCount);
        vault.Dispose();
        Assert.Equal(0, channel.PendingCount);
    });

    private static uint RequestId(byte[] payload) => BitConverter.ToUInt32(payload, 2);

    private sealed class Clock
    {
        private DateTime _now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Now() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }

    private sealed class RecordingTransport : IServerChannelTransport
    {
        public bool Available { get; set; } = true;
        public bool IsAvailable => Available;
        public List<byte[]> Sent { get; } = new();
        public bool Send(byte[] requestPayload) { Sent.Add(requestPayload); return true; }
    }

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}
