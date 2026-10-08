using System;
using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>Host boundary implemented by the game plugin and faked by framework tests.</summary>
public interface IClientUiHost
{
    IDisposable OpenWindow(WindowDefinition definition, Control content, Point requestedLocation);
}

/// <summary>
/// Feature-facing entry point for opening LegACEy windows.
/// It owns all registrations so logoff, failure and unload share one cleanup path.
/// </summary>
public sealed class ClientUiFramework : IDisposable
{
    private readonly IClientUiHost _host;
    private readonly Dictionary<string, IDisposable> _windows = new(StringComparer.Ordinal);
    private bool _ended;

    public ClientUiFramework(IClientUiHost host, IGameStatePort gameState, IClientTheme theme, IServerChannel? serverChannel = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        GameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        Theme = theme ?? throw new ArgumentNullException(nameof(theme));
        ServerChannel = serverChannel ?? UnavailableServerChannel.Instance;
    }

    public IGameStatePort GameState { get; }
    /// <summary>The in-band LegACEy server channel. Requests and subscriptions belong to the feature that made them.</summary>
    public IServerChannel ServerChannel { get; }
    public IClientTheme Theme { get; }
    public int OpenWindowCount => _windows.Count;

    public bool OpenWindow(WindowDefinition definition, Control content, Point? requestedLocation = null)
    {
        EnsureActive();
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (content == null) throw new ArgumentNullException(nameof(content));
        if (_windows.ContainsKey(definition.Id)) return false;
        try { _windows.Add(definition.Id, _host.OpenWindow(definition, content, requestedLocation ?? new Point(120, 70))); }
        catch { EndSession(); throw; }
        return true;
    }

    public bool CloseWindow(string id)
    {
        if (!_windows.TryGetValue(id, out var registration)) return false;
        registration.Dispose();
        _windows.Remove(id);
        return true;
    }

    public void EndSession()
    {
        _ended = true;
        Exception? first = null;
        foreach (var pair in new List<KeyValuePair<string, IDisposable>>(_windows))
            try { pair.Value.Dispose(); _windows.Remove(pair.Key); } catch (Exception error) { first ??= error; }
        if (first != null) throw new InvalidOperationException("One or more client UI registrations failed to clean up.", first);
    }

    public void Dispose() => EndSession();

    private void EnsureActive()
    {
        if (_ended) throw new ObjectDisposedException(nameof(ClientUiFramework));
    }
}
