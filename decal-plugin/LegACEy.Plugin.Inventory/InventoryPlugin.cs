using System;
using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Inventory;

/// <summary>
/// The inventory window, standing in for retail's inventory panel: it opens and closes with retail's panel, in the layout the
/// character last chose. Each layout is its own window with its own saved size and position, so switching hides one and opens the
/// other at its own size. A layout with its equipment section collapsed is another window with its own size, but it stays where
/// the full one is, so collapsing and expanding keep the window in place.
/// </summary>
public sealed class InventoryPlugin : ILegACEyPlugin
{
    private static readonly Point DefaultLocation = new(240, 100);

    // The window of each view while the client keeps it, open or hidden. A hidden window keeps its content.
    private readonly Dictionary<InventoryView, InventoryWindow> _windows = new();
    // The retail panel this window stands in for. It does nothing while the client's takeover is off, and then retail's panel shows.
    private IRetailPanel? _retail;
    // The settings while a window is open: read from the client when a window opens, and every change is saved as it happens.
    // The cache is the character's, and the character logged in now may not be the one it came from, so a retail report clears it
    // while no window is open (the next read reaches the client); a view switch never clears it.
    private InventorySettings? _settings;
    // The view whose window the plugin last showed, so a retail report knows whether it hides the window or opens one.
    private InventoryView? _shown;

    public string Name => "Inventory";
    public string Version => typeof(InventoryPlugin).Assembly.GetName().Version.ToString(3);
    /// <summary>The inventory uses no server actions, so it is never hidden for a missing one.</summary>
    public IReadOnlyCollection<string> RequiredActions { get; } = Array.Empty<string>();

    public void Start(ILegACEyClient client)
    {
        _retail = client.TakeOverRetailInventory(open => RetailPanelChanged(client, open));
        // The window can open before the server's action list arrives at login: the doll joins it when the list does.
        client.WhenServerActionsChange(() =>
        {
            if (_shown is { } view && _windows.TryGetValue(view, out var window)) GiveDoll(client, window);
        });
    }

    /// <summary>
    /// Retail opened or closed its inventory panel (by key, toolbar or item) while the takeover is on. A view switch never comes
    /// here: it hides and shows windows of its own and leaves retail's panel open.
    /// </summary>
    private void RetailPanelChanged(ILegACEyClient client, bool open)
    {
        if (open == (_shown != null)) return;
        if (_shown == null) _settings = null;
        Toggle(client, Current(client).View);
    }

    /// <summary>Opens the window of a view, or hides it when it is the one showing.</summary>
    private void Toggle(ILegACEyClient client, InventoryView view)
    {
        var opening = _shown != view;
        if (opening)
        {
            // A kept window takes the Slots choice made since it was hidden, and draws what changed while it was hidden.
            if (_windows.TryGetValue(view, out var kept))
            {
                GiveDoll(client, kept);
                kept.SetShowSlots(Current(client).ShowSlots);
                kept.Resume();
            }
        }
        else if (_windows.TryGetValue(view, out var shown))
        {
            shown.Suspend();
        }
        _shown = opening ? view : null;
        Show(client, view);
    }

    /// <summary>
    /// Gives a window its 3D character when the server lists the look action. The list can arrive after a window was built (the
    /// window opens before channel.hello), so every open and the list's arrival ask again; a window that already has its character is left alone.
    /// </summary>
    private static void GiveDoll(ILegACEyClient client, InventoryWindow window)
    {
        if (!window.HasDoll && client.SupportsAction(PaperdollProtocol.Look)) window.SetDoll(new PaperdollView(client.ServerChannel, client.PortalPath));
    }

    /// <summary>Opens the window of a view, or hides it when the client has it open.</summary>
    private void Show(ILegACEyClient client, InventoryView view)
    {
        var (width, height, sizing) = Shape(view);
        client.ToggleWindowWithChrome(WindowId(view), InventoryWindow.Title, width, height, DefaultLocation,
            close => CreateWindow(client, view, close), new DerethClientTheme(), sizing, DerethWindow.TitleBarHeight,
            view.Collapsed ? WindowId(new InventoryView(view.Layout, false)) : null);
    }

    private Control CreateWindow(ILegACEyClient client, InventoryView view, Action close)
    {
        // The client keeps a closed window hidden and shows it again, so this runs once per session for each view.
        var window = new InventoryWindow(client.Inventory, client.Art, new InventorySettings(view.Layout, Current(client).ShowSlots, view.Collapsed), client.ItemDrag);
        GiveDoll(client, window);
        _windows[view] = window;
        // The close box closes retail's panel too, through its own path, when the takeover holds it. Retail's close then reaches us as
        // closed, which finds the window already hidden.
        window.CloseRequested += (_, _) =>
        {
            Hide(view, close);
            _retail?.Close();
        };
        window.SettingsChanged += settings =>
        {
            // The new settings are the plugin's from here on, even if the client cannot save them, so the other view opens from
            // them and not from what the client reads back. The cache is not reset here: this is not a menu press.
            _settings = settings;
            client.SaveSettings(settings.ToInt());
            if (settings.View == view) return;
            // Hiding keeps this view's size and position. The other view opens with its own saved size.
            Hide(view, close);
            Toggle(client, settings.View);
        };
        window.DetachedFromVisualTree += (_, _) => Released(view, window);
        return window;
    }

    /// <summary>Hides a view's window. The client keeps it, so it comes back as it was.</summary>
    private void Hide(InventoryView view, Action close)
    {
        if (_windows.TryGetValue(view, out var window)) window.Suspend();
        if (_shown == view) _shown = null;
        close();
    }

    /// <summary>The client released a window (the player logged off, or the plugin turned off): forget it and free it.</summary>
    private void Released(InventoryView view, InventoryWindow window)
    {
        if (_windows.TryGetValue(view, out var current) && current == window) _windows.Remove(view);
        if (_shown == view) _shown = null;
        window.Dispose();
    }

    private InventorySettings Current(ILegACEyClient client) => _settings ??= InventorySettings.FromInt(client.LoadSettings());

    private static string WindowId(InventoryView view) => "inventory-" + view.Layout.ToString().ToLowerInvariant() + (view.Collapsed ? "-compact" : string.Empty);

    /// <summary>
    /// Each view's default size and minimum. The minimum keeps the paperdoll, unless it is collapsed, and one row of the grid visible,
    /// and the horizontal one also keeps the pack strip on one row: the strip is the main pack, a divider and seven side packs, 295 px across.
    /// </summary>
    private static (int Width, int Height, WindowResizing Sizing) Shape(InventoryView view) => (view.Layout, view.Collapsed) switch
    {
        (InventoryLayout.Horizontal, false) => (640, 400, new WindowResizing(new Size(600, 330))),
        (InventoryLayout.Horizontal, true) => (420, 320, new WindowResizing(new Size(340, 220))),
        (_, false) => (360, 530, new WindowResizing(new Size(330, 420))),
        _ => (360, 300, new WindowResizing(new Size(280, 200))),
    };
}
