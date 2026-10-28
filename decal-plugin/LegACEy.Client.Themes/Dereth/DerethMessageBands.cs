using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// Short game messages ("The chest is locked!") as the game message mockups' soft band (look 2): one line of cream Palatino on a dark band
/// that fades out to both sides. Up to three show at once, newest on top; older ones dim and go first. A repeated message takes its own row,
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
    private const double OlderOpacity = 0.6;
    private static readonly Color Shade = Color.FromArgb(0xD1, 0x0C, 0x0B, 0x07);
    private static readonly IBrush BandFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Shade.WithAlpha(0), 0), new GradientStop(Shade, 0.22), new GradientStop(Shade, 0.78), new GradientStop(Shade.WithAlpha(0), 1)
        }
    };

    private readonly Func<DateTime> _clock;
    private DateTime _lastTick;
    private double _textScale = 1;

    /// <param name="clock">The time, for each message's lifetime; null for the system's.</param>
    public DerethMessageBands(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _lastTick = _clock();
        Spacing = 6;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        IsVisible = false;
    }

    /// <summary>A band as the game shows one, at a text size in percent, for the setting's preview.</summary>
    public static Control Sample(string text, int textSize) => new Band(text, textSize / 100.0);

    /// <summary>The text size, as a scale on the design's: every band, shown and to come, draws at it.</summary>
    public double TextScale
    {
        get => _textScale;
        set
        {
            _textScale = value;
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
            band.Opacity = fade * (i == newest ? 1 : OlderOpacity);
        }
        IsVisible = Children.Count > 0;
    }

    private sealed class Band : Border
    {
        private readonly TextBlock _text;

        public Band(string text, double scale, bool sample = false)
        {
            IsSample = sample;
            Background = BandFill;
            HorizontalAlignment = HorizontalAlignment.Center;
            if (sample)
            {
                BorderBrush = DerethPalette.GoldBrush;
                BorderThickness = new Thickness(1);
            }
            Child = _text = new TextBlock { Text = text, FontFamily = DerethPalette.Title, Foreground = DerethPalette.CreamBrush };
            Scale(scale);
        }

        public bool IsSample { get; }
        public TimeSpan Remaining { get; set; }

        public void Scale(double scale)
        {
            _text.FontSize = 17 * scale;
            Padding = new Thickness(70 * scale, 6 * scale);
        }
    }
}
