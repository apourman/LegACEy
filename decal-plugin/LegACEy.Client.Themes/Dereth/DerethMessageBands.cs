using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace LegACEy.Client.Themes;

/// <summary>
/// Short game messages ("The chest is locked!") as the game message mockups' soft band (look 2), in the smoke of the band mockups (look B):
/// one line of cream Palatino with room above it, on a cloud of dark smoke that thins out to every side and drifts slowly while it shows.
/// Up to three show at once, newest on top; older ones dim their words more than their smoke and go first. A repeated message takes its own row,
/// as retail's floating lines stack. Each shows for its lifetime and fades over the last moment. The
/// client's panels run Avalonia's jobs but not its timers, so the host ticks it once a frame. It is hidden while it shows nothing. While
/// <see cref="Moving"/>, a sample band on top shows where they go, outlined in gold, for as long as the player places them.
/// </summary>
public sealed class DerethMessageBands : StackPanel
{
    public const int MaxShown = 3;
    /// <summary>The text size bounds, the step the setting moves in and the size before one is set, in percent.</summary>
    public const int SmallestTextSize = 60, LargestTextSize = 200, TextSizeStep = 10, DefaultTextSize = 100;
    public const string SampleText = "Game messages show here";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Fade = TimeSpan.FromSeconds(0.3);
    private const double OlderText = 0.6, OlderSmoke = 0.8;
    private static readonly Color Shade = Color.FromRgb(0x0C, 0x0B, 0x07);
    // The smoke spills past each band by this much, at the design's size: the bands' margin keeps it on their surface, and stacked bands
    // overlap into one plume.
    private static readonly Size Spill = new(44, 14);

    private readonly Func<DateTime> _clock;
    private DateTime _lastTick;
    private double _textScale = 1;

    /// <param name="clock">The time, for each message's lifetime; null for the system's.</param>
    public DerethMessageBands(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _lastTick = _clock();
        Margin = SpillMargin(1);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        IsVisible = false;
    }

    /// <summary>A band as the game shows one, at a text size in percent, for the setting's preview.</summary>
    public static Control Sample(string text, int textSize) => new Band(text, textSize / 100.0) { Margin = SpillMargin(textSize / 100.0) };

    private static Thickness SpillMargin(double scale) => new(Spill.Width * scale, Spill.Height * scale);

    /// <summary>The text size, as a scale on the design's: every band, shown and to come, draws at it.</summary>
    public double TextScale
    {
        get => _textScale;
        set
        {
            _textScale = value;
            Margin = SpillMargin(value);
            foreach (var child in Children) ((Band)child).Scale(value);
        }
    }

    /// <summary>Whether the player is placing the bands: the sample band shows on top until this goes false.</summary>
    public bool Moving
    {
        get => Children.Count > 0 && ((Band)Children[0]).IsSample;
        set
        {
            if (value == Moving) return;
            if (value) Children.Insert(0, new Band(SampleText, _textScale, sample: true));
            else Children.RemoveAt(0);
            Tick();
        }
    }

    /// <summary>Shows a message on top, a repeat on its own row as retail's floating lines stack; the oldest goes past three.</summary>
    public void Show(string text)
    {
        Tick();
        var band = new Band(text, _textScale) { Remaining = Lifetime };
        var top = Moving ? 1 : 0;
        Children.Insert(top, band);
        while (Children.Count - top > MaxShown) Children.RemoveAt(Children.Count - 1);
        Tick();
    }

