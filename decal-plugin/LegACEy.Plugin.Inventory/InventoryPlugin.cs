using System;
using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Inventory;

/// <summary>
/// The inventory window, standing in for retail's inventory panel: it opens and closes with retail's panel. With its equipment
/// section collapsed it is another window with its own size, but it stays where the full one is, so collapsing and expanding keep
/// the window in place.
/// </summary>
public sealed class InventoryPlugin : ILegACEyPlugin
{
    private static readonly Point DefaultLocation = new(240, 100);

    // The window of each view, full (false) or collapsed (true), while the client keeps it, open or hidden. A hidden window keeps its content.
    private readonly Dictionary<bool, InventoryWindow> _windows = new();
    // The retail panel this window stands in for. It does nothing while the client's takeover is off, and then retail's panel shows.
    private IRetailPanel? _retail;
    // The settings while a window is open: read from the client when a window opens, and every change is saved as it happens.
    // The cache is the character's, and the character logged in now may not be the one it came from, so a retail report clears it
    // while no window is open (the next read reaches the client); a view switch never clears it.
    private InventorySettings? _settings;
    // The view whose window the plugin last showed, so a retail report knows whether it hides the window or opens one.
    private bool? _shown;

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
            if (_shown is { } collapsed && _windows.TryGetValue(collapsed, out var window)) GiveDoll(client, window);
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
        Toggle(client, Current(client).Collapsed);
    }

    /// <summary>Opens the window of a view, or hides it when it is the one showing.</summary>
    private void Toggle(ILegACEyClient client, bool collapsed)
    {
        var opening = _shown != collapsed;
        if (opening)
        {
            // A kept window takes the Slots choice made since it was hidden, and draws what changed while it was hidden.
            if (_windows.TryGetValue(collapsed, out var kept))
            {
                GiveDoll(client, kept);
                kept.SetShowSlots(Current(client).ShowSlots);
                kept.Resume();
            }
        }
        else if (_windows.TryGetValue(collapsed, out var shown))
        {
            shown.Suspend();
        }
        _shown = opening ? collapsed : null;
        Show(client, collapsed);
    }

    /// <summary>
    /// Gives a window its 3D character once its look is known: the game sent it, or the server lists the look action. The list can arrive
    /// after a window was built (the window opens before channel.hello), so every open and the list's arrival ask again; a window that already has its character is left alone.
    /// </summary>
    private static void GiveDoll(ILegACEyClient client, InventoryWindow window)
    {
        if (!window.HasDoll && client.Looks.Knows(0)) window.SetDoll(new PaperdollView(client.Looks, client.PortalPath) { Still = true });
    }

    /// <summary>Opens the window of a view, or hides it when the client has it open.</summary>
    private void Show(ILegACEyClient client, bool collapsed)
    {
        var (width, height, sizing) = Shape(collapsed);
        client.ToggleWindowWithChrome(WindowId(collapsed), InventoryWindow.Title, width, height, DefaultLocation,
            close => CreateWindow(client, collapsed, close), new DerethClientTheme(), sizing, DerethWindow.CentredTitleBarHeight,
            collapsed ? WindowId(false) : null);
    }

    private Control CreateWindow(ILegACEyClient client, bool collapsed, Action close)
    {
        // The client keeps a closed window hidden and shows it again, so this runs once per session for each view.
        var window = new InventoryWindow(client.Inventory, client.Art, new InventorySettings(Current(client).ShowSlots, collapsed), client.ItemDrag);
        GiveDoll(client, window);
        _windows[collapsed] = window;
        // The close box closes retail's panel too, through its own path, when the takeover holds it. Retail's close then reaches us as
        // closed, which finds the window already hidden.
        window.CloseRequested += (_, _) =>
        {
            Hide(collapsed, close);
            _retail?.Close();
        };
        window.InspectRequested += () => client.Inspect(0, string.Empty, null);
        window.SettingsChanged += settings =>
        {
            // The new settings are the plugin's from here on, even if the client cannot save them, so the other view opens from
            // them and not from what the client reads back. The cache is not reset here: this is not a menu press.
            _settings = settings;
            client.SaveSettings(settings.ToInt());
            if (settings.Collapsed == collapsed) return;
            // Hiding keeps this view's size and position. The other view opens with its own saved size.
            Hide(collapsed, close);
            Toggle(client, settings.Collapsed);
        };
        window.DetachedFromVisualTree += (_, _) => Released(collapsed, window);
        return window;
    }

    /// <summary>Hides a view's window. The client keeps it, so it comes back as it was.</summary>
    private void Hide(bool collapsed, Action close)
    {
        if (_windows.TryGetValue(collapsed, out var window)) window.Suspend();
        if (_shown == collapsed) _shown = null;
        close();
    }

    /// <summary>The client released a window (the player logged off, or the plugin turned off): forget it and free it.</summary>
    private void Released(bool collapsed, InventoryWindow window)
    {
        if (_windows.TryGetValue(collapsed, out var current) && current == window) _windows.Remove(collapsed);
        if (_shown == collapsed) _shown = null;
        window.Dispose();
    }

    private InventorySettings Current(ILegACEyClient client) => _settings ??= InventorySettings.FromInt(client.LoadSettings());

    // The ids the vertical layout's windows had, so their saved places and sizes still apply.
    private static string WindowId(bool collapsed) => "inventory-vertical" + (collapsed ? "-compact" : string.Empty);

    private const int VerticalWidth = 300, TallestVertical = 10_000;

    /// <summary>
    /// Each view's default size and minimum. The minimum keeps the paperdoll, unless it is collapsed, and one row of the grid visible.
    /// They are retail's panel, so only their height changes: retail's width, the paperdoll and grid with the pack column on their
    /// right, and the full one opens with the grid's six columns eleven rows deep, as retail's does. Scaling, by the UI size or Ctrl and
    /// the wheel, changes the whole window.
    /// </summary>
    private static (int Width, int Height, WindowResizing Sizing) Shape(bool collapsed) => collapsed switch
    {
        false => (VerticalWidth, 794, new WindowResizing(new Size(VerticalWidth, 431), new Size(VerticalWidth, TallestVertical))),
        _ => (VerticalWidth, 276, new WindowResizing(new Size(VerticalWidth, 210), new Size(VerticalWidth, TallestVertical))),
    };
}
