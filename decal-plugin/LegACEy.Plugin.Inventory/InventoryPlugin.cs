using System;
using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Inventory;

/// <summary>The inventory window, standing in for retail's inventory panel: it opens and closes with retail's panel.</summary>
public sealed class InventoryPlugin : ILegACEyPlugin
{
    private static readonly Point DefaultLocation = new(240, 100);
    // The id the vertical layout's window had, so its saved place and size still apply.
    private const string WindowId = "inventory-vertical";

    // The window while the client keeps it, open or hidden. A hidden window keeps its content.
    private InventoryWindow? _window;
    // The retail panel this window stands in for. It does nothing while the client's takeover is off, and then retail's panel shows.
    private IRetailPanel? _retail;
    // The settings while the window is open: read from the client when it opens, and every change is saved as it happens.
    // The cache is the character's, and the character logged in now may not be the one it came from, so a retail report clears it
    // while the window is closed (the next read reaches the client).
    private InventorySettings? _settings;
    // Whether the plugin last showed the window, so a retail report knows whether it hides the window or opens it.
    private bool _shown;

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
            if (_shown && _window != null) GiveDoll(client, _window);
        });
    }

    /// <summary>Retail opened or closed its inventory panel (by key, toolbar or item) while the takeover is on.</summary>
    private void RetailPanelChanged(ILegACEyClient client, bool open)
    {
        if (open == _shown) return;
        if (!_shown) _settings = null;
        Toggle(client);
    }

    /// <summary>Opens the window, or hides it when it is showing.</summary>
    private void Toggle(ILegACEyClient client)
    {
        if (!_shown)
        {
            // A kept window takes the Slots choice made since it was hidden, and draws what changed while it was hidden.
            if (_window != null)
            {
                GiveDoll(client, _window);
                _window.SetShowSlots(Current(client).ShowSlots);
                _window.Resume();
            }
        }
        else _window?.Suspend();
        _shown = !_shown;
        client.ToggleWindowWithChrome(WindowId, InventoryWindow.Title, Width, DefaultHeight, DefaultLocation, close => CreateWindow(client, close),
            new DerethClientTheme(), Sizing, DerethWindow.CentredTitleBarHeight);
    }

    /// <summary>
    /// Gives a window its 3D character once its look is known: the game sent it, or the server lists the look action. The list can arrive
    /// after a window was built (the window opens before channel.hello), so every open and the list's arrival ask again; a window that already has its character is left alone.
    /// </summary>
    private static void GiveDoll(ILegACEyClient client, InventoryWindow window)
    {
        if (!window.HasDoll && client.Looks.Knows(0)) window.SetDoll(new PaperdollView(client.Looks, client.PortalPath) { Still = true });
    }

    private Control CreateWindow(ILegACEyClient client, Action close)
    {
        // The client keeps a closed window hidden and shows it again, so this runs once per session.
        var window = new InventoryWindow(client.Inventory, client.Art, new InventorySettings(Current(client).ShowSlots), client.ItemDrag);
        GiveDoll(client, window);
        _window = window;
        // The close box closes retail's panel too, through its own path, when the takeover holds it. Retail's close then reaches us as
        // closed, which finds the window already hidden.
        window.CloseRequested += (_, _) =>
        {
            window.Suspend();
            _shown = false;
            close();
            _retail?.Close();
        };
        window.InspectRequested += () => client.Inspect(0, string.Empty, null);
        window.SettingsChanged += settings =>
        {
            // The new settings are the plugin's from here on, even if the client cannot save them.
            _settings = settings;
            client.SaveSettings(settings.ToInt());
        };
        window.DetachedFromVisualTree += (_, _) => Released(window);
        return window;
    }

    /// <summary>The client released the window (the player logged off, or the plugin turned off): forget it and free it.</summary>
    private void Released(InventoryWindow window)
    {
        if (_window == window)
        {
            _window = null;
            _shown = false;
        }
        window.Dispose();
    }

    private InventorySettings Current(ILegACEyClient client) => _settings ??= InventorySettings.FromInt(client.LoadSettings());

    // Retail's toolbar window width, so the inventory stacked on the toolbar lines up with it.
    private const int Width = (int)RetailToolbar.RetailWidth;

    /// <summary>
    /// The window's default height and its resizing. It is retail's panel, so only its height changes: retail's width, the paperdoll and
    /// grid with the pack column on their right, opening with the grid's six columns eleven rows deep, as retail's does; the minimum keeps
    /// the paperdoll and one row of the grid visible. Scaling, by the UI size or Ctrl and the wheel, changes the whole window.
    /// </summary>
    private const int DefaultHeight = 766;
    private static readonly WindowResizing Sizing = new(new Size(Width, 410), new Size(Width, 10_000));
}
