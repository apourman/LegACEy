using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// The Market's toast: a title and a line in a green-edged card, as the mockups draw it. It doesn't block; it hides on a click, or at the first
/// <see cref="Tick"/> after <see cref="ShownFor"/>. The client's panels run Avalonia's jobs but not its timers, so the window showing the toast
/// ticks it once a frame. The Market window shows it over its own contents; the client shows it over the game for a sale.
/// </summary>
public sealed class DerethToast : Border
{
    /// <summary>How long a toast shows before it hides itself</summary>
    public static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(8);

    public const double CardWidth = 330;

    private readonly TextBlock _title = DerethItemCells.Label(string.Empty, DerethPalette.CreamBrush, 13);
    private readonly TextBlock _line = DerethItemCells.Label(string.Empty, DerethPalette.Brush(Color.Parse("#A9D9B5")), 12);
    private readonly Func<DateTime> _clock;
    // a new toast starts the time again
    private DateTime _hideAt;

    /// <param name="clock">The time, for hiding after <see cref="ShownFor"/>; null for the system's.</param>
    public DerethToast(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _line.TextWrapping = TextWrapping.Wrap;
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(_title);
        text.Children.Add(_line);
        Width = CardWidth;
        Padding = new Thickness(14, 12);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        BorderBrush = DerethPalette.TealBrush;
        Background = DerethPalette.Brush(Color.Parse("#17301C"));
        IsVisible = false;
        Child = text;
        PointerPressed += (_, _) => Hide();
    }

    /// <summary>Raised when the toast hides itself, after a while or on a click.</summary>
    public event Action? Hidden;

    /// <summary>The title and the line, or empty while the toast is hidden</summary>
    public string Text => IsVisible ? $"{_title.Text} {_line.Text}" : string.Empty;

    public void Show(string title, string line)
    {
        _title.Text = title;
        _line.Text = line;
        IsVisible = true;
        _hideAt = _clock() + ShownFor;
    }

    /// <summary>Hides the toast once it has shown for <see cref="ShownFor"/>. The window showing it calls this once a frame.</summary>
    public void Tick()
    {
        if (IsVisible && _clock() >= _hideAt) Hide();
    }

    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        Hidden?.Invoke();
    }
}
