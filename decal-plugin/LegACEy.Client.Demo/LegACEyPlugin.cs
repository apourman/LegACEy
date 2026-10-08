using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;

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
    /// <summary>The client's portal.dat path, for game art and model building.</summary>
    string PortalPath { get; }
    /// <summary>Adds an entry to the LegACEy menu. Its action runs when the player picks it.</summary>
    void AddMenuEntry(string title, uint iconId, Action action);
    /// <summary>Opens the plugin's window around the content it builds, or closes that window when it is open.</summary>
    void ToggleWindow(string id, string title, int width, int height, Point defaultLocation, Func<Control> createContent);
}

/// <summary>The client's side of plugin hosting. ClientUiRuntime implements it; tests fake it.</summary>
public interface ILegACEyPluginHost
{
    IServerChannel ServerChannel { get; }
    string PortalPath { get; }
    bool IsWindowOpen(string id);
    /// <summary>
    /// Opens a LegACEy window around the content. Returns false when windows are unavailable right now. An error from the
    /// window or its panel is passed to <paramref name="failed"/>.
    /// </summary>
    bool OpenWindow(WindowDefinition definition, Point location, Control content, Action<Exception> failed);
    /// <summary>Closes the window if it is open.</summary>
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
    public PluginEntry(string name, string version, IReadOnlyList<string> requiredActions)
    {
        Name = name;
        Version = version;
        RequiredActions = requiredActions;
    }

    public string Name { get; }
    public string Version { get; }
    public IReadOnlyList<string> RequiredActions { get; }
    public bool Enabled { get; set; } = true;
    public List<PluginMenuEntry> MenuEntries { get; } = new();
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
