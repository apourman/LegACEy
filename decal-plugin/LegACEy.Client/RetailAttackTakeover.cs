using System;
using System.Drawing;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Holds retail's attack panel hidden and opens the Dereth attack bar while melee or missile is the stance and the attack bar switch is on.
/// Peace and magic close the bar and leave retail's panel as retail shows it: the hold is given back then, with the panel's own state restored.
/// The bar closes on the same paths the panel is given back: the switch goes off, the post-UI draw is gone, the player is out of the game, a
/// native check fails, the window fails, and unload. A closed bar keeps its place, as the window store saves it on close.
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

    /// <summary>
    /// Called every frame, in game or not. While the bar is up (the switch on, the post-UI draw running, in game, in melee or missile) the bar is
    /// open and retail's panel is held; otherwise both are given back.
    /// </summary>
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
            var up = switchOn && canDraw && inGame && !_failed && (stance == RetailStance.Melee || stance == RetailStance.Missile);
            if (!up)
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
            // Retail's panel is held only while the bar is open; until the window can open, retail's panel stays as it is.
            if (_window == null) return;
            _held ??= _hold();
            // Re-asserted each frame: retail may show its panel again on a stance change.
            _held.Tick(viewport);
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
