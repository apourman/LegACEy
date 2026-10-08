using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
        try
        {
            name = plugin.Name ?? string.Empty;
            version = plugin.Version ?? string.Empty;
            requiredActions = (plugin.RequiredActions ?? Array.Empty<string>()).ToArray();
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

        var entry = new PluginEntry(name, version, requiredActions);
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
        try { item.Action(); }
        catch (Exception exception) { Fail(item.Owner, exception); }
    }

    private bool IsVisible(PluginEntry entry) => entry.Enabled && entry.RequiredActions.All(_serverActions.Contains);

    /// <summary>Turns one plugin off: its windows close, its menu entries go, and the reason is logged with its name.</summary>
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
        MenuChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleWindow(PluginEntry entry, string id, string title, int width, int height, Point location, Func<Control> createContent)
    {
        if (_host.IsWindowOpen(id))
        {
            if (!_windowOwners.TryGetValue(id, out var owner) || owner != entry)
                throw new InvalidOperationException($"The window '{id}' is not one {entry.Name} opened.");
            _windowOwners.Remove(id);
            _host.CloseWindow(id);
            return;
        }

        var content = createContent();
        if (_host.OpenWindow(new WindowDefinition(id, title, width, height), location, content, reason => Fail(entry, reason)))
            _windowOwners[id] = entry;
        else
            (content as IDisposable)?.Dispose();
    }

    private sealed class PluginClient : ILegACEyClient
    {
        private readonly PluginRegistry _registry;
        private readonly PluginEntry _entry;

        public PluginClient(PluginRegistry registry, PluginEntry entry)
        {
            _registry = registry;
            _entry = entry;
        }

        public IServerChannel ServerChannel => _registry._host.ServerChannel;
        public string PortalPath => _registry._host.PortalPath;

        public void AddMenuEntry(string title, uint iconId, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _entry.MenuEntries.Add(new PluginMenuEntry(_entry, title, iconId, action));
            _registry.MenuChanged?.Invoke(_registry, EventArgs.Empty);
        }

        public void ToggleWindow(string id, string title, int width, int height, Point defaultLocation, Func<Control> createContent)
        {
            if (createContent == null) throw new ArgumentNullException(nameof(createContent));
            _registry.ToggleWindow(_entry, id, title, width, height, defaultLocation, createContent);
        }
    }
}
