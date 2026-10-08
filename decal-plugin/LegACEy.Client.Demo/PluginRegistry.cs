using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;

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
    private HashSet<string> _serverActions = new(StringComparer.Ordinal);

    public PluginRegistry(ILegACEyPluginHost host, Action<string> log)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _log = log ?? throw new ArgumentNullException(nameof(log));
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
            _host.CloseWindow(pair.Key);
        }
        foreach (var subscription in entry.Subscriptions)
            subscription.Dispose();
        entry.Subscriptions.Clear();
        MenuChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleWindow(PluginEntry entry, string id, string title, int width, int height, Point location, Func<Action, Control> createContent, bool ownChrome)
    {
        // Window ids are namespaced by plugin, so a plugin can never open or close another plugin's window or a client window.
        var windowId = entry.Name + "/" + id;
        if (_host.IsWindowOpen(windowId))
        {
            _windowOwners.Remove(windowId);
            _host.CloseWindow(windowId);
            return;
        }

        if (_host.OpenWindow(new WindowDefinition(windowId, title, width, height), location, createContent, reason => Fail(entry, reason), ownChrome))
            _windowOwners[windowId] = entry;
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
        public IItemDragHost ItemDrag => _registry._host.ItemDrag;
        public uint CurrentSelection => _registry._host.CurrentSelection;

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
            _entry.Subscriptions.Add(subscription);
            return subscription;
        }
    }
}
