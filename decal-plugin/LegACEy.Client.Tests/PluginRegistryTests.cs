using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Paperdoll;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class PluginRegistryTests
{
    [Fact]
    public void A_plugin_needing_an_action_the_reply_lacks_stays_hidden()
    {
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        registry.Add(new FakePlugin("Paperdoll", "paperdoll.look"));

        registry.SetServerActions(new[] { "channel.hello", "vault.list" });
        Assert.Empty(Titles(registry));

        registry.SetServerActions(new[] { "channel.hello", "paperdoll.look" });
        Assert.Equal(new[] { "Paperdoll" }, Titles(registry));
    }

    [Fact]
    public void SupportsAction_follows_the_servers_last_list_and_the_plugin_gets_the_host_inventory()
    {
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        ILegACEyClient? client = null;
        registry.Add(new FakePlugin("Local") { OnStart = started => client = started });

        Assert.False(client!.SupportsAction("paperdoll.look"));

        registry.SetServerActions(new[] { "paperdoll.look" });
        Assert.True(client.SupportsAction("paperdoll.look"));
        Assert.False(client.SupportsAction("vault.list"));

        registry.SetServerActions(null);
        Assert.False(client.SupportsAction("paperdoll.look"));
    }

    [Fact]
    public void A_throwing_inventory_handler_turns_off_only_its_plugin_and_a_turned_off_plugin_hears_no_more_changes()
    {
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        var inventory = (FakeInventoryPort)host.Inventory;
        var quietChanges = 0;
        var offChanges = 0;
        registry.Add(new FakePlugin("Broken") { OnStart = client =>
        {
            client.AddMenuEntry("Broken", 0, () => { });
            client.Inventory.Changed += () => throw new InvalidOperationException("handler failed");
        } });
        registry.Add(new FakePlugin("Quiet") { OnStart = client =>
        {
            client.AddMenuEntry("Quiet", 0, () => { });
            client.Inventory.Changed += () => quietChanges++;
        } });
        registry.Add(new FakePlugin("Off") { OnStart = client =>
        {
            client.AddMenuEntry("Off", 0, () => throw new InvalidOperationException("menu failed"));
            client.Inventory.Changed += () => offChanges++;
        } });

        inventory.Push(InventorySnapshot.Empty);

        Assert.Equal(new[] { "Quiet", "Off" }, Titles(registry));
        Assert.Equal(1, quietChanges);
        Assert.Equal(1, offChanges);

        registry.RunMenuEntry(registry.VisibleMenuEntries.Single(entry => entry.Title == "Off"));
        inventory.Push(InventorySnapshot.Empty);

        Assert.Equal(new[] { "Quiet" }, Titles(registry));
        Assert.Equal(2, quietChanges);
        Assert.Equal(1, offChanges);
    }

    [Fact]
    public void Without_a_server_reply_server_backed_plugins_stay_hidden_and_a_plugin_needing_none_shows()
    {
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        registry.Add(new FakePlugin("Paperdoll", "paperdoll.look"));
        registry.Add(new FakePlugin("Local"));

        Assert.Equal(new[] { "Local" }, Titles(registry));

        registry.SetServerActions(new[] { "paperdoll.look" });
        Assert.Equal(new[] { "Paperdoll", "Local" }, Titles(registry));

        // Logoff, or a timed-out hello, forgets the answer.
        registry.SetServerActions(null);
        Assert.Equal(new[] { "Local" }, Titles(registry));
    }

    [Fact]
    public void A_plugin_that_throws_on_start_turns_off_alone_and_the_other_keeps_working()
    {
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        var ran = 0;
        registry.Add(new FakePlugin("Working") { OnStart = client => client.AddMenuEntry("Working", 0, () => ran++) });
        registry.Add(new FakePlugin("Broken") { StartFailure = new InvalidOperationException("start failed") });

        Assert.Equal(new[] { "Working" }, Titles(registry));
        Assert.Contains(host.Log, line => line.Contains("'Broken'") && line.Contains("start failed"));

        registry.RunMenuEntry(registry.VisibleMenuEntries.Single());
        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_window_error_turns_off_only_the_plugin_that_opened_it() => RenderThread.Run(() =>
    {
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        registry.Add(new FakePlugin("Owner") { OnStart = client => client.AddMenuEntry("Owner", 0,
            () => client.ToggleWindow("window", "Owner", 100, 80, new Point(0, 0), () => new Border())) });
        registry.Add(new FakePlugin("Other") { OnStart = client => client.AddMenuEntry("Other", 0,
            () => client.ToggleWindow("window", "Other", 100, 80, new Point(0, 0), () => new Border())) });
        registry.RunMenuEntry(registry.VisibleMenuEntries.First(entry => entry.Title == "Owner"));
        registry.RunMenuEntry(registry.VisibleMenuEntries.First(entry => entry.Title == "Other"));
        Assert.True(host.IsWindowOpen("Owner/window"));
        Assert.True(host.IsWindowOpen("Other/window"));

        host.Errors["Owner/window"](new InvalidOperationException("content failed"));

        Assert.False(host.IsWindowOpen("Owner/window"));
        Assert.True(host.IsWindowOpen("Other/window"));
        Assert.Equal(new[] { "Other" }, Titles(registry));
    });

    [Fact]
    public void A_reply_or_push_handler_that_throws_turns_off_only_its_plugin()
    {
        var channel = new FakeChannel();
        var host = new FakeHost { ServerChannel = channel };
        var registry = new PluginRegistry(host, host.Log.Add);
        registry.Add(new FakePlugin("Reader")
        {
            OnStart = client =>
            {
                client.AddMenuEntry("Reader", 0, () => { });
                client.ServerChannel.Subscribe("paperdoll.changed", _ => throw new InvalidDataException("malformed body"));
            },
        });
        registry.Add(new FakePlugin("Other"));

        channel.Push("paperdoll.changed", Array.Empty<byte>());

        Assert.Equal(new[] { "Other" }, Titles(registry));
        Assert.Contains(host.Log, line => line.Contains("'Reader'") && line.Contains("malformed body"));
    }

    [Fact]
    public void An_error_through_a_plugin_assembly_turns_that_plugin_off_and_an_error_without_one_does_not()
    {
        // The Paperdoll assembly is a real plugin assembly; the test assembly hosts the fake plugin.
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        registry.Add(new PaperdollPlugin());
        registry.Add(new FakePlugin("Other"));
        registry.SetServerActions(new[] { PaperdollProtocol.Look });
        Exception? thrown = null;
        // The throw comes from the Paperdoll assembly's own code (a null client); ReadLook now lives in the shared assembly.
        try { new PaperdollPlugin().Start(null!); }
        catch (Exception exception) { thrown = exception; }

        Assert.False(registry.TryFailOwner(new InvalidOperationException("no frames: never thrown")));
        Assert.Equal(new[] { "Paperdoll", "Other" }, Titles(registry));

        Assert.True(registry.TryFailOwner(thrown!));
        Assert.Equal(new[] { "Other" }, Titles(registry));
    }

    [Fact]
    public void A_window_with_its_own_chrome_opens_without_the_client_chrome_and_its_close_action_closes_it() => RenderThread.Run(() =>
    {
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        var root = new Border();
        Action? close = null;
        registry.Add(new FakePlugin("Owner") { OnStart = client => client.AddMenuEntry("Owner", 0,
            () => client.ToggleWindowWithChrome("window", "Owner", 100, 80, new Point(0, 0), closeWindow => { close = closeWindow; return root; })) });
        registry.RunMenuEntry(registry.VisibleMenuEntries.Single());

        Assert.True(host.IsWindowOpen("Owner/window"));
        Assert.True(host.OwnChrome["Owner/window"]);
        Assert.Same(root, host.Content["Owner/window"]);
        close!();
        Assert.False(host.IsWindowOpen("Owner/window"));
    });

    [Fact]
    public void A_window_with_its_own_chrome_gets_the_theme_resizing_and_header_height_it_asks_for() => RenderThread.Run(() =>
    {
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        var theme = new DerethClientTheme();
        var resizing = new WindowResizing(new Size(200, 150));
        registry.Add(new FakePlugin("Owner") { OnStart = client => client.AddMenuEntry("Owner", 0,
            () => client.ToggleWindowWithChrome("window", "Owner", 300, 200, new Point(0, 0), _ => new Border(), theme, resizing, 48)) });
        registry.RunMenuEntry(registry.VisibleMenuEntries.Single());

        var definition = host.Definitions["Owner/window"];
        Assert.Same(theme, definition.Theme);
        Assert.Same(resizing, definition.Resizing);
        Assert.Equal(48, definition.TitleBarHeight);
    });

    [Fact]
    public void A_toggled_off_window_comes_back_without_rebuilding_and_logoff_still_releases_it() => RenderThread.Run(() =>
    {
        var host = new FakeHost();
        var registry = new PluginRegistry(host, host.Log.Add);
        registry.Add(new FakePlugin("Owner") { OnStart = client => client.AddMenuEntry("Owner", 0,
            () => client.ToggleWindow("window", "Owner", 100, 80, new Point(0, 0), () => new Border())) });
        var entry = registry.VisibleMenuEntries.Single();

        registry.RunMenuEntry(entry);
        registry.RunMenuEntry(entry);
        Assert.False(host.IsWindowOpen("Owner/window"));
        registry.RunMenuEntry(entry);
        Assert.True(host.IsWindowOpen("Owner/window"));
        Assert.Equal(1, host.Built);

        registry.RunMenuEntry(entry);
        registry.EndSession();
        Assert.Empty(host.Content);
    });

    [Fact]
    public void A_station_open_opens_the_station_window_and_a_second_open_leaves_it_and_station_close_closes_it() => RenderThread.Run(() =>
    {
        var channel = new FakeChannel();
        var host = new FakeHost { ServerChannel = channel };
        var registry = new PluginRegistry(host, host.Log.Add);
        registry.Add(new FakePlugin("Vault") { OnStart = client => client.RegisterStationWindow("vault", "window", "Vault", 100, 80, new Point(0, 0), _ => new Border()) });
        registry.SetServerActions(new[] { StationProtocol.Leave });

        channel.Push(StationProtocol.Open, StationBody("vault"));
        Assert.True(host.IsWindowOpen("Vault/window"));
        Assert.True(host.OwnChrome["Vault/window"]);

        channel.Push(StationProtocol.Open, StationBody("vault"));
        Assert.True(host.IsWindowOpen("Vault/window"));
        Assert.Equal(1, host.Built);

        channel.Push(StationProtocol.Close, StationBody("vault"));
        Assert.False(host.IsWindowOpen("Vault/window"));
        Assert.Empty(host.Content);

        // Logoff closes an open station window; the server ends the session itself.
        channel.Push(StationProtocol.Open, StationBody("vault"));
        registry.EndSession();
        Assert.False(host.IsWindowOpen("Vault/window"));
        Assert.Empty(channel.Requested);
    });

    [Fact]
    public void A_station_window_is_opened_with_the_theme_it_was_registered_with() => RenderThread.Run(() =>
    {
        var channel = new FakeChannel();
        var host = new FakeHost { ServerChannel = channel };
        var registry = new PluginRegistry(host, host.Log.Add);
        var theme = new DerethClientTheme();
        registry.Add(new FakePlugin("Vault") { OnStart = client => client.RegisterStationWindow("vault", "window", "Vault", 100, 80, new Point(0, 0), _ => new Border(), theme) });
        registry.SetServerActions(new[] { StationProtocol.Leave });

        channel.Push(StationProtocol.Open, StationBody("vault"));
        Assert.Same(theme, host.Themes["Vault/window"]);
    });

    [Fact]
    public void Closing_a_station_window_sends_station_leave() => RenderThread.Run(() =>
    {
        var channel = new FakeChannel();
        var host = new FakeHost { ServerChannel = channel };
        var registry = new PluginRegistry(host, host.Log.Add);
        Action? close = null;
        registry.Add(new FakePlugin("Vault") { OnStart = client => client.RegisterStationWindow("vault", "window", "Vault", 100, 80, new Point(0, 0),
            closeWindow => { close = closeWindow; return new Border(); }) });
        registry.SetServerActions(new[] { StationProtocol.Leave });
        channel.Push(StationProtocol.Open, StationBody("vault"));

        close!();

        Assert.False(host.IsWindowOpen("Vault/window"));
        Assert.Equal(new[] { StationProtocol.Leave }, channel.Requested);
    });

    [Fact]
    public void A_station_window_that_throws_while_opening_turns_off_only_its_plugin()
    {
        var channel = new FakeChannel();
        var host = new FakeHost { ServerChannel = channel };
        var registry = new PluginRegistry(host, host.Log.Add);
        registry.Add(new FakePlugin("Broken") { OnStart = client => client.RegisterStationWindow("vault", "window", "Broken", 100, 80, new Point(0, 0), _ => throw new InvalidOperationException("window failed")) });
        registry.Add(new FakePlugin("Other"));
        registry.SetServerActions(new[] { StationProtocol.Leave });

        channel.Push(StationProtocol.Open, StationBody("vault"));

        Assert.False(host.IsWindowOpen("Broken/window"));
        Assert.Equal(new[] { "Other" }, Titles(registry));
        Assert.Contains(host.Log, line => line.Contains("'Broken'") && line.Contains("window failed"));
        // The window never opened, so the client leaves the station the server opened.
        Assert.Equal(new[] { StationProtocol.Leave }, channel.Requested);
    }

    [Fact]
    public void An_open_station_window_that_fails_turns_off_its_plugin_and_leaves_the_station() => RenderThread.Run(() =>
    {
        var channel = new FakeChannel();
        var host = new FakeHost { ServerChannel = channel };
        var registry = new PluginRegistry(host, host.Log.Add);
        registry.Add(new FakePlugin("Vault") { OnStart = client => client.RegisterStationWindow("vault", "window", "Vault", 100, 80, new Point(0, 0), _ => new Border()) });
        registry.SetServerActions(new[] { StationProtocol.Leave });
        channel.Push(StationProtocol.Open, StationBody("vault"));

        host.Errors["Vault/window"](new InvalidOperationException("content failed"));

        Assert.False(host.IsWindowOpen("Vault/window"));
        Assert.Equal(new[] { StationProtocol.Leave }, channel.Requested);
    });

    private static byte[] StationBody(string station) => ChannelWire.Body(writer =>
    {
        ChannelWire.WriteString(writer, station);
        writer.Write(1234u);
    });

    private static string[] Titles(PluginRegistry registry) => registry.VisibleMenuEntries.Select(entry => entry.Title).ToArray();

    private sealed class FakeHost : ILegACEyPluginHost
    {
        public List<string> Log { get; } = new();
        public Dictionary<string, Action<Exception>> Errors { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Control> Content { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> OwnChrome { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IClientTheme?> Themes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, WindowDefinition> Definitions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Settings { get; } = new(StringComparer.Ordinal);

        public int? LoadPluginSettings(string plugin) => Settings.TryGetValue(plugin, out var saved) ? saved : null;
        public void SavePluginSettings(string plugin, int value) => Settings[plugin] = value;

        public IServerChannel ServerChannel { get; init; } = UnavailableServerChannel.Instance;
        public string PortalPath => string.Empty;
        public IGameArtSource Art => throw new NotSupportedException();
        public IItemDragHost ItemDrag => new FakeItemDragHost();
        public IInventoryPort Inventory { get; } = new FakeInventoryPort();
        public bool IsWindowOpen(string id) => Errors.ContainsKey(id);

        public int Built { get; private set; }

        public bool OpenWindow(WindowDefinition definition, Point location, Func<Action, Control> createContent, Action<Exception> failed, bool ownChrome)
        {
            // Like the client: a hidden window comes back as it was; only a new one builds its content.
            if (!Content.ContainsKey(definition.Id))
            {
                Content[definition.Id] = createContent(() => HideWindow(definition.Id));
                Built++;
            }
            Errors[definition.Id] = failed;
            OwnChrome[definition.Id] = ownChrome;
            Themes[definition.Id] = definition.Theme;
            Definitions[definition.Id] = definition;
            return true;
        }

        public void HideWindow(string id) => Errors.Remove(id);

        public void CloseWindow(string id)
        {
            Errors.Remove(id);
            Content.Remove(id);
        }
    }

    /// <summary>Holds subscriptions and delivers pushes to them, as the host's channel would.</summary>
    private sealed class FakeChannel : IServerChannel
    {
        private readonly List<(string Topic, Action<byte[]> Handler)> _subscriptions = new();

        public List<string> Requested { get; } = new();
        public bool IsAvailable => true;

        public IDisposable Request(string action, byte[] body, Action<ChannelReply> completed, TimeSpan? timeout = null)
        {
            Requested.Add(action);
            return new Handle(() => { });
        }

        public IDisposable Subscribe(string topic, Action<byte[]> handler)
        {
            var subscription = (topic, handler);
            _subscriptions.Add(subscription);
            return new Handle(() => _subscriptions.Remove(subscription));
        }

        public IDisposable Schedule(TimeSpan delay, Action action) => new Handle(() => { });

        public void Push(string topic, byte[] body)
        {
            foreach (var (subscribed, handler) in _subscriptions.ToArray())
                if (subscribed == topic) handler(body);
        }

        private sealed class Handle(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }

    private sealed class FakePlugin : ILegACEyPlugin
    {
        public FakePlugin(string name, params string[] requiredActions)
        {
            Name = name;
            RequiredActions = requiredActions;
        }

        public string Name { get; }
        public string Version => "1.0.0";
        public IReadOnlyCollection<string> RequiredActions { get; }
        public Exception? StartFailure { get; init; }
        /// <summary>What Start does; by default, one menu entry named after the plugin.</summary>
        public Action<ILegACEyClient>? OnStart { get; init; }

        public void Start(ILegACEyClient client)
        {
            if (StartFailure != null) throw StartFailure;
            if (OnStart != null) OnStart(client);
            else client.AddMenuEntry(Name, 0, () => { });
        }
    }
}
