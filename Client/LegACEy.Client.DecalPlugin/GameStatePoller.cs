using System;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Polls Decal character data from the render callback without owning rendering.</summary>
internal sealed class GameStatePoller
{
    private readonly GameStatePort _state;
    private readonly Func<GameStateSnapshot> _read;
    private readonly Action<COMException>? _reportUnavailable;
    private bool _unavailable;

    public GameStatePoller(GameStatePort state, Func<GameStateSnapshot> read, Action<COMException>? reportUnavailable = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _reportUnavailable = reportUnavailable;
    }

    public void Poll(bool isSessionReady)
    {
        // Decal exposes its filter before character stats are readable. Entry/logout
        // rendering must continue without touching that COM boundary.
        if (!isSessionReady) return;
        GameStateSnapshot snapshot;
        try { snapshot = _read(); }
        catch (COMException error)
        {
            // Preserve the last complete snapshot and retry on the next frame.
            // Report once per unavailable interval rather than logging every frame.
            if (!_unavailable) _reportUnavailable?.Invoke(error);
            _unavailable = true;
            return;
        }
        _unavailable = false;
        // Subscriber/rendering failures must still reach the runtime's normal guard.
        _state.Publish(snapshot);
    }
}
