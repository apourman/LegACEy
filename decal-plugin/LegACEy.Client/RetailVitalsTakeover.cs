using System;
using System.Drawing;

namespace LegACEy.Client.DecalPlugin;

/// <summary>LegACEy's vitals window while retail's vitals are held. It is refreshed each frame, and disposed to close it.</summary>
internal interface IRetailVitalsWindow : IDisposable
{
    /// <summary>Shows the values as they stand at <paramref name="now"/>, the player's clock.</summary>
    void Refresh(TimeSpan now);
}

/// <summary>
/// Holds retail's vitals windows hidden and opens LegACEy's vitals window at their place, while the vitals switch is on. Retail's windows
/// are given back the same way on every path: the switch goes off, the post-UI draw is gone, the window fails, the player logs off, and on
/// unload. A failure stays off until the switch is turned off and on again, as chat does.
/// </summary>
internal sealed class RetailVitalsTakeover : IDisposable
{
    private readonly Func<RetailHeldWindows?> _hold;
    private readonly Func<Point> _retailAt;
    private readonly Func<Point, IRetailVitalsWindow?> _open;
    private readonly Action<string> _log;
    private RetailHeldWindows? _held;
    private Point _place;
    private IRetailVitalsWindow? _window;
    private bool _failed;
    private bool _releaseLogged;

    /// <param name="hold">Holds retail's vitals windows. Null when the running client does not match its native entries: retail's stay.</param>
    /// <param name="retailAt">Where retail's vitals stand. Read as they are held, so the window opens where retail's were.</param>
    /// <param name="open">
    /// Opens LegACEy's vitals window at a place. Null while the windows cannot open yet: retail's stay held and the next frame asks again.
    /// It throws only when opening fails, which turns the takeover off until the switch is turned off and on.
    /// </param>
    public RetailVitalsTakeover(Func<RetailHeldWindows?> hold, Func<Point> retailAt, Func<Point, IRetailVitalsWindow?> open, Action<string> log)
    {
        _hold = hold ?? throw new ArgumentNullException(nameof(hold));
        _retailAt = retailAt ?? throw new ArgumentNullException(nameof(retailAt));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Called every frame. Holds retail's vitals and opens the window once the windows can open, or gives them all back.</summary>
    /// <param name="switchOn">Whether the vitals switch is on.</param>
    /// <param name="canDraw">Whether the post-UI draw is running, so a window can be drawn.</param>
    /// <param name="inGame">Whether the character is in the world; the window opens only then.</param>
    public void Tick(bool switchOn, bool canDraw, bool inGame, Size viewport, TimeSpan now)
    {
        try
        {
            if (!switchOn) _failed = false;
            if (_held != null && (!canDraw || _failed || !switchOn))
            {
                Release();
                return;
            }
            if (!canDraw || !switchOn || _failed) return;
            if (_held == null && !Hold()) return;
            _held!.Tick(viewport);
            if (!inGame) return;
            _window ??= _open(_place);
            _window?.Refresh(now);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /// <summary>Whether retail's vitals are held. A give-back that failed is still held until a later one lands.</summary>
    public bool Holds => _held != null;

    /// <summary>Gives retail's vitals back and closes LegACEy's window. The next switch-on, login or frame holds them again.</summary>
    public void Release()
    {
        if (_window != null)
        {
            try { _window.Dispose(); }
            catch (Exception exception) { _log($"Could not close the Dereth vitals window: {exception}"); }
            _window = null;
        }
        if (_held == null) return;
        try { _held.Release(); }
        catch (Exception exception)
        {
            // A give-back that fails is retried on the next call, so retail's vitals are never left hidden.
            if (!_releaseLogged) _log($"Could not give the retail vitals back; retrying: {exception}");
            _releaseLogged = true;
            return;
        }
        _held = null;
        _releaseLogged = false;
    }

    /// <summary>A failure while drawing or opening: LegACEy's window goes, retail's vitals come back, and the takeover stays off.</summary>
    public void Fail(Exception exception)
    {
        _failed = true;
        _log($"Dereth vitals window disabled; retail's vitals are back: {exception}");
        Release();
    }

    public void Dispose() => Release();

    private bool Hold()
    {
        var held = _hold();
        if (held == null) return false;
        // Read before the window holds retail's, so the window's place is retail's own.
        _place = _retailAt();
        _held = held;
        return true;
    }
}
