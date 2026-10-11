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
    /// <summary>Drag services for items dragged out of a LegACEy window onto the retail UI, and handed to a LegACEy window.</summary>
    IItemDragHost ItemDrag { get; }
    /// <summary>The character's inventory: a read-only snapshot, a change event and the retail-call commands.</summary>
    IInventoryPort Inventory { get; }
    /// <summary>True when the server registered the action (the list from channel.hello). Plugins use it for optional features.</summary>
    bool SupportsAction(string action);
    /// <summary>
    /// Calls <paramref name="changed"/> each time the server's action list changes: channel.hello answered after login, and logoff.
    /// A window opened before the answer uses it to take up an optional feature.
    /// </summary>
    void WhenServerActionsChange(Action changed);
    /// <summary>
    /// Takes over retail's inventory panel for the plugin's window, while the client's retail takeover switch is on. The client keeps the
    /// panel open where the player cannot see it, and calls <paramref name="retailOpenChanged"/> with true when the panel opens and false
    /// when it closes; the plugin then shows or hides its window. With the switch off, nothing is called and the panel is untouched.
    /// </summary>
    IRetailPanel TakeOverRetailInventory(Action<bool> retailOpenChanged);
    /// <summary>
    /// The plugin's own saved value for this character: one integer that only the plugin reads and writes, so the client does not
    /// interpret it. Null when none is saved.
    /// </summary>
    int? LoadSettings();
    /// <summary>Saves the plugin's one settings value for this character. See <see cref="LoadSettings"/>.</summary>
    void SaveSettings(int value);
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
    /// <param name="theme">The theme the window is drawn in. Null uses the client's theme.</param>
    /// <param name="resizing">How the player can resize the window from its edges and corners. Null means it does not resize.</param>
    /// <param name="titleBarHeight">The top strip of the window that drags it, in pixels. A window with a taller header passes the header's height.</param>
    /// <param name="sharesLocationWith">The id of another of the plugin's windows this one stands in for: it opens where that one was left, and that one where this one was.</param>
    void ToggleWindowWithChrome(string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow, IClientTheme? theme = null, WindowResizing? resizing = null, int titleBarHeight = 28, string? sharesLocationWith = null);
    /// <summary>
    /// Registers the plugin's window for a station, built with its own chrome as <see cref="ToggleWindowWithChrome"/> is.
    /// The window opens when the server pushes station.open for it and closes on station.close. Closing it sends station.leave.
    /// </summary>
    /// <param name="theme">The theme the window is drawn in. Null uses the client's theme; a window with its own theme is not given the retail theme over it.</param>
    /// <param name="resizing">How the player can resize the window from its edges and corners. Null means it does not resize.</param>
    /// <param name="titleBarHeight">The top strip of the window that drags it, in pixels. A window with a taller header passes the header's height.</param>
    void RegisterStationWindow(string station, string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow, IClientTheme? theme = null, WindowResizing? resizing = null, int titleBarHeight = 28);
}

/// <summary>The client's side of plugin hosting. ClientUiRuntime implements it; tests fake it.</summary>
public interface ILegACEyPluginHost
{
    IServerChannel ServerChannel { get; }
    string PortalPath { get; }
    IGameArtSource Art { get; }
    IItemDragHost ItemDrag { get; }
    IInventoryPort Inventory { get; }
    bool IsWindowOpen(string id);
    /// <summary>Takes over retail's inventory panel for a plugin's window. Returns a handle that does nothing while the switch is off.</summary>
    IRetailPanel TakeOverRetailInventory(Action<bool> retailOpenChanged);
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
    /// <summary>A plugin's one saved settings value for the current character, or null when none is saved.</summary>
    int? LoadPluginSettings(string plugin);
    /// <summary>Saves a plugin's one settings value for the current character.</summary>
    void SavePluginSettings(string plugin, int value);
}

/// <summary>
/// Retail's inventory panel, held by a plugin's window while the takeover is on. Disposing it gives the panel back; the client does that
/// too when the plugin is turned off.
/// </summary>
public interface IRetailPanel : IDisposable
{
    /// <summary>
    /// True while the takeover holds retail's panel. Then <see cref="Close"/> asks retail, and retail's report
    /// opens or closes the plugin's window. False when the switch is off: the plugin's window opens and closes itself.
    /// </summary>
    bool Holds { get; }
    /// <summary>Closes retail's panel through its own panel switch, while the takeover holds it. Does nothing otherwise.</summary>
    void Close();
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
    /// <summary>Channel subscriptions and retail panel takeovers the plugin holds; disposed when it is turned off.</summary>
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