    /// <summary>Runs every message's time down, drops the ones that have run out and fades the last moment. The host calls this once a frame.</summary>
    public void Tick()
    {
        var now = _clock();
        var elapsed = now - _lastTick;
        _lastTick = now;
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var band = (Band)Children[i];
            band.Drift(elapsed);
            if (band.IsSample) continue;
            band.Remaining -= elapsed;
            if (band.Remaining <= TimeSpan.Zero) Children.RemoveAt(i);
        }
        var newest = Moving ? 1 : 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var band = (Band)Children[i];
            if (band.IsSample) continue;
            var fade = band.Remaining < Fade ? band.Remaining.TotalSeconds / Fade.TotalSeconds : 1;
            band.Opacity = fade;
            band.Older = i != newest;
        }
        IsVisible = Children.Count > 0;
    }

    private sealed class Band : Decorator
    {
        // The smoke: a dense core behind the words, and soft puffs that slide left at their own speeds and thin out towards every edge, so
        // one leaving at the left fades out before it comes back in at the right. Each band draws its own puffs, so a stack never repeats.
        // The puffs fade out pixel by pixel to nothing at the smoke's edge, so none is cut off square by the surface; the core is gone
        // well inside it.
        private const int PuffCount = 9;
        private const double DriftSpeed = 16;
        private static readonly IBrush Core = Radial((0, 0.9), (0.3, 0.9), (0.45, 0.72), (0.56, 0.38), (0.7, 0.1), (0.8, 0));
        private static readonly IBrush PuffFill = Radial((0, 1), (0.4, 0.7), (1, 0));
        private static readonly IBrush EdgeFade = Radial((0, 1), (0.3, 1), (0.55, 0.7), (0.75, 0.3), (0.9, 0.08), (1, 0));
        private static readonly Random Seeds = new();
        private static readonly IPen SampleOutline = new ImmutablePen(DerethPalette.GoldBrush.ToImmutable());
        private readonly TextBlock _text;
        private readonly (double X, double Y, double RadiusX, double RadiusY, double Alpha, double Speed)[] _puffs = new (double, double, double, double, double, double)[PuffCount];
        private double _scale = 1, _driftSeconds;

        public Band(string text, double scale, bool sample = false)
        {
            IsSample = sample;
            HorizontalAlignment = HorizontalAlignment.Center;
            var random = new Random(Seeds.Next());
            for (var i = 0; i < PuffCount; i++)
                _puffs[i] = (random.NextDouble(), (random.NextDouble() - 0.5) * 0.56, 0.18 + random.NextDouble() * 0.22, 0.3 + random.NextDouble() * 0.3,
                    0.3 + random.NextDouble() * 0.3, 0.6 + random.NextDouble() * 0.8);
            Child = _text = new TextBlock { Text = text, FontFamily = DerethPalette.Title, Foreground = DerethPalette.CreamBrush };
            Scale(scale);
        }

        public bool IsSample { get; }
        public TimeSpan Remaining { get; set; }

        /// <summary>An older band dims its words more than its smoke, so it stays readable over bright sky.</summary>
        public bool Older
        {
            get => _text.Opacity < 1;
            set
            {
                if (value == Older) return;
                _text.Opacity = value ? OlderText : 1;
                InvalidateVisual();
            }
        }

        public void Scale(double scale)
        {
            _scale = scale;
            _text.FontSize = 17 * scale;
            Padding = new Thickness(96 * scale, 16 * scale, 96 * scale, 11 * scale);
            InvalidateVisual();
        }

        public void Drift(TimeSpan elapsed)
        {
            _driftSeconds += elapsed.TotalSeconds;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var smoke = new Rect(Bounds.Size).Inflate(new Thickness(Spill.Width * _scale, Spill.Height * _scale));
            using (context.PushOpacity(Older ? OlderSmoke : 1))
            {
                context.DrawEllipse(Core, null, smoke);
                using var edge = context.PushOpacityMask(EdgeFade, smoke);
                foreach (var puff in _puffs)
                {
                    // Across 1.4 widths, so a puff wraps where it cannot be seen.
                    var travel = puff.X - _driftSeconds * DriftSpeed * _scale * puff.Speed / smoke.Width;
                    var x = (travel - Math.Floor(travel)) * 1.4 - 0.7;
                    var fromCentre = Math.Sqrt(4 * x * x + 4 * puff.Y * puff.Y);
                    var thinning = Math.Min(1, (1 - fromCentre) / 0.7);
                    if (thinning <= 0) continue;
                    using (context.PushOpacity(puff.Alpha * thinning))
                        context.DrawEllipse(PuffFill, null, smoke.Center + new Vector(x * smoke.Width, puff.Y * smoke.Height),
                            puff.RadiusX * smoke.Width, puff.RadiusY * smoke.Height);
                }
            }
            if (IsSample) context.DrawRectangle(null, SampleOutline, new Rect(Bounds.Size).Deflate(0.5));
        }

        private static IBrush Radial(params (double Offset, double Alpha)[] stops)
        {
            var brush = new RadialGradientBrush();
            foreach (var (offset, alpha) in stops) brush.GradientStops.Add(new GradientStop(Shade.WithAlpha((byte)Math.Round(alpha * 255)), offset));
            return brush.ToImmutable();
        }
    }
}
