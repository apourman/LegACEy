using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Demo;
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
            () => client.ToggleWindow("owner-window", "Owner", 100, 80, new Point(0, 0), () => new Border())) });
        registry.Add(new FakePlugin("Other") { OnStart = client => client.AddMenuEntry("Other", 0,
            () => client.ToggleWindow("other-window", "Other", 100, 80, new Point(0, 0), () => new Border())) });
        registry.RunMenuEntry(registry.VisibleMenuEntries.First(entry => entry.Title == "Owner"));
        registry.RunMenuEntry(registry.VisibleMenuEntries.First(entry => entry.Title == "Other"));

        host.Errors["owner-window"](new InvalidOperationException("content failed"));

        Assert.False(host.IsWindowOpen("owner-window"));
        Assert.True(host.IsWindowOpen("other-window"));
        Assert.Equal(new[] { "Other" }, Titles(registry));
    });

    private static string[] Titles(PluginRegistry registry) => registry.VisibleMenuEntries.Select(entry => entry.Title).ToArray();

    private sealed class FakeHost : ILegACEyPluginHost
    {
        public List<string> Log { get; } = new();
        public Dictionary<string, Action<Exception>> Errors { get; } = new(StringComparer.Ordinal);

        public IServerChannel ServerChannel => UnavailableServerChannel.Instance;
        public string PortalPath => string.Empty;
        public bool IsWindowOpen(string id) => Errors.ContainsKey(id);

        public bool OpenWindow(WindowDefinition definition, Point location, Control content, Action<Exception> failed)
        {
            Errors[definition.Id] = failed;
            return true;
        }

        public void CloseWindow(string id) => Errors.Remove(id);
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
