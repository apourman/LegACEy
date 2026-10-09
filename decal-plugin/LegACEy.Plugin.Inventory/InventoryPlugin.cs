using System;
using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Inventory;

/// <summary>
/// The inventory window, opened from the LegACEy menu in the layout the character last chose. Each layout is its own window with
/// its own saved size and position, so switching layout hides one and opens the other at its own size.
/// </summary>
public sealed class InventoryPlugin : ILegACEyPlugin
{
    private static readonly Point DefaultLocation = new(240, 100);

    // The window of each layout while the client keeps it, open or hidden. A hidden window keeps its content.
    private readonly Dictionary<InventoryLayout, InventoryWindow> _windows = new();
    // The settings while a window is open: read from the client when a window opens, and every change is saved as it happens.
    private InventorySettings? _settings;
    // The layout whose window the plugin last showed, so a menu press knows whether it hides the window or opens one.
    private InventoryLayout? _shown;

    public string Name => "Inventory";
    public string Version => typeof(InventoryPlugin).Assembly.GetName().Version.ToString(3);
    /// <summary>The inventory uses no server actions, so it is never hidden for a missing one.</summary>
    public IReadOnlyCollection<string> RequiredActions { get; } = Array.Empty<string>();

    public void Start(ILegACEyClient client) =>
        client.AddMenuEntry(InventoryWindow.Title, InventoryWindow.BackpackIcon, () => Toggle(client));

    /// <summary>The menu press: opens the current layout's window, or hides it when it is showing.</summary>
    private void Toggle(ILegACEyClient client)
    {
        // The settings are the character's, and the character logged in now may not be the one the cache came from. The cache
        // is trusted only while a window is open; otherwise the press reads the value the client holds for this character.
        if (_shown == null) _settings = null;
        var layout = Current(client).Layout;
        var opening = _shown != layout;
        if (opening)
        {
            // A kept window takes the Slots choice made since it was hidden, and draws what changed while it was hidden.
            if (_windows.TryGetValue(layout, out var kept))
            {
                kept.SetShowSlots(Current(client).ShowSlots);
                kept.Resume();
            }
        }
        else if (_windows.TryGetValue(layout, out var shown))
        {
            shown.Suspend();
        }
        _shown = opening ? layout : null;
        Show(client, layout);
    }

    /// <summary>Opens the window of a layout, or hides it when the client has it open.</summary>
    private void Show(ILegACEyClient client, InventoryLayout layout)
    {
        var (width, height, sizing) = Shape(layout);
        client.ToggleWindowWithChrome(WindowId(layout), InventoryWindow.Title, width, height, DefaultLocation,
            close => CreateWindow(client, layout, close), new DerethClientTheme(), sizing, DerethWindow.TitleBarHeight);
    }

    private Control CreateWindow(ILegACEyClient client, InventoryLayout layout, Action close)
    {
        // The client keeps a closed window hidden and shows it again, so this runs once per session for each layout.
        var window = new InventoryWindow(client.Inventory, client.Art, new InventorySettings(layout, Current(client).ShowSlots));
        _windows[layout] = window;
        window.CloseRequested += (_, _) => Hide(layout, close);
        window.SettingsChanged += settings =>
        {
            _settings = settings;
            client.SaveSettings(settings.ToInt());
            if (settings.Layout == layout) return;
            // Hiding keeps this layout's size and position. The other layout opens with its own saved size.
            Hide(layout, close);
            Toggle(client);
        };
        window.DetachedFromVisualTree += (_, _) => Released(layout, window);
        return window;
    }

    /// <summary>Hides a layout's window. The client keeps it, so it comes back as it was.</summary>
    private void Hide(InventoryLayout layout, Action close)
    {
        if (_windows.TryGetValue(layout, out var window)) window.Suspend();
        if (_shown == layout) _shown = null;
        close();
    }

    /// <summary>The client released a window (the player logged off, or the plugin turned off): forget it and free it.</summary>
    private void Released(InventoryLayout layout, InventoryWindow window)
    {
        if (_windows.TryGetValue(layout, out var current) && current == window) _windows.Remove(layout);
        if (_shown == layout) _shown = null;
        window.Dispose();
    }

    private InventorySettings Current(ILegACEyClient client) => _settings ??= InventorySettings.FromInt(client.LoadSettings());

    private static string WindowId(InventoryLayout layout) => "inventory-" + layout.ToString().ToLowerInvariant();

    /// <summary>
    /// Each layout's default size and minimum. The minimum keeps the paperdoll and one row of the grid visible, and the horizontal
    /// one also keeps the pack strip on one row: the strip is the main pack, a divider and seven side packs, 295 px across.
    /// </summary>
    private static (int Width, int Height, WindowResizing Sizing) Shape(InventoryLayout layout) =>
        layout == InventoryLayout.Horizontal
            ? (640, 400, new WindowResizing(new Size(600, 330)))
            : (360, 530, new WindowResizing(new Size(330, 420)));
}
