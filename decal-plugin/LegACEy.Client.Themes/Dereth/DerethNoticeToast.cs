using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>A toast's icon, which also tints its ring: a search, a new thing, done, wait, a failure and a question to think twice about.</summary>
public enum ToastIcon { Search, New, Done, Wait, Failed, Caution }

/// <summary>
/// A toast's button: its label and what it does. A quiet one is drawn muted, beside the main one. Unavailable says why the button can't be
/// used now, or null when it can; the toast asks it every frame and again on the click, and greys the button with the reason beside it.
/// </summary>
public sealed record ToastAction(string Label, Action Run, bool Quiet = false, Func<string?>? Unavailable = null);

/// <summary>
/// What a toast shows: an icon, a title, a line or two of body, buttons, and how long it stays. No lifetime waits for the player; busy
/// shows a moving bar under the title.
/// </summary>
public sealed record ToastNotice(ToastIcon Icon, string Title, string? Body = null, IReadOnlyList<ToastAction>? Actions = null,
    TimeSpan? Lifetime = null, bool Busy = false);

/// <summary>
/// A Dereth toast, as the update toast mockups draw it: a small stone plaque with a ringed icon, a title, a body, buttons and a × that closes
/// it. It shows one notice at a time, a new one in place of the last. A notice with a lifetime hides itself once that has run down, which
/// the thin line along the bottom shows; it pauses while the pointer is over the toast. It knows nothing about what it tells. The client's
/// panels run Avalonia's jobs but not its timers, so the host ticks it once a frame.
/// </summary>
public sealed class DerethNoticeToast : Border
{
    public const double CardWidth = 360;
    // The busy bar's track: the card less its two borders, margins, icon column and gap.
    private const double TrackWidth = CardWidth - 86;
    // The bar's block runs across in this time, over and over, while the toast is busy.
    private static readonly TimeSpan BusyCycle = TimeSpan.FromSeconds(1.4);
    // The last moment of a toast's life, when it fades out.
    private static readonly TimeSpan Fade = TimeSpan.FromSeconds(0.3);
    private static readonly IBrush Stone = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#1C1912"), 0), new GradientStop(DerethPalette.Navy, 0.45), new GradientStop(Color.Parse("#120F0A"), 1) }
    };
    private static readonly IBrush IconFill = new RadialGradientBrush
    {
        GradientOrigin = new RelativePoint(0.5, 0.4, RelativeUnit.Relative), Center = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#2E2719"), 0), new GradientStop(Color.Parse("#120F0A"), 1) }
    };
    private static readonly IBrush BusyFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(DerethPalette.GoldDark, 0), new GradientStop(DerethPalette.Gold, 1) }
    };

    private readonly Func<DateTime> _clock;
    private readonly Border _card = new() { BorderBrush = DerethPalette.GoldDarkBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Background = Stone };
    private Border? _life;
    private Border? _busy;
    // The buttons that can be unavailable, and the line that says why.
    private readonly List<(Button Button, ToastAction Action)> _gated = new();
    private TextBlock? _why;
    private ToastNotice? _notice;
    private TimeSpan _left;
    private DateTime _lastTick;

    /// <param name="clock">The time, for the lifetime and the busy bar; null for the system's.</param>
    public DerethNoticeToast(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        Width = CardWidth;
        VerticalAlignment = VerticalAlignment.Top;
        // a black hairline outside the gold, as the mockup's ring
        BorderBrush = Brushes.Black;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Child = _card;
        IsVisible = false;
    }

    /// <summary>Shows a notice in place of the one showing, with its full lifetime.</summary>
    public void Show(ToastNotice notice)
    {
        _notice = notice;
        _left = notice.Lifetime ?? TimeSpan.Zero;
        _lastTick = _clock();
        _card.Child = Build(notice);
        Opacity = 1;
        IsVisible = true;
        Tick();
    }

    /// <summary>Runs the lifetime down, unless the pointer is over the toast, and moves the busy bar. The host calls this once a frame.</summary>
    public void Tick()
    {
        var now = _clock();
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (!IsVisible || _notice == null) return;
        Gate();
        if (_busy != null)
            Canvas.SetLeft(_busy, ((double)(now.Ticks % BusyCycle.Ticks) / BusyCycle.Ticks * 1.3 - 0.3) * TrackWidth);
        if (_notice.Lifetime is not { } lifetime) return;
        if (!IsPointerOver) _left -= elapsed;
        if (_left <= TimeSpan.Zero)
        {
            IsVisible = false;
            return;
        }
        _life!.RenderTransform = new ScaleTransform(_left.TotalSeconds / lifetime.TotalSeconds, 1);
        Opacity = _left < Fade ? _left.TotalSeconds / Fade.TotalSeconds : 1;
    }

    private void Gate()
    {
        string? why = null;
        foreach (var (button, action) in _gated)
        {
            var reason = action.Unavailable!();
            button.IsEnabled = reason == null;
            why ??= reason;
        }
        if (_why == null) return;
        _why.Text = why;
        _why.IsVisible = why != null;
    }

    private Control Build(ToastNotice notice)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("38,*,20"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            ColumnSpacing = 14, Margin = new Thickness(16, 14, 14, 16)
        };
        var (glyph, ink, ring) = Look(notice.Icon);
        var icon = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), BorderBrush = DerethPalette.Brush(ring),
            Background = IconFill, VerticalAlignment = VerticalAlignment.Top,
            Child = new DerethGlyphIcon(glyph, ink) { Width = 19, Height = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        Grid.SetRowSpan(icon, 4);
        grid.Children.Add(icon);

        // a title on its own stands in the middle of the icon
        if (notice.Body == null && notice.Actions is not { Count: > 0 } && !notice.Busy) grid.RowDefinitions[0].MinHeight = 36;
        var title = new TextBlock
        {
            Text = notice.Title, FontFamily = DerethPalette.Title, FontSize = 16, Foreground = DerethPalette.CreamBrush, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var close = new DerethPagerButton(DerethSpriteArt.Close) { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        close.Click += (_, _) => IsVisible = false;
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        if (notice.Body != null)
        {
            var body = new TextBlock
            {
                Text = notice.Body, FontFamily = DerethPalette.Body, FontSize = 12.5, LineHeight = 18, Foreground = DerethPalette.MutedBrush,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            };
            Place(grid, body, 1);
        }
        if (notice.Busy)
        {
            _busy = new Border { Width = TrackWidth * 0.3, Height = 4, Background = BusyFill };
            var track = new Border
            {
                Height = 6, Margin = new Thickness(0, 12, 0, 0), Background = DerethPalette.GrooveBrush, BorderBrush = DerethPalette.GrooveEdgeBrush,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = new Canvas { Children = { _busy } }
            };
            Place(grid, track, 2);
        }
        else _busy = null;
        _gated.Clear();
        _why = null;
        if (notice.Actions is { Count: > 0 } actions)
        {
            // a long label wraps the next button onto its own line rather than past the card
            var row = new WrapPanel { ItemSpacing = 10, LineSpacing = 8 };
            foreach (var action in actions)
            {
                var button = new DerethButton
                {
                    // DerethButton's frame ignores Padding, so the label carries the room; narrow enough that two buttons fit a row with room to spare,
                    // or rounding at the UI size wraps the second while the toast is sized and leaves an empty row
                    Content = new TextBlock { Text = action.Label, Margin = new Thickness(12, 6) }, FontFamily = DerethPalette.Title, FontSize = 13,
                    Foreground = action.Quiet ? DerethPalette.MutedBrush : DerethPalette.CreamBrush
                };
                button.Click += (_, _) =>
                {
                    if (action.Unavailable?.Invoke() == null) action.Run();
                    else Gate();
                };
                if (action.Unavailable != null) _gated.Add((button, action));
                row.Children.Add(button);
            }
            var line = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(row, Dock.Left);
            line.Children.Add(row);
            if (_gated.Count > 0)
            {
                _why = new TextBlock
                {
                    FontFamily = DerethPalette.Body, FontSize = 11.5, Foreground = DerethPalette.MutedBrush, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
                };
                line.Children.Add(_why);
            }
            Place(grid, line, 3);
        }

        var whole = new Panel();
        whole.Children.Add(grid);
        _life = null;
        if (notice.Lifetime != null)
        {
            // the time left: a line along the bottom edge that runs down from the right
            _life = new Border { Height = 2, Background = DerethPalette.GoldDarkBrush, RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative) };
            whole.Children.Add(new Border
            {
                Height = 2, Margin = new Thickness(3, 0, 3, 2), VerticalAlignment = VerticalAlignment.Bottom, Background = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x26)),
                Child = _life
            });
        }
        return whole;
    }

    private static void Place(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        Grid.SetColumnSpan(control, 2);
        grid.Children.Add(control);
    }

    // the mockup's gold rim, around the gold icons, and its warning amber
    private static readonly Color GoldRim = Color.Parse("#C99A45");
    private static readonly Color Amber = Color.Parse("#E0A04A");

    private static (DerethGlyph Glyph, Color Ink, Color Ring) Look(ToastIcon icon) => icon switch
    {
        ToastIcon.Search => (DerethGlyph.Search, DerethPalette.Gold, GoldRim),
        ToastIcon.New => (DerethGlyph.Gem, DerethPalette.Gold, GoldRim),
        ToastIcon.Done => (DerethGlyph.Tick, DerethPalette.TealText, Color.Parse("#3C7A4A")),
        ToastIcon.Wait => (DerethGlyph.Hourglass, Amber, DerethPalette.GoldDark),
        ToastIcon.Caution => (DerethGlyph.Warning, Amber, DerethPalette.GoldDark),
        _ => (DerethGlyph.Warning, DerethPalette.Invalid, Color.Parse("#7A3A2C")),
    };
}
