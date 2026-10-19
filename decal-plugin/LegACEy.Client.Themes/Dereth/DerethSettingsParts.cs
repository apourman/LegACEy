using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// A row of options in a groove, one of them chosen and lit gold. A click on an option chooses it and raises <see cref="Chosen"/> with its index.
/// </summary>
public sealed class DerethSegmented : Border
{
    private static readonly IBrush LitBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x29));

    private readonly List<Border> _cells = new();
    private readonly List<TextBlock> _labels = new();
    private int _selected;

    public DerethSegmented(IReadOnlyList<string> options)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", options.Select(_ => "*"))) };
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var label = new TextBlock
            {
                Text = options[i], FontFamily = DerethPalette.Body, FontSize = 12, Foreground = DerethPalette.MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var cell = new Border { Background = Brushes.Transparent, Padding = new Thickness(8, 4), Child = label };
            cell.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed || index == Selected) return;
                Selected = index;
                Chosen?.Invoke(index);
            };
            Grid.SetColumn(cell, i);
            row.Children.Add(cell);
            _cells.Add(cell);
            _labels.Add(label);
        }
        Background = DerethPalette.GrooveBrush;
        BorderBrush = DerethPalette.GrooveEdgeBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(3);
        Child = row;
        Light();
    }

    /// <summary>The index of the chosen option. Setting it lights that option; no event is raised.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Light();
        }
    }

    /// <summary>The player chose an option, by its index.</summary>
    public event Action<int>? Chosen;

    private void Light()
    {
        for (var i = 0; i < _cells.Count; i++)
        {
            var lit = i == _selected;
            _cells[i].Background = lit ? LitBrush : Brushes.Transparent;
            _labels[i].Foreground = lit ? DerethPalette.CreamBrush : DerethPalette.MutedBrush;
        }
    }
}

/// <summary>
/// A horizontal slider in the Dereth look: a groove filled in gold up to a cream thumb. It takes values from <paramref name="minimum"/> to
/// <paramref name="maximum"/> in steps of <paramref name="step"/>. The thumb follows a drag, and <see cref="ValueChanged"/> is raised as it
/// moves. <see cref="Committed"/> is raised with the value when the pointer lets go, if the value moved; a drag that loses the pointer's
/// capture is cancelled, and the thumb goes back where it was pressed.
/// </summary>
public sealed class DerethSlider : Control
{
    private const double Thumb = 12;

    private readonly int _minimum;
    private readonly int _maximum;
    private readonly int _step;
    private int _value;
    private bool _dragging;
    private int _pressedValue;

    public DerethSlider(int minimum, int maximum, int step)
    {
        _minimum = minimum;
        _maximum = maximum;
        _step = step;
        _value = minimum;
        Height = 16;
        PointerCaptureLost += (_, _) => CancelDrag();
    }

    /// <summary>The value the thumb shows, kept within the range. Setting it moves the thumb without raising <see cref="Committed"/>.</summary>
    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Max(_minimum, Math.Min(_maximum, value));
            InvalidateVisual();
        }
    }

    /// <summary>The thumb moved, by a drag or a press, to this value.</summary>
    public event Action<int>? ValueChanged;

    /// <summary>The player let go after moving the thumb, with the value it stopped on.</summary>
    public event Action<int>? Committed;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        if (width <= Thumb) return;
        var middle = Bounds.Height / 2;
        var reach = width - Thumb;
        var thumbX = Thumb / 2 + reach * (_value - _minimum) / (_maximum - _minimum);
        var track = new Rect(Thumb / 2, middle - 2, reach, 4);
        context.FillRectangle(DerethPalette.GrooveBrush, track);
        context.FillRectangle(DerethPalette.GoldBrush, new Rect(track.X, track.Y, thumbX - track.X, track.Height));
        context.DrawEllipse(DerethPalette.CreamBrush, new Pen(DerethPalette.GoldBrush, 1.5), new Point(thumbX, middle), Thumb / 2 - 1, Thumb / 2 - 1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _pressedValue = _value;
        e.Pointer.Capture(this);
        MoveTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) MoveTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        // Releasing the capture raises PointerCaptureLost, which finds the drag already over.
        e.Pointer.Capture(null);
        if (_value != _pressedValue) Committed?.Invoke(_value);
    }

    private void CancelDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Value = _pressedValue;
        ValueChanged?.Invoke(_value);
    }

    private void MoveTo(double x)
    {
        var fraction = Math.Max(0, Math.Min(1, (x - Thumb / 2) / (Bounds.Width - Thumb)));
        var value = _minimum + (int)Math.Round(fraction * (_maximum - _minimum) / _step) * _step;
        if (value == _value) return;
        Value = value;
        ValueChanged?.Invoke(_value);
    }
}

/// <summary>
/// A small tag that says whether a setting applies to every character on the install or only to the one playing. The tag for this character
/// is edged in green.
/// </summary>
public sealed class DerethScopeTag : Border
{
    private static readonly IBrush AllEdge = DerethPalette.GrooveEdgeBrush;
    private static readonly IBrush ThisEdge = DerethPalette.Brush(DerethPalette.Teal.WithAlpha(0x73));
    private static readonly IBrush ThisText = DerethPalette.Brush(DerethPalette.TealText);

    public DerethScopeTag(string text, bool thisCharacter)
    {
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        BorderBrush = thisCharacter ? ThisEdge : AllEdge;
        Padding = new Thickness(6, 1);
        VerticalAlignment = VerticalAlignment.Center;
        Child = new TextBlock
        {
            Text = text, FontFamily = DerethPalette.Body, FontSize = 10,
            Foreground = thisCharacter ? ThisText : DerethPalette.MutedBrush
        };
    }
}

/// <summary>
/// An on/off switch in the Dereth look: a groove with a cream knob, lit gold and knob to the right when on. A click flips it and raises
/// <see cref="Toggled"/> with the new value.
/// </summary>
public sealed class DerethToggle : Border
{
    private readonly Border _knob = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = DerethPalette.CreamBrush };
    private bool _on;

    public DerethToggle()
    {
        Width = 30;
        Height = 16;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(1);
        Cursor = new Cursor(StandardCursorType.Hand);
        Child = _knob;
        Light();
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            On = !On;
            Toggled?.Invoke(On);
            e.Handled = true;
        };
    }

    /// <summary>Whether the switch is on. Setting it moves the knob; no event is raised.</summary>
    public bool On
    {
        get => _on;
        set
        {
            _on = value;
            Light();
        }
    }

    /// <summary>The player clicked the switch, which now has this value.</summary>
    public event Action<bool>? Toggled;

    private void Light()
    {
        Background = _on ? DerethPalette.GoldDarkBrush : DerethPalette.GrooveBrush;
        BorderBrush = _on ? DerethPalette.GoldBrush : DerethPalette.GrooveEdgeBrush;
        _knob.HorizontalAlignment = _on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
