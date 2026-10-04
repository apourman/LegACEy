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
    IDisposable TakeOverRoot(uint rootElementId, Control content);
    bool MoveRoot(uint rootElementId, Point location);
    void ApplyTheme(IClientTheme theme);
}

/// <summary>
/// Feature-facing entry point for opening LegACEy windows and retail-root replacements.
/// It owns all registrations so logoff, failure and unload share one cleanup path.
/// </summary>
public sealed class ClientUiFramework : IDisposable
{
    private readonly IClientUiHost _host;
    private readonly Dictionary<string, IDisposable> _windows = new(StringComparer.Ordinal);
    private readonly List<TakeoverRegistration> _takeovers = new();
    private IClientTheme _theme;
    private bool _ended;

    public ClientUiFramework(IClientUiHost host, IGameStatePort gameState, IClientTheme theme)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        GameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
    }

    public IGameStatePort GameState { get; }
    public IClientTheme Theme => _theme;
    public int OpenWindowCount => _windows.Count;
    public int TakeoverCount => _takeovers.Count;

    public void SetTheme(IClientTheme theme)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _host.ApplyTheme(theme);
    }

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

    public IDisposable TakeOverRoot(uint rootElementId, Control content)
    {
        EnsureActive();
        if (content == null) throw new ArgumentNullException(nameof(content));
        try
        {
            var hostRegistration = _host.TakeOverRoot(rootElementId, content);
            var registration = new TakeoverRegistration(this, hostRegistration);
            _takeovers.Add(registration);
            return registration;
        }
        catch { EndSession(); throw; }
    }

    public bool MoveRoot(uint rootElementId, Point location)
    {
        EnsureActive();
        try { return _host.MoveRoot(rootElementId, location); }
        catch { EndSession(); throw; }
    }

    private void ReleaseTakeover(TakeoverRegistration registration)
    {
        if (!_takeovers.Contains(registration)) return;
        registration.DisposeHost();
        _takeovers.Remove(registration);
    }

    public void EndSession()
    {
        _ended = true;
        Exception? first = null;
        foreach (var pair in new List<KeyValuePair<string, IDisposable>>(_windows))
            try { pair.Value.Dispose(); _windows.Remove(pair.Key); } catch (Exception error) { first ??= error; }
        for (var i = _takeovers.Count - 1; i >= 0; i--)
            try { _takeovers[i].DisposeHost(); _takeovers.RemoveAt(i); } catch (Exception error) { first ??= error; }
        if (first != null) throw new InvalidOperationException("One or more client UI registrations failed to clean up.", first);
    }

    public void Dispose() => EndSession();

    private void EnsureActive()
    {
        if (_ended) throw new ObjectDisposedException(nameof(ClientUiFramework));
    }

    private sealed class TakeoverRegistration : IDisposable
    {
        private readonly ClientUiFramework _owner;
        private IDisposable? _hostRegistration;
        public TakeoverRegistration(ClientUiFramework owner, IDisposable hostRegistration)
        { _owner = owner; _hostRegistration = hostRegistration; }
        public void Dispose() => _owner.ReleaseTakeover(this);
        public void DisposeHost()
        {
            var registration = _hostRegistration;
            registration?.Dispose();
            _hostRegistration = null;
        }
    }
}
