using System;
using System.Collections.Generic;
using System.Drawing;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The Dereth container window in place of retail's container view, held as the magic panel is: retail shows its view when the player
/// opens a chest, corpse or ground container and hides it when the container closes (a walk away, its close box, or ours), and our
/// window follows. What the container holds comes from the server's messages (<see cref="ContainerState"/>); the items' actions go through
/// the inventory port, as the inventory's cells do. The window has two layouts, and the player's choice is kept per character under
/// <see cref="LayoutKey"/>, where /saveui layouts carry it with the character's other window settings. Call everything on the game thread.
/// </summary>
internal sealed class ContainerFeature : IDisposable
{
    /// <summary>The character's saved layout: X is 1 for the grid and 0 (or no row) for retail's row.</summary>
    internal const string LayoutKey = "container-layout";

    private readonly ILegACEyPluginHost _host;
    private readonly RetailPanelTakeover? _panel;
    private readonly IWindowPositionStore _store;
    private readonly Func<(string Server, string Character)> _who;
    private readonly Action<string> _beginMove;
    private readonly Action<string> _log;
    private readonly Dictionary<ContainerLayout, ContainerWindow> _views = new();
    private readonly Dictionary<ContainerLayout, Action> _hides = new();
    private ContainerLayout? _shown;

    public ContainerFeature(ILegACEyPluginHost host, IRetailPanelPort? port, IWindowPositionStore store, Func<(string Server, string Character)> who,
        Action<string> beginMove, Action<string> log)
    {
        _host = host;
        _store = store;
        _who = who;
        _beginMove = beginMove;
        _log = log;
        if (port != null) _panel = new RetailPanelTakeover(port, OpenChanged, log);
    }

    public ContainerState State { get; } = new();

    /// <summary>The layout the character chose.</summary>
    public ContainerLayout Layout
    {
        get
        {
            var (server, character) = _who();
            return _store.Load(server, character, LayoutKey)?.Location.X == 1 ? ContainerLayout.Grid : ContainerLayout.Strip;
        }
    }

    /// <summary>Once a frame in game. With <paramref name="enabled"/> false retail's view is given back, and our window closes.</summary>
    public void Tick(bool enabled, bool canShow) => _panel?.Tick(enabled, canShow);

    /// <summary>Logoff, unload or the windows failing: gives retail's view back and drops the windows.</summary>
    public void EndSession()
    {
        _panel?.EndSession();
        Release();
    }

    public void Dispose()
    {
        _panel?.Dispose();
        Release();
    }

    private void Release()
    {
        foreach (var view in _views.Values) view.Dispose();
        _views.Clear();
        _hides.Clear();
        _shown = null;
    }

    private void OpenChanged(bool open)
    {
        if (!open)
        {
            Hide();
            return;
        }
        if (_shown != null) return;
        try { Open(Layout); }
        catch (Exception exception)
        {
            _log($"The container window could not open: {exception.Message}");
            _panel?.Close();
        }
    }

    private void Hide()
    {
        if (_shown is not { } layout) return;
        _shown = null;
        if (_views.TryGetValue(layout, out var view)) view.Suspend();
        if (_hides.TryGetValue(layout, out var hide)) hide();
    }

    private static WindowDefinition Definition(ContainerLayout layout) => layout == ContainerLayout.Strip
        ? new WindowDefinition(ContainerWindow.StripId, ContainerWindow.Title, ContainerWindow.Width, ContainerWindow.StripHeight, titleBarHeight: 1,
            theme: new DerethClientTheme(), dragsFromContent: true)
        : new WindowDefinition(ContainerWindow.GridId, ContainerWindow.Title, ContainerWindow.Width, ContainerWindow.GridHeight, DerethWindow.IconTitleBarHeight,
            new DerethClientTheme(), ContainerWindow.GridSizing, sharesLocationWith: ContainerWindow.StripId);

    private void Open(ContainerLayout layout)
    {
        // Without the game's art the cells show a question mark, which beats closing the player's container.
        IGameArtSource art;
        try { art = _host.Art; }
        catch (InvalidOperationException) { art = new NoArt(); }
        var definition = Definition(layout);
        if (!_views.TryGetValue(layout, out var view))
        {
            view = _views[layout] = new ContainerWindow(layout, State, _host.Inventory, art, _host.ItemDrag, () => _beginMove(definition.Id));
            view.ToggleRequested += Toggle;
            view.CloseRequested += Close;
        }
        var place = _panel?.HeldPlace ?? new Point(200, 150);
        var current = view;
        var opened = _host.OpenWindow(definition, place, close =>
        {
            _hides[layout] = close;
            return current;
        }, error =>
        {
            _log($"The container window failed: {error.Message}");
            _views.Remove(layout);
            if (_shown == layout) _shown = null;
            current.Dispose();
            _panel?.Close();
        }, ownChrome: true);
        if (!opened)
        {
            view.Suspend();
            _panel?.Close();
            return;
        }
        _shown = layout;
        view.Resume();
    }

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }

    // The close box closes retail's view through its own path, which closes the container; retail's report then finds the window hidden.
    private void Close()
    {
        if (_panel?.Holds == true) _panel.Close();
        Hide();
    }

    // Switches to the other layout: the window in view hides and the other opens, in the same place.
    private void Toggle()
    {
        var next = _shown == ContainerLayout.Strip ? ContainerLayout.Grid : ContainerLayout.Strip;
        var (server, character) = _who();
        try { _store.Save(server, character, LayoutKey, (new Point(next == ContainerLayout.Grid ? 1 : 0, 0), null)); }
        catch (Exception exception) { _log($"Could not save the container layout: {exception.Message}"); }
        var wasShown = _shown != null;
        Hide();
        if (!wasShown) return;
        try { Open(next); }
        catch (Exception exception)
        {
            _log($"The container window could not open: {exception.Message}");
            _panel?.Close();
        }
    }
}
