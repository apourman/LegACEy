using System;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Polls the character's vitals from the render callback without owning rendering.</summary>
internal sealed class GameStatePoller
{
    private readonly GameStatePort _state;
    private readonly Func<GameStateSnapshot?> _read;
    private readonly Action? _reportUnavailable;
    private bool _unavailable;

    public GameStatePoller(GameStatePort state, Func<GameStateSnapshot?> read, Action? reportUnavailable = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _reportUnavailable = reportUnavailable;
    }

    public void Poll(bool isSessionReady)
    {
        // The game has no player to read before the session is up: entry/logout rendering must continue without reading.
        if (!isSessionReady) return;
        var snapshot = _read();
        if (snapshot == null)
        {
            // Preserve the last complete snapshot and retry on the next frame.
            // Report once per unavailable interval rather than logging every frame.
            if (!_unavailable) _reportUnavailable?.Invoke();
            _unavailable = true;
            return;
        }
        _unavailable = false;
        // Subscriber/rendering failures must still reach the runtime's normal guard.
        _state.Publish(snapshot);
    }
}
