using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The appraisal window: a Dereth window whose header carries the item's icon and name, drawing an <see cref="AppraisalContent"/>. The
/// summary, then the sections (each a gold heading over a rule), then the long description in italics under a rule. The body scrolls
/// past <see cref="MaxHeight"/>, and the inscription stays pinned under it. One instance serves the session: <see cref="TryShow"/> swaps in
/// the next appraisal while it fits the height the window has.
/// </summary>
public sealed class AppraisalWindow
{
    public const int Width = 330;
    /// <summary>The tallest the window grows before its body scrolls.</summary>
    public const int MaxHeight = 520;
    // The Dereth frame and header, the title rule and the body's bottom margin, around the body.
    private const int Chrome = DerethWindow.TitleBarHeight + 5 + 6 + 8;
    // The frame and the window's side margins, and the body's padding, around the text.
    private const int BodyInset = 40;

    private readonly DerethWindow _window;
    private readonly ScrollViewer _scroll;
    private readonly ContentControl _foot;

    private AppraisalWindow(DerethWindow window, ScrollViewer scroll, ContentControl foot, int height)
    {
        _window = window;
        _scroll = scroll;
        _foot = foot;
        Height = height;
    }

    public DerethWindow Window => _window;

    /// <summary>The height the window was built at: its body and inscription plus the chrome, up to <see cref="MaxHeight"/>.</summary>
    public int Height { get; }

    /// <summary>Runs when the player closes the window with its close box.</summary>
    public Action? Close { get; set; }

    /// <summary>
    /// The window for an appraisal, at the height its content needs. <paramref name="inscribe"/> saves an inscription on an item and returns
    /// whether it was sent.
    /// </summary>
    public static AppraisalWindow Create(AppraisalContent content, IGameArtSource? art, Func<uint, string, bool> inscribe)
    {
        var layout = Layout(content, inscribe);
        var scroll = Scroll(layout.Body);
        var foot = new ContentControl { Content = layout.Foot };
        var window = new DerethWindow(content.Name, Icon(content.Visual, art), Frame(scroll, foot));
        var view = new AppraisalWindow(window, scroll, foot, layout.Height);
        window.CloseRequested += (_, _) => view.Close?.Invoke();
        return view;
    }

    /// <summary>
    /// Shows another appraisal in this window, when its content fits the height the window has. Returns false, with the window unchanged,
    /// when it needs another height: the window's size cannot change once it is open.
    /// </summary>
    public bool TryShow(AppraisalContent content, IGameArtSource? art, Func<uint, string, bool> inscribe)
    {
        var layout = Layout(content, inscribe);
        if (layout.Height != Height) return false;
        _window.Title = content.Name;
        _window.SetIcon(Icon(content.Visual, art));
        _scroll.Content = layout.Body;
        _foot.Content = layout.Foot;
        return true;
    }

    /// <summary>The body and the inscription for the content, and the height the window needs to show all of it.</summary>
    private static (Control Body, Control? Foot, int Height) Layout(AppraisalContent content, Func<uint, string, bool> inscribe)
    {
        var body = new StackPanel { Margin = new Thickness(10, 6, 10, 6), Spacing = 2 };
        if (content.TypeLine.Length > 0) body.Children.Add(Text(content.TypeLine, DerethPalette.CreamBrush, 14));
        if (content.Figures.Count > 0) body.Children.Add(Figures(content.Figures));
        foreach (var section in content.Sections)
        {
            body.Children.Add(Heading(section.Heading));
            foreach (var row in section.Rows) body.Children.Add(Row(row));
        }
        if (content.Description.Length > 0)
        {
            body.Children.Add(new DerethRule { Margin = new Thickness(0, 8, 0, 6) });
            body.Children.Add(new TextBlock
            {
                Text = content.Description, Foreground = DerethPalette.MutedBrush, FontStyle = FontStyle.Italic, FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });
        }
        var foot = content.Inscription is { } inscription ? Inscription(inscription, content.ObjectId, inscribe) : null;
        // A window measured before it is shown has no template yet, so the parts are measured and the chrome added to them.
        body.Measure(new Size(Width - BodyInset, double.PositiveInfinity));
        foot?.Measure(new Size(Width - BodyInset, double.PositiveInfinity));
        var needed = Chrome + (int)Math.Ceiling(body.DesiredSize.Height) + (foot == null ? 0 : (int)Math.Ceiling(foot.DesiredSize.Height));
        return (body, foot, Math.Min(MaxHeight, needed));
    }

