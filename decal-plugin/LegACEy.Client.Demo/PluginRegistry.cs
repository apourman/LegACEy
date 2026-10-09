using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The client's plugin state: which plugins started, which server actions the server reported, what the LegACEy menu
/// lists, and which windows each plugin owns. A failure turns off only the plugin that caused it. Like the rest of the
/// host, it is used from the render thread only.
/// </summary>
public sealed class PluginRegistry
{
    private readonly ILegACEyPluginHost _host;
    private readonly Action<string> _log;
    private readonly List<PluginEntry> _entries = new();
    private readonly Dictionary<string, PluginEntry> _windowOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PluginEntry Owner, WindowDefinition Window, Point Location, Func<Action, Control> CreateWindow)> _stations = new(StringComparer.Ordinal);
    private HashSet<string> _serverActions = new(StringComparer.Ordinal);

    public PluginRegistry(ILegACEyPluginHost host, Action<string> log)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        host.ServerChannel.Subscribe(StationProtocol.Open, body => { if (StationName(body) is { } station) OpenStation(station); });
        host.ServerChannel.Subscribe(StationProtocol.Close, body => { if (StationName(body) is { } station) CloseStation(station); });
    }

    /// <summary>Raised when the menu may have changed: a server answer, a plugin turned off, a menu entry added, or a session change.</summary>
    public event EventHandler? MenuChanged;

    /// <summary>The menu entries of visible plugins, in load order.</summary>
    public IReadOnlyList<PluginMenuEntry> VisibleMenuEntries =>
        _entries.Where(IsVisible).SelectMany(entry => entry.MenuEntries).ToArray();

    /// <summary>
    /// Starts a plugin. A plugin whose name is already loaded is skipped; one whose Start throws is turned off.
    /// </summary>
    public void Add(ILegACEyPlugin plugin)
    {
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        string name, version;
        string[] requiredActions;
        Assembly assembly;
        try
        {
            name = plugin.Name ?? string.Empty;
            version = plugin.Version ?? string.Empty;
            requiredActions = (plugin.RequiredActions ?? Array.Empty<string>()).ToArray();
            assembly = plugin.GetType().Assembly;
        }
        catch (Exception exception)
        {
            _log($"A plugin was skipped: its name or required actions could not be read: {exception}");
            return;
        }
        if (name.Trim().Length == 0)
        {
            _log("A plugin was skipped: it has no name.");
            return;
        }
        if (_entries.Any(entry => entry.Name == name))
        {
            _log($"Plugin '{name}' skipped: a plugin with that name is already loaded.");
            return;
        }

        var entry = new PluginEntry(name, assembly, requiredActions);
        _entries.Add(entry);
        try
        {
            plugin.Start(new PluginClient(this, entry));
            _log($"Plugin '{name}' {version} started.");
        }
        catch (Exception exception)
        {
            Fail(entry, exception);
        }
    }

    /// <summary>
    /// The server's answer to channel.hello. Null means no answer, so no server actions: every plugin that needs one stays hidden.
    /// </summary>
    public void SetServerActions(IEnumerable<string>? actions)
    {
        _serverActions = new HashSet<string>(actions ?? Array.Empty<string>(), StringComparer.Ordinal);
        MenuChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The player logged off: plugin windows close and the server's actions are forgotten until the next login.</summary>
    public void EndSession()
    {
        foreach (var id in _windowOwners.Keys.ToArray())
            _host.CloseWindow(id);
        _windowOwners.Clear();
        SetServerActions(null);
    }

    /// <summary>Runs a menu entry the player picked. An error from it turns off the plugin that owns it.</summary>
    public void RunMenuEntry(PluginMenuEntry item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        Guarded(item.Owner, item.Action);
    }

    /// <summary>
    /// Attributes an error that surfaced on the shared Avalonia dispatcher or render timer to the plugin whose code is on its
    /// stack. Returns false when no loaded plugin's code is on the stack, so the error belongs to the client.
    /// </summary>
    public bool TryFailOwner(Exception exception)
    {
        if (exception == null) throw new ArgumentNullException(nameof(exception));
        for (var error = exception; error != null; error = error.InnerException)
            foreach (var frame in new StackTrace(error).GetFrames() ?? Array.Empty<StackFrame>())
            {
                var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
                var owner = assembly == null ? null : _entries.FirstOrDefault(entry => entry.Enabled && entry.Assembly == assembly);
                if (owner == null) continue;
                Fail(owner, exception);
                return true;
            }
        return false;
    }

    private bool IsVisible(PluginEntry entry) => entry.Enabled && entry.RequiredActions.All(_serverActions.Contains);

    /// <summary>Runs one of a plugin's actions. An error from it turns that plugin off; a plugin that is already off is ignored.</summary>
    private void Guarded(PluginEntry entry, Action action)
    {
        if (!entry.Enabled) return;
        try { action(); }
        catch (Exception exception) { Fail(entry, exception); }
    }

    /// <summary>Turns one plugin off: its windows close, its menu entries and channel subscriptions go, and the reason is logged with its name.</summary>
    private void Fail(PluginEntry entry, Exception reason)
    {
        if (!entry.Enabled) return;
        entry.Enabled = false;
        _log($"Plugin '{entry.Name}' turned off: {reason}");
        foreach (var pair in _windowOwners.Where(pair => pair.Value == entry).ToArray())
        {
            _windowOwners.Remove(pair.Key);
            // An open station window still holds its session on the server; a hidden one already sent station.leave.
            var endsStation = _host.IsWindowOpen(pair.Key) && _stations.Values.Any(station => station.Window.Id == pair.Key);
            _host.CloseWindow(pair.Key);
            if (endsStation) LeaveStation();
        }
        foreach (var subscription in entry.Subscriptions.ToArray())
            subscription.Dispose();
        entry.Subscriptions.Clear();
        MenuChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleWindow(PluginEntry entry, string id, string title, int width, int height, Point location, Func<Action, Control> createContent, bool ownChrome)
    {
        // Window ids are namespaced by plugin, so a plugin can never open or close another plugin's window or a client window.
        var windowId = entry.Name + "/" + id;
        // A hidden window stays owned, so logoff or the plugin's failure still releases it.
        if (_host.IsWindowOpen(windowId))
        {
            _host.HideWindow(windowId);
            return;
        }

        if (_host.OpenWindow(new WindowDefinition(windowId, title, width, height), location, createContent, reason => Fail(entry, reason), ownChrome))
            _windowOwners[windowId] = entry;
    }

    private void RegisterStation(PluginEntry entry, string station, string id, string title, int width, int height, Point location, Func<Action, Control> createWindow, IClientTheme? theme, WindowResizing? resizing, int titleBarHeight)
    {
        if (_stations.ContainsKey(station)) throw new InvalidOperationException($"The station '{station}' already has a window.");
        _stations[station] = (entry, new WindowDefinition(entry.Name + "/" + id, title, width, height, titleBarHeight, theme: theme, resizing: resizing), location, createWindow);
    }

    private void OpenStation(string station)
    {
        if (!_stations.TryGetValue(station, out var registered)) return;
        var owner = registered.Owner;
        // The server must still serve the plugin's actions and station.leave, as for menu entries.
        // A window that can't open leaves the station, so the server doesn't hold the session for nothing.
        if (!IsVisible(owner) || !_serverActions.Contains(StationProtocol.Leave))
        {
            _log(owner.Enabled
                ? $"Station '{station}' did not open: the server does not list every action plugin '{owner.Name}' needs."
                : $"Station '{station}' did not open: plugin '{owner.Name}' is off.");
            LeaveStation();
            return;
        }
        // An open window stays as it is.
        if (_host.IsWindowOpen(registered.Window.Id)) return;
        Guarded(owner, () =>
        {
            // Closing the window ends the station session on the server.
            if (_host.OpenWindow(registered.Window, registered.Location, close => registered.CreateWindow(() => { close(); LeaveStation(); }),
                    reason => Fail(owner, reason), ownChrome: true))
                _windowOwners[registered.Window.Id] = owner;
        });
        if (!owner.Enabled && !_host.IsWindowOpen(registered.Window.Id)) LeaveStation();
    }

    private void CloseStation(string station)
    {
        if (!_stations.TryGetValue(station, out var registered)) return;
        Guarded(registered.Owner, () =>
        {
            _windowOwners.Remove(registered.Window.Id);
            _host.CloseWindow(registered.Window.Id);
        });
    }

    private void LeaveStation() => _host.ServerChannel.Request(StationProtocol.Leave, Array.Empty<byte>(), _ => { });

    /// <summary>The station name at the start of a station push; null, with a log line, when the body is malformed.</summary>
    private string? StationName(byte[] body)
    {
        try { return ChannelWire.ReadString(ChannelWire.Reader(body)); }
        catch (Exception error) when (error is EndOfStreamException || error is InvalidDataException)
        {
            _log("A station push was skipped: its body is malformed.");
            return null;
        }
    }

    private sealed class PluginClient : ILegACEyClient
    {
        private readonly PluginRegistry _registry;
        private readonly PluginEntry _entry;

        public PluginClient(PluginRegistry registry, PluginEntry entry)
        {
            _registry = registry;
            _entry = entry;
            ServerChannel = new GuardedChannel(registry, entry);
        }

        public IServerChannel ServerChannel { get; }
        public string PortalPath => _registry._host.PortalPath;
        public IGameArtSource Art => _registry._host.Art;
        public IItemDragHost ItemDrag => _registry._host.ItemDrag;

        public void AddMenuEntry(string title, uint iconId, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _entry.MenuEntries.Add(new PluginMenuEntry(_entry, title, iconId, action));
            _registry.MenuChanged?.Invoke(_registry, EventArgs.Empty);
        }

        public void ToggleWindow(string id, string title, int width, int height, Point defaultLocation, Func<Control> createContent)
        {
            if (createContent == null) throw new ArgumentNullException(nameof(createContent));
            _registry.ToggleWindow(_entry, id, title, width, height, defaultLocation, _ => createContent(), ownChrome: false);
        }

        public void ToggleWindowWithChrome(string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow)
        {
            if (createWindow == null) throw new ArgumentNullException(nameof(createWindow));
            _registry.ToggleWindow(_entry, id, title, width, height, defaultLocation, createWindow, ownChrome: true);
        }

        public void RegisterStationWindow(string station, string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow, IClientTheme? theme = null, WindowResizing? resizing = null, int titleBarHeight = 28)
        {
            if (createWindow == null) throw new ArgumentNullException(nameof(createWindow));
            _registry.RegisterStation(_entry, station, id, title, width, height, defaultLocation, createWindow, theme, resizing, titleBarHeight);
        }
    }

    /// <summary>The shared server channel as one plugin sees it: each callback runs under that plugin's guard.</summary>
    private sealed class GuardedChannel : IServerChannel
    {
        private readonly PluginRegistry _registry;
        private readonly PluginEntry _entry;

        public GuardedChannel(PluginRegistry registry, PluginEntry entry)
        {
            _registry = registry;
            _entry = entry;
        }

        private IServerChannel Channel => _registry._host.ServerChannel;

        public bool IsAvailable => Channel.IsAvailable;

        public IDisposable Request(string action, byte[] body, Action<ChannelReply> completed, TimeSpan? timeout = null)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            return Channel.Request(action, body, reply => _registry.Guarded(_entry, () => completed(reply)), timeout);
        }

        public IDisposable Subscribe(string topic, Action<byte[]> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var subscription = Channel.Subscribe(topic, body => _registry.Guarded(_entry, () => handler(body)));
            return new TrackedSubscription(_entry.Subscriptions, subscription);
        }

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return Channel.Schedule(delay, () => _registry.Guarded(_entry, action));
        }
    }

    /// <summary>A subscription the plugin holds. Disposing it removes it from the plugin's list, so the list does not grow.</summary>
    private sealed class TrackedSubscription : IDisposable
    {
        private readonly List<IDisposable> _held;
        private IDisposable? _subscription;

        public TrackedSubscription(List<IDisposable> held, IDisposable subscription)
        {
            _held = held;
            _subscription = subscription;
            held.Add(this);
        }

        public void Dispose()
        {
            var subscription = _subscription;
            if (subscription == null) return;
            _subscription = null;
            _held.Remove(this);
            subscription.Dispose();
        }
    }
}
