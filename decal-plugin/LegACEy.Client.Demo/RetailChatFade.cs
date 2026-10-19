using System;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Dereth chat window's idle fade. The caller says whether the window is awake (the pointer is over it, its box has the keyboard, a
/// talk-target menu is open, or it is being moved or resized). Awake, everything shows. Left alone for <see cref="IdleAfterSeconds"/>, the
/// window is idle: the frame, tabs, input and scrollbar fade out over <see cref="ChromeFadeSeconds"/>, and each line holds for
/// <see cref="LineStaySeconds"/> after it arrived and then fades over <see cref="LineFadeSeconds"/>. A line that had already held its time
/// when the window went idle fades from that moment. The time is passed in, so the rule runs without sleeping.
/// </summary>
public sealed class RetailChatFade
{
    /// <summary>How long a window is left alone before it is idle, in seconds, when the player has chosen no delay.</summary>
    public const int DefaultIdleAfterSeconds = 15;
    /// <summary>How long the frame, tabs, input and scrollbar take to fade out once the window is idle.</summary>
    public const double ChromeFadeSeconds = 0.25;
    private const double LineStaySeconds = 10;
    private const double LineFadeSeconds = 0.4;

    // When the window was first left alone, since its last awake moment; null while it is awake.
    private DateTime? _quietSince;

    /// <summary>How long the window is left alone before it is idle, in seconds; null keeps it awake. A change applies at once.</summary>
    public double? IdleAfterSeconds { get; set; } = DefaultIdleAfterSeconds;

    /// <summary>Applies the idle rule at <paramref name="now"/>; call it before reading the opacities.</summary>
    public void Update(bool awake, DateTime now)
    {
        if (awake) _quietSince = null;
        else _quietSince ??= now;
        Idle = IdleFrom <= now;
    }

    /// <summary>Whether the window is idle, as of the last <see cref="Update"/>.</summary>
    public bool Idle { get; private set; }

    /// <summary>The opacity of the frame, tabs, input and scrollbar at <paramref name="now"/>.</summary>
    public double ChromeOpacity(DateTime now) =>
        IdleFrom is { } since ? Clamp01(1 - (now - since).TotalSeconds / ChromeFadeSeconds) : 1;

    /// <summary>
    /// The opacity of a line that arrived at <paramref name="arrived"/>, at <paramref name="now"/>: full while awake. When idle, the line
    /// fades from the later of its stay's end and the moment the window went idle.
    /// </summary>
    public double LineOpacity(DateTime arrived, DateTime now)
    {
        if (IdleFrom is not { } since) return 1;
        var fadeFrom = arrived.AddSeconds(LineStaySeconds);
        if (since > fadeFrom) fadeFrom = since;
        return Clamp01(1 - (now - fadeFrom).TotalSeconds / LineFadeSeconds);
    }

    /// <summary>When the window goes idle; null while it is awake or never fades.</summary>
    private DateTime? IdleFrom => _quietSince is { } quiet && IdleAfterSeconds is { } seconds ? quiet.AddSeconds(seconds) : null;

    private static double Clamp01(double value) => Math.Min(1, Math.Max(0, value));
}
