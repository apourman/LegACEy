using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// A LegACEy plugin: one class in Plugins/&lt;Name&gt;/&lt;Name&gt;.dll beside the client. The client calls
/// <see cref="Start"/> once with the plugin's own <see cref="ILegACEyClient"/>.
/// </summary>
public interface ILegACEyPlugin
{
    string Name { get; }
    string Version { get; }
    /// <summary>Server actions the plugin uses. It stays hidden unless the server registers every one.</summary>
    IReadOnlyCollection<string> RequiredActions { get; }
    void Start(ILegACEyClient client);
}

/// <summary>What a plugin can use. Windows and menu entries made through it belong to that plugin.</summary>
public interface ILegACEyClient
{
    IServerChannel ServerChannel { get; }
    /// <summary>The client's portal.dat path, for reading models off the game thread.</summary>
    string PortalPath { get; }
    /// <summary>The client's interface art, read on the game thread. Images are decoded once and shared.</summary>
    IGameArtSource Art { get; }
    /// <summary>Drag services for items between the retail inventory and a LegACEy window.</summary>
    IItemDragHost ItemDrag { get; }
    /// <summary>The object selected in the game; zero for none.</summary>
    uint CurrentSelection { get; }
    /// <summary>Adds an entry to the LegACEy menu. Its action runs when the player picks it.</summary>
    void AddMenuEntry(string title, uint iconId, Action action);
    /// <summary>
    /// Opens the plugin's window around the content it builds, or hides that window when it is open. A hidden window keeps its
    /// content and comes back as it was; the content is built once per session.
    /// </summary>
    void ToggleWindow(string id, string title, int width, int height, Point defaultLocation, Func<Control> createContent);
    /// <summary>
    /// Like <see cref="ToggleWindow"/>, but the plugin builds the whole window, with its own chrome. The function gets the action
    /// that closes the window; the client adds no chrome of its own.
    /// </summary>
    void ToggleWindowWithChrome(string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow);
    /// <summary>
    /// Registers the plugin's window for a station. The window opens when the server pushes station.open for it and
    /// closes on station.close. Closing it sends station.leave. Takes the same arguments as <see cref="ToggleWindowWithChrome"/>.
    /// </summary>
    void RegisterStationWindow(string station, string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow, IClientTheme? theme = null);
}

/// <summary>The client's side of plugin hosting. ClientUiRuntime implements it; tests fake it.</summary>
public interface ILegACEyPluginHost
{
    IServerChannel ServerChannel { get; }
    string PortalPath { get; }
    IGameArtSource Art { get; }
    IItemDragHost ItemDrag { get; }
    uint CurrentSelection { get; }
    bool IsWindowOpen(string id);
    /// <summary>
    /// Opens a LegACEy window, or shows it again as it was if it is hidden. <paramref name="createContent"/> runs
    /// only when a new window will open, and gets the action that hides it. Without <paramref name="ownChrome"/> the
    /// client wraps the content in its theme chrome. Returns false when windows are unavailable right now. An error from the window or its panel is passed to <paramref name="failed"/>.
    /// </summary>
    bool OpenWindow(WindowDefinition definition, Point location, Func<Action, Control> createContent, Action<Exception> failed, bool ownChrome);
    /// <summary>Hides the window if it is open, keeping it for the next <see cref="OpenWindow"/> with the same id.</summary>
    void HideWindow(string id);
    /// <summary>Closes the window, open or hidden, and releases it.</summary>
    void CloseWindow(string id);
}

/// <summary>One row of the LegACEy menu.</summary>
public sealed class PluginMenuEntry
{
    internal PluginMenuEntry(PluginEntry owner, string title, uint iconId, Action action)
    {
        Owner = owner;
        Title = title ?? string.Empty;
        IconId = iconId;
        Action = action;
    }

    public string Title { get; }
    /// <summary>The portal.dat image drawn beside the title; zero for none.</summary>
    public uint IconId { get; }
    internal PluginEntry Owner { get; }
    internal Action Action { get; }
}

internal sealed class PluginEntry
{
    public PluginEntry(string name, Assembly assembly, IReadOnlyList<string> requiredActions)
    {
        Name = name;
        Assembly = assembly;
        RequiredActions = requiredActions;
    }

    public string Name { get; }
    /// <summary>The plugin's own assembly; errors whose stack passes through it belong to the plugin.</summary>
    public Assembly Assembly { get; }
    public IReadOnlyList<string> RequiredActions { get; }
    public bool Enabled { get; set; } = true;
    public List<PluginMenuEntry> MenuEntries { get; } = new();
    /// <summary>Channel subscriptions the plugin holds; disposed when it is turned off.</summary>
    public List<IDisposable> Subscriptions { get; } = new();
}

/// <summary>Finds the plugins beside the client: Plugins/&lt;Name&gt;/&lt;Name&gt;.dll, one class implementing <see cref="ILegACEyPlugin"/>.</summary>
public static class PluginLoader
{
    /// <summary>Loads every plugin folder that holds a matching DLL with exactly one plugin class. Anything else is logged and skipped.</summary>
    public static IReadOnlyList<ILegACEyPlugin> Load(string pluginsDirectory, Action<string> log)
    {
        if (log == null) throw new ArgumentNullException(nameof(log));
        var plugins = new List<ILegACEyPlugin>();
        if (!Directory.Exists(pluginsDirectory)) return plugins;
        foreach (var folder in Directory.GetDirectories(pluginsDirectory).OrderBy(folder => Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(folder);
            var path = Path.Combine(folder, name + ".dll");
            if (!File.Exists(path))
            {
                log($"Plugin folder '{name}' skipped: it has no {name}.dll.");
                continue;
            }
            try
            {
                var plugin = Find(path, name, log);
                if (plugin != null) plugins.Add(plugin);
            }
            catch (Exception exception)
            {
                log($"Plugin '{name}' skipped: {exception}");
            }
        }
        return plugins;
    }

    private static ILegACEyPlugin? Find(string path, string folderName, Action<string> log)
    {
        var assembly = Assembly.LoadFrom(path);
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { types = exception.Types.Where(type => type != null).Select(type => type!).ToArray(); }
        var implementations = types.Where(type => type.IsClass && !type.IsAbstract && typeof(ILegACEyPlugin).IsAssignableFrom(type)).ToArray();
        if (implementations.Length != 1)
        {
            log($"Plugin '{folderName}' skipped: expected one class implementing ILegACEyPlugin, found {implementations.Length}.");
            return null;
        }
        return (ILegACEyPlugin)Activator.CreateInstance(implementations[0])!;
    }
}
