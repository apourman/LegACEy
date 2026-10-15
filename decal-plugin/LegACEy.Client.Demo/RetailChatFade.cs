using System;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Dereth chat window's idle fade. The window is awake while the pointer is over it, its box has the keyboard, a talk-target menu
/// is open, or retail's UI is unlocked for moving windows (so the frame can be found). Awake, everything shows. Idle, the frame, tabs,
/// input and scrollbar fade out over <see cref="ChromeFadeSeconds"/>, and each line holds for <see cref="LineStaySeconds"/> after it
/// arrived and then fades. The clock is injected, so the rule runs without sleeping.
/// </summary>
public sealed class RetailChatFade
{
    /// <summary>How long the frame, tabs, input and scrollbar take to fade out once the window is idle.</summary>
    public const double ChromeFadeSeconds = 0.25;
    /// <summary>How long a line stays at full opacity after it arrived, while the window is idle.</summary>
    public const double LineStaySeconds = 10;
    /// <summary>How long a line takes to fade out once it has stayed. The mockup's line fade.</summary>
    public const double LineFadeSeconds = 0.4;

    private readonly Func<DateTime> _now;
    private DateTime? _idleSince;

    public RetailChatFade(Func<DateTime> now) => _now = now ?? throw new ArgumentNullException(nameof(now));

    /// <summary>Applies the idle rule now; call it before reading the opacities. <paramref name="uiUnlocked"/> is asked only when nothing else keeps the window awake.</summary>
    public void Update(bool pointerOver, bool inputFocused, bool menuOpen, Func<bool> uiUnlocked)
    {
        var awake = pointerOver || inputFocused || menuOpen || uiUnlocked();
        if (awake) _idleSince = null;
        else _idleSince ??= _now();
    }

    /// <summary>The opacity of the frame, tabs, input and scrollbar.</summary>
    public double ChromeOpacity => _idleSince is { } since ? Clamp01(1 - (_now() - since).TotalSeconds / ChromeFadeSeconds) : 1;

    /// <summary>The opacity of a line that arrived at <paramref name="arrived"/>: full while awake, and after its stay when idle.</summary>
    public double LineOpacity(DateTime arrived) =>
        _idleSince == null ? 1 : Clamp01(1 - ((_now() - arrived).TotalSeconds - LineStaySeconds) / LineFadeSeconds);

    private static double Clamp01(double value) => Math.Min(1, Math.Max(0, value));
}