    /// <summary>The frame's content: the scrolling body, and the inscription pinned under it.</summary>
    private static Grid Frame(Control scroll, Control foot)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(scroll);
        Grid.SetRow(foot, 1);
        grid.Children.Add(foot);
        return grid;
    }

    private static ScrollViewer Scroll(Control body) => new()
    {
        Content = body,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    private static Control? Icon(ItemVisual? visual, IGameArtSource? art)
    {
        if (visual == null || art == null) return null;
        var image = ItemIcon.Draw(art, visual.Underlay, visual.Icon, visual.Overlay, 0, visual.UiEffects, visual.Plate);
        return DerethItemCells.Icon(GameArtImageExtension.CreateBitmap(image));
    }

    /// <summary>The summary figures side by side, each a value over its label, sharing the width evenly.</summary>
    private static Control Figures(IReadOnlyList<AppraisalFigure> figures)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", figures.Select(_ => "*"))), Margin = new Thickness(0, 4, 0, 4) };
        for (var i = 0; i < figures.Count; i++)
        {
            var figure = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { Text(figures[i].Value, DerethPalette.CreamBrush, 16, centred: true), Text(figures[i].Label, DerethPalette.MutedBrush, 11, centred: true) }
            };
            Grid.SetColumn(figure, i);
            grid.Children.Add(figure);
        }
        return grid;
    }

    /// <summary>A section heading in gold, with the rule running from it to the window's edge.</summary>
    private static Control Heading(string text)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 8, 0, 2), ColumnSpacing = 8 };
        var title = Text(text, DerethPalette.GoldBrush, 14, font: DerethPalette.Title);
        title.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(title);
        var rule = new DerethRule { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rule, 1);
        heading.Children.Add(rule);
        return heading;
    }

    /// <summary>A label on the left and its value on the right, or a line of text across the section when there is no label.</summary>
    private static Control Row(AppraisalRow row)
    {
        var brush = ToneBrush(row.Tone);
        if (row.Label == null) return new TextBlock { Text = row.Text, Foreground = brush, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(Text(row.Label, DerethPalette.MutedBrush, 13));
        var value = Text(row.Text, brush, 13);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    /// <summary>
    /// The inscription at the foot: an editable field the character writes in, or the text and its scribe read only. A read-only item that
    /// nobody inscribed says so.
    /// </summary>
    private static Control Inscription(AppraisalInscription inscription, uint itemId, Func<uint, string, bool> inscribe)
    {
        if (inscription.Mode == InscriptionMode.Editable) return Editable(inscription.Text, text => inscribe(itemId, text));
        var panel = new StackPanel { Margin = new Thickness(10, 6, 10, 6), Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = inscription.Text.Length > 0 ? $"“{inscription.Text}”" : "Not inscribed",
            Foreground = DerethPalette.CreamBrush, FontSize = 13, FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap
        });
        if (inscription.Scribe.Length > 0) panel.Children.Add(Text($"Inscribed by {inscription.Scribe}", DerethPalette.MutedBrush, 11));
        return panel;
    }

    /// <summary>
    /// The field the character writes in. Enter saves, and so does leaving it with changed text. Only the appraisal's text counts as saved:
    /// a send is remembered only so that leaving the field after Enter does not send the same text again, and the refreshed appraisal
    /// (the response to the silent ID) brings the field its new saved text. Escape puts back the saved text. A save that the game does not
    /// send keeps the typed text, so the next Enter tries again. Empty text erases the inscription. The placeholder shows while it is empty.
    /// </summary>
    private static Control Editable(string saved, Func<string, bool> save)
    {
        string? sent = null;
        var input = new TextBox
        {
            Text = saved, Watermark = "Inscribe here", MaxLength = AppraisalInscription.MaxLength, FontFamily = DerethPalette.Body,
            FontSize = 12, Foreground = DerethPalette.TextBrush, CaretBrush = DerethPalette.GoldBrush, Background = DerethPalette.GrooveBrush,
            BorderBrush = DerethPalette.GrooveEdgeBrush,
        };
        void Save()
        {
            var text = input.Text ?? string.Empty;
            if (text == (sent ?? saved)) return;
            if (save(text)) sent = text;
        }
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Save(); e.Handled = true; }
            else if (e.Key == Key.Escape) { input.Text = saved; sent = null; e.Handled = true; }
        };
        input.LostFocus += (_, _) => Save();
        var panel = new StackPanel { Margin = new Thickness(10, 6, 10, 6), Spacing = 2, Children = { input } };
        panel.Children.Add(Text("Enter saves · Esc cancels", DerethPalette.MutedBrush, 11));
        return panel;
    }

    /// <summary>Raised values are the theme's green, lowered ones its red (the invalid colour), and the rest the text colour.</summary>
    private static IBrush ToneBrush(AppraisalTone tone) => tone switch
    {
        AppraisalTone.Raised => DerethPalette.RaisedBrush,
        AppraisalTone.Lowered => DerethPalette.InvalidBrush,
        _ => DerethPalette.TextBrush,
    };

    private static TextBlock Text(string text, IBrush brush, double size, bool centred = false, FontFamily? font = null) => new()
    {
        Text = text, Foreground = brush, FontSize = size, FontFamily = font ?? FontFamily.Default,
        TextAlignment = centred ? TextAlignment.Center : TextAlignment.Left,
    };
}
