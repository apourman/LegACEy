using System;
using System.Drawing;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Holds retail's attack panel hidden while melee or missile is the stance, and opens the Dereth attack bar while the attack bar switch is on.
/// Peace and magic leave retail's panel as retail shows it: the hold is given back then, with the panel's own state restored. Retail's panel is
/// also given back when the switch goes off, the post-UI draw is gone, the player is out of the game, a native check fails, the window fails, and
/// on unload. The bar window opens once and shows itself by the stance; its host surface is hidden with it by the runtime.
/// </summary>
internal sealed class RetailAttackTakeover : IDisposable
{
    private readonly Func<RetailAttackNatives?> _natives;
    private readonly Func<RetailHeldWindows> _hold;
    private readonly Func<Point> _retailAt;
    private readonly Func<Point, IDisposable?> _open;
    private readonly Action<string> _log;
    private RetailHeldWindows? _held;
    private IDisposable? _window;
    private bool _failed;
    private bool _releaseLogged;

    /// <param name="natives">The validated attack bar natives; null while they cannot be checked yet, when retail's panel stays as it is.</param>
    /// <param name="hold">Holds retail's attack panel, by its placed id, once the Hold list has passed.</param>
    /// <param name="retailAt">Where retail's attack panel stands, so the bar opens over it.</param>
    /// <param name="open">Opens the bar window at a place. Null while the windows cannot open yet; it throws only when opening fails.</param>
    public RetailAttackTakeover(Func<RetailAttackNatives?> natives, Func<RetailHeldWindows> hold, Func<Point> retailAt, Func<Point, IDisposable?> open,
        Action<string> log)
    {
        _natives = natives ?? throw new ArgumentNullException(nameof(natives));
        _hold = hold ?? throw new ArgumentNullException(nameof(hold));
        _retailAt = retailAt ?? throw new ArgumentNullException(nameof(retailAt));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Whether retail's attack panel is held: hidden by this takeover in melee or missile.</summary>
    public bool Holds => _held?.Holds == true;

    /// <summary>Called every frame, in game or not. Holds retail's panel while melee or missile is shown and the bar is up, and gives it back otherwise.</summary>
    /// <param name="switchOn">Whether the attack bar switch is on.</param>
    /// <param name="canDraw">Whether the post-UI draw is running, so the bar can be drawn.</param>
    /// <param name="inGame">Whether the character is in the world.</param>
    /// <param name="stance">The player's combat stance now.</param>
    /// <param name="viewport">The screen, for the held panel's place.</param>
    public void Tick(bool switchOn, bool canDraw, bool inGame, RetailStance stance, Size viewport)
    {
        try
        {
            if (!switchOn) _failed = false;
            if (!switchOn || !canDraw || !inGame || _failed)
            {
                Release();
                return;
            }
            if (_natives() is not { } natives) return;
            if (!natives.Complete)
            {
                // The validation logged which lists failed; the takeover stays off as a whole, so the bar never shows a dead control.
                _failed = true;
                _log("Dereth attack bar takeover is off: the running client does not match its native entries, so retail's attack panel stays.");
                Release();
                return;
            }
            _window ??= _open(_retailAt());
            if (stance is RetailStance.Melee or RetailStance.Missile)
            {
                _held ??= _hold();
                // Re-asserted each frame: retail may show its panel again on a stance change.
                _held.Tick(viewport);
            }
            else ReleaseHold();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /// <summary>Gives retail's attack panel back and closes the bar window. The next switch-on or stance change holds them again.</summary>
    public void Release()
    {
        CloseWindow();
        ReleaseHold();
    }

    /// <summary>A failure while holding or opening: the bar goes, retail's panel comes back, and the takeover stays off until the switch is turned off and on.</summary>
    public void Fail(Exception exception)
    {
        _failed = true;
        _log($"Dereth attack bar disabled; retail's attack panel is back: {exception}");
        Release();
    }

    public void Dispose() => Release();

    private void CloseWindow()
    {
        if (_window == null) return;
        var window = _window;
        _window = null;
        try { window.Dispose(); }
        catch (Exception exception) { _log($"Could not close the Dereth attack bar: {exception}"); }
    }

    private void ReleaseHold()
    {
        if (_held == null) return;
        try { _held.Release(); }
        catch (Exception exception)
        {
            // A give-back that fails is retried on the next call, so retail's panel is never left hidden.
            if (!_releaseLogged) _log($"Could not give retail's attack panel back; retrying: {exception}");
            _releaseLogged = true;
            return;
        }
        _held = null;
        _releaseLogged = false;
    }
}
