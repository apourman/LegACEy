using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Market;

/// <summary>
/// The Market window: the Dereth frame and header around the shell. The window measures: its width picks the layout mode.
/// Esc closes it, and "/" focuses the search; the window takes the keyboard as it opens.
/// </summary>
public sealed class MarketShellWindow : UserControl, IDisposable, IWindowShortcuts, IFrameTick
{
    private readonly MarketShellPanel _panel;

    public event EventHandler? CloseRequested;

    public MarketShellWindow(MarketShellPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        // the dark variant of the base theme, so a control the Market doesn't draw itself (a menu, a combo's list) is never light
        var frame = new DerethWindow(MarketPlugin.Title, Seal(), panel, panel.TopBar, stretchActions: true, iconHeader: true);
        frame.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Content = new ThemeVariantScope { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, Child = frame };
        SizeChanged += (_, e) => _panel.SetMode(MarketLayout.ModeFor(e.NewSize.Width));
        Focusable = true;
        AttachedToVisualTree += (_, _) => Focus();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The Market's seal: gold scales on a red disc with a gold rim.</summary>
    private static Control Seal()
    {
        var disc = new Avalonia.Controls.Border
        {
            Width = 24, Height = 24, CornerRadius = new Avalonia.CornerRadius(12), BorderThickness = new Avalonia.Thickness(1),
            BorderBrush = MarketInk.GoldRimBrush, Child = new MarketGlyph(MarketIcon.Scales, DerethPalette.Gold, 14)
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            },
            Background = new Avalonia.Media.RadialGradientBrush
            {
                Center = new Avalonia.RelativePoint(0.4, 0.35, Avalonia.RelativeUnit.Relative),
                GradientOrigin = new Avalonia.RelativePoint(0.4, 0.35, Avalonia.RelativeUnit.Relative),
                GradientStops = { new(Avalonia.Media.Color.Parse("#7A1A12"), 0), new(Avalonia.Media.Color.Parse("#3A0805"), 1) }
            },
        };
        return disc;
    }

    /// <summary>The panel the window draws, for the window's own tests.</summary>
    public MarketShellPanel Panel => _panel;

    /// <summary>Esc and "/". "/" is a character, not a shortcut, while any text box has the keyboard.</summary>
    public bool WantsShortcut(Key key) => key == Key.Escape || (key == Key.Oem2 && !TextBoxHasFocus());

    private bool TextBoxHasFocus() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    public bool TakesFocusOnOpen => true;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // an open buy dialog is cancelled first, as its Cancel does
            if (_panel.BrowseState.Buying != null) _panel.BrowseState.CloseBuy();
            else CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Oem2 && !_panel.SearchHasFocus && !TextBoxHasFocus())
        {
            _panel.FocusSearch();
            e.Handled = true;
        }
    }

    public void Tick() => _panel.Tick();

    public void Dispose() => _panel.Dispose();
}
