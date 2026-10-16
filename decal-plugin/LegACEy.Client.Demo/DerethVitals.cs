using System;
using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>The three vitals, in retail's order.</summary>
public enum Vital { Health, Stamina, Mana }

/// <summary>The vitals window's layout: stacked (A, the default) or side by side (D).</summary>
public enum VitalsLayout { Stacked = 0, SideBySide = 1 }

/// <summary>One vital's bar as the window shows it: how full it is, how far its pale trail reaches, and its numbers.</summary>
public readonly struct VitalReading
{
    internal VitalReading(Vital vital, int current, int max, double trailFraction, bool pulsing)
    {
        Vital = vital;
        Current = current;
        Max = max;
        TrailFraction = trailFraction;
        Pulsing = pulsing;
    }

    public Vital Vital { get; }
    public string Name => Vital.ToString();
    public int Current { get; }
    public int Max { get; }

    /// <summary>How full the bar is: current over max, from 0 to 1.</summary>
    public double Fraction => Max > 0 ? Math.Min(1, Math.Max(0, Current / (double)Max)) : 0;

    /// <summary>How far the pale trail reaches: the fraction the bar was at before its last drop, draining down to <see cref="Fraction"/>.</summary>
    public double TrailFraction { get; }

    /// <summary>Health under a quarter: the bar's edge pulses.</summary>
    public bool Pulsing { get; }

    public string Label => $"{Current} / {Max}";
    public string Tooltip => $"{Name} {Label}";
}

/// <summary>
/// The vitals' values and their trails, from the game's state each frame. A drop leaves the lost part pale for a moment, then it drains
/// to the current value; a rise closes the trail at once. Health pulses under a quarter.
/// </summary>
public sealed class VitalsModel
{
    /// <summary>Health below this fraction of its max pulses.</summary>
    public const double LowFraction = 0.25;
    /// <summary>How long a drop's trail holds before it drains.</summary>
    public static readonly TimeSpan TrailHold = TimeSpan.FromMilliseconds(650);
    /// <summary>How fast a trail drains, as a fraction of the vital's max per second.</summary>
    public const double TrailDrainPerSecond = 0.4;

    private readonly Dictionary<Vital, Track> _tracks = new()
    {
        [Vital.Health] = new Track(), [Vital.Stamina] = new Track(), [Vital.Mana] = new Track()
    };

    private readonly VitalReading[] _bars = new VitalReading[3];

    /// <summary>The bars as of the last <see cref="Update"/>, in retail's order. Filled in place, so an update allocates nothing.</summary>
    public IReadOnlyList<VitalReading> Bars => _bars;

    /// <summary>Reads the game's values at <paramref name="now"/>, the player's clock.</summary>
    public void Update(GameStateSnapshot state, TimeSpan now)
    {
        _bars[0] = Read(Vital.Health, state.Health, state.MaxHealth, now);
        _bars[1] = Read(Vital.Stamina, state.Stamina, state.MaxStamina, now);
        _bars[2] = Read(Vital.Mana, state.Mana, state.MaxMana, now);
    }

    /// <summary>The pulse's strength at <paramref name="now"/>: a cycle a second, from 0 to 1.</summary>
    public double PulseAt(TimeSpan now) => 0.5 + 0.5 * Math.Sin(2 * Math.PI * now.TotalSeconds);

    private VitalReading Read(Vital vital, int current, int max, TimeSpan now)
    {
        var track = _tracks[vital];
        if (!track.Seen)
        {
            track.Seen = true;
            track.Last = current;
            track.Trail = current;
            track.Updated = now;
            track.HoldUntil = now;
        }
        if (current < track.Last)
        {
            track.Trail = Math.Max(track.Trail, track.Last);
            track.HoldUntil = now + TrailHold;
        }
        // The trail drains from the later of the last update and the end of its hold.
        var drainFrom = track.Updated > track.HoldUntil ? track.Updated : track.HoldUntil;
        if (now > drainFrom && track.Trail > current)
            track.Trail = Math.Max(current, track.Trail - TrailDrainPerSecond * max * (now - drainFrom).TotalSeconds);
        if (track.Trail < current) track.Trail = current;
        track.Last = current;
        track.Updated = now;

        var fraction = max > 0 ? Math.Min(1, Math.Max(0, current / (double)max)) : 0;
        var trailFraction = max > 0 ? Math.Min(1, track.Trail / max) : 0;
        return new VitalReading(vital, current, max, trailFraction, vital == Vital.Health && fraction < LowFraction);
    }

    /// <summary>One vital's history between updates.</summary>
    private sealed class Track
    {
        public bool Seen;
        public int Last;
        public double Trail;
        public TimeSpan Updated;
        public TimeSpan HoldUntil;
    }
}
