using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// Short game messages ("The chest is locked!") as the game message mockups' soft band (look 2): one line of cream Palatino on a dark band
/// that fades out to both sides. Up to three show at once, newest on top; older ones dim and go first. A message already showing counts up
/// (×2, ×3) and starts its time again instead of taking another row. Each shows for its lifetime and fades over the last moment. The
/// client's panels run Avalonia's jobs but not its timers, so the host ticks it once a frame. It is hidden while it shows nothing.
/// </summary>
public sealed class DerethMessageBands : StackPanel
{
    public const int MaxShown = 3;
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

    /// <summary>Shows a message on top, or counts up the one already showing it; the oldest goes past three.</summary>
    public void Show(string text)
    {
        Tick();
        Band? band = null;
        foreach (var child in Children)
            if (child is Band { Text: var shown } same && shown == text) band = same;
        if (band != null) Children.Remove(band);
        else band = new Band(text);
        band.Repeat();
        Children.Insert(0, band);
        while (Children.Count > MaxShown) Children.RemoveAt(Children.Count - 1);
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
            band.Left -= elapsed;
            if (band.Left <= TimeSpan.Zero) Children.RemoveAt(i);
        }
        for (var i = 0; i < Children.Count; i++)
        {
            var band = (Band)Children[i];
            var fade = band.Left < Fade ? band.Left.TotalSeconds / Fade.TotalSeconds : 1;
            band.Opacity = fade * (i == 0 ? 1 : OlderOpacity);
        }
        IsVisible = Children.Count > 0;
    }

    private sealed class Band : Border
    {
        private readonly Run _count = new() { FontSize = 12, Foreground = DerethPalette.GoldBrush };
        private int _times;

        public Band(string text)
        {
            Text = text;
            Background = BandFill;
            Padding = new Thickness(70, 6);
            HorizontalAlignment = HorizontalAlignment.Center;
            Child = new TextBlock
            {
                FontFamily = DerethPalette.Title, FontSize = 17, Foreground = DerethPalette.CreamBrush,
                Inlines = new InlineCollection { new Run(text), _count }
            };
        }

        public string Text { get; }
        public TimeSpan Left { get; set; }

        public void Repeat()
        {
            _times++;
            _count.Text = _times > 1 ? $"  ×{_times}" : string.Empty;
            Left = Lifetime;
        }
    }
}
