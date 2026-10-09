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
    // The Dereth header's strip from the window's top that drags it: the 8 px frame edge and the 40 px header.
    private const int HeaderHeight = 48;
    private static readonly Point DefaultLocation = new(240, 100);

    // The window of each layout, while the client keeps it open or hidden. A hidden window keeps its content, so it is
    // told about the Slots choice made in the other layout when it comes back.
    private readonly Dictionary<InventoryLayout, InventoryWindow> _windows = new();

    public string Name => "Inventory";
    public string Version => typeof(InventoryPlugin).Assembly.GetName().Version.ToString(3);
    /// <summary>The inventory uses no server actions, so it is never hidden for a missing one.</summary>
    public IReadOnlyCollection<string> RequiredActions { get; } = Array.Empty<string>();

    public void Start(ILegACEyClient client) =>
        client.AddMenuEntry(InventoryWindow.Title, InventoryWindow.BackpackIcon, () => Toggle(client, Settings(client).Layout));

    /// <summary>Opens the window of one layout, or hides it when it is open.</summary>
    private void Toggle(ILegACEyClient client, InventoryLayout layout)
    {
        if (_windows.TryGetValue(layout, out var kept)) kept.SetShowSlots(Settings(client).ShowSlots);
        var (width, height, sizing) = Shape(layout);
        client.ToggleWindowWithChrome(WindowId(layout), InventoryWindow.Title, width, height, DefaultLocation,
            close => CreateWindow(client, layout, close), new DerethClientTheme(), sizing, HeaderHeight);
    }

    private Control CreateWindow(ILegACEyClient client, InventoryLayout layout, Action close)
    {
        // The client keeps a closed window hidden and shows it again, so this runs once per session for each layout.
        var window = new InventoryWindow(client.Inventory, client.Art, new InventorySettings(layout, Settings(client).ShowSlots));
        _windows[layout] = window;
        window.CloseRequested += (_, _) => close();
        window.SettingsChanged += settings =>
        {
            client.SaveSettings(settings.ToPoint());
            if (settings.Layout == layout) return;
            // Hiding keeps this layout's size and position. The other layout opens with its own saved size.
            close();
            Toggle(client, settings.Layout);
        };
        window.DetachedFromVisualTree += (_, _) =>
        {
            if (_windows.TryGetValue(layout, out var current) && current == window) _windows.Remove(layout);
            window.Dispose();
        };
        return window;
    }

    private static InventorySettings Settings(ILegACEyClient client) => InventorySettings.FromPoint(client.LoadSettings());

    private static string WindowId(InventoryLayout layout) => "inventory-" + layout.ToString().ToLowerInvariant();

    /// <summary>
    /// Each layout's default size and minimum. The minimum keeps the paperdoll and one row of the grid visible: the vertical
    /// minimum is the paperdoll's width and height plus the header, and the horizontal one fits the pack strip and the footer.
    /// </summary>
    private static (int Width, int Height, WindowResizing Sizing) Shape(InventoryLayout layout) =>
        layout == InventoryLayout.Horizontal
            ? (640, 400, new WindowResizing(new Size(560, 330)))
            : (360, 530, new WindowResizing(new Size(330, 420)));
}
