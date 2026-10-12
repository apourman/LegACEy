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
/// when it does not fit, and the inscription stays pinned under it. The window opens fitted to its content (up to <see cref="MaxHeight"/>)
/// and resizes from its edges. One instance serves the session: <see cref="Show"/> swaps in the next appraisal at the window's size.
/// </summary>
public sealed class AppraisalWindow
{
    public const int Width = 330;
    // The label column: as wide as the window's widest label, so every value starts in one place, from 92 up to 150 (a longer label wraps).
    private const double LabelMinWidth = 92, LabelMaxWidth = 150;
    private static readonly Color ManaFill = Color.Parse("#4F86C6");
    private static readonly Color HealthFill = Color.Parse("#B8453A");
    /// <summary>The tallest the window opens at before its body scrolls; the player can resize it from there.</summary>
    public const int MaxHeight = 520;
    /// <summary>The smallest the player can resize the window to: the header, the summary and a few rows.</summary>
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(260, 160));
    // The Dereth frame and header, the title rule and the body's bottom margin, around the body.
    private const int Chrome = DerethWindow.TitleBarHeight + 5 + 6 + 8;
    // The frame and the window's side margins, and the body's padding, around the text.
    private const int BodyInset = 40;

    private readonly DerethWindow _window;
    private readonly ScrollViewer _scroll;
    private readonly ContentControl _foot;
    // The inscription field in the foot, and the item it writes for. Null when the foot holds no field.
    private InscriptionField? _field;
    private uint _footObject;

    private AppraisalWindow(DerethWindow window, ScrollViewer scroll, ContentControl foot, int height, InscriptionField? field, uint footObject)
    {
        _window = window;
        _scroll = scroll;
        _foot = foot;
        _field = field;
        _footObject = footObject;
        Height = height;
    }

    public DerethWindow Window => _window;

    /// <summary>The height the window opens at when no size is saved: its body and inscription plus the chrome, up to <see cref="MaxHeight"/>.</summary>
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
        var view = new AppraisalWindow(window, scroll, foot, layout.Height, layout.Field, content.ObjectId);
        window.CloseRequested += (_, _) => view.Close?.Invoke();
        return view;
    }

    /// <summary>
    /// Shows another appraisal in this window, at the size the window has (the player's to set): the body scrolls when it does not fit. The
    /// inscription field with unsaved text is kept only for the same item, and then it takes the refreshed saved text (Escape restores that).
    /// Another item's appraisal replaces the field: the typed text is dropped, unsent.
    /// </summary>
    public void Show(AppraisalContent content, IGameArtSource? art, Func<uint, string, bool> inscribe)
    {
        var layout = Layout(content, inscribe);
        _window.Title = content.Name;
        _window.SetIcon(Icon(content.Visual, art));
        _scroll.Content = layout.Body;
        if (_field is { Unsaved: true } kept && layout.Field != null && content.ObjectId == _footObject)
        {
            kept.Rebase(content.Inscription?.Text ?? string.Empty);
            return;
        }
        _field?.Detach();
        _foot.Content = layout.Foot;
        _field = layout.Field;
        _footObject = content.ObjectId;
    }

    /// <summary>The window is replaced by a new one: its inscription field no longer saves what is typed in it.</summary>
    public void Retire() => _field?.Detach();

    /// <summary>The body and the inscription for the content, and the height the window needs to show all of it.</summary>
    private static (Control Body, Control? Foot, int Height, InscriptionField? Field) Layout(AppraisalContent content, Func<uint, string, bool> inscribe)
    {
        var body = new StackPanel { Margin = new Thickness(10, 6, 10, 6), Spacing = 2 };
        Grid.SetIsSharedSizeScope(body, true);
        if (content.TypeLine.Length > 0) body.Children.Add(Text(content.TypeLine, DerethPalette.CreamBrush, 14));
        if (content.Subtitle.Length > 0) body.Children.Add(Text(content.Subtitle, DerethPalette.MutedBrush, 12));
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
        InscriptionField? field = null;
        var foot = content.Inscription is { } inscription ? Inscription(inscription, content.ObjectId, inscribe, out field) : null;
        // A window measured before it is shown has no template yet, so the parts are measured and the chrome added to them.
        body.Measure(new Size(Width - BodyInset, double.PositiveInfinity));
        foot?.Measure(new Size(Width - BodyInset, double.PositiveInfinity));
        var needed = Chrome + (int)Math.Ceiling(body.DesiredSize.Height) + (foot == null ? 0 : (int)Math.Ceiling(foot.DesiredSize.Height));
        return (body, foot, Math.Min(MaxHeight, needed), field);
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

    /// <summary>Value, Burden and Workmanship stacked, each label in the label column with its value beside it, as the rows below.</summary>
    private static Control Figures(IReadOnlyList<AppraisalFigure> figures)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 4), Spacing = 2 };
        foreach (var figure in figures) stack.Children.Add(Row(new AppraisalRow(figure.Label, figure.Value)));
        return stack;
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

    /// <summary>
    /// A label in a fixed column with its value beside it, or a line of text across the section when there is no label. The tone colours the
    /// value. Spells and meters have their own layout.
    /// </summary>
    private static Control Row(AppraisalRow row)
    {
        switch (row)
        {
            case AppraisalSpellRow spell: return Spell(spell);
            case AppraisalMeterRow meter: return Meter(meter);
        }
        var brush = ToneBrush(row.Tone);
        if (row.Label == null) return new TextBlock { Text = row.Text, Foreground = brush, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var grid = LabelGrid();
        grid.Children.Add(Label(row.Label));
        var value = Text(row.Text, brush, 13);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    /// <summary>The name in cream, the description under it in muted text.</summary>
    private static Control Spell(AppraisalSpellRow spell)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(spell.Name, spell.Tone == AppraisalTone.Normal ? DerethPalette.CreamBrush : ToneBrush(spell.Tone), 12));
        if (spell.Description.Length > 0)
            stack.Children.Add(new TextBlock { Text = spell.Description, Foreground = DerethPalette.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        return stack;
    }

    /// <summary>The label in the label column, the bar beside it, and the note under the bar.</summary>
    private static Control Meter(AppraisalMeterRow meter)
    {
        var grid = LabelGrid();
        grid.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(Label(meter.Name));
        var bar = new StackPanel { Spacing = 3 };
        bar.Children.Add(Bar(meter.Current, meter.Maximum, meter.Text, meter.Tone == AppraisalTone.Normal ? DerethPalette.CreamBrush : ToneBrush(meter.Tone),
            meter.Fill));
        if (meter.Note != null) bar.Children.Add(Text(meter.Note, DerethPalette.MutedBrush, 11));
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        return grid;
    }

    /// <summary>
    /// The groove, filled from the left to the share of the maximum in a gradient, with the value centred on it. The gradient runs from the
    /// fill's colour to half of it.
    /// </summary>
    private static Control Bar(int current, int maximum, string text, IBrush textBrush, AppraisalMeterFill kind)
    {
        var share = maximum > 0 ? Math.Min(1, Math.Max(0, current / (double)maximum)) : 0;
        var top = FillColor(kind);
        var fill = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(top, 0), new GradientStop(Color.FromRgb((byte)(top.R / 2), (byte)(top.G / 2), (byte)(top.B / 2)), 1) }
            }
        };
        var filled = new Grid { ColumnDefinitions = new ColumnDefinitions { new(share, GridUnitType.Star), new(1 - share, GridUnitType.Star) } };
        filled.Children.Add(fill);
        var track = new Grid { Height = 14 };
        track.Children.Add(new Border
        {
            Background = DerethPalette.GrooveBrush, BorderBrush = DerethPalette.GrooveEdgeBrush, BorderThickness = new Thickness(1),
            Padding = new Thickness(1), Child = filled
        });
        track.Children.Add(new TextBlock
        {
            Text = text, Foreground = textBrush, FontSize = 10.5, FontFamily = FontFamily.Default,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        return track;
    }
    /// <summary>Mana blue, health red and stamina gold, the three meters' fills.</summary>
    private static Color FillColor(AppraisalMeterFill kind) => kind switch
    {
        AppraisalMeterFill.Health => HealthFill,
        AppraisalMeterFill.Stamina => DerethPalette.Gold,
        _ => ManaFill,
    };

    /// <summary>
    /// The inscription at the foot: an editable field the character writes in, or the text and its scribe read only. A read-only item that
    /// nobody inscribed says so.
    /// </summary>
    private static Control Inscription(AppraisalInscription inscription, uint itemId, Func<uint, string, bool> inscribe, out InscriptionField? field)
    {
        if (inscription.Mode == InscriptionMode.Editable)
        {
            field = new InscriptionField(inscription.Text, text => inscribe(itemId, text));
            return field.Panel;
        }
        field = null;
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
    /// (the response to the silent ID) brings the field its new saved text, through <see cref="Rebase"/>. Escape puts back the saved text. A
    /// save that the game does not send keeps the typed text, so the next Enter tries again. Empty text erases the inscription. The
    /// placeholder shows while it is empty. Once the window has replaced the field (<see cref="Detach"/>), it no longer saves.
    /// </summary>
    private sealed class InscriptionField
    {
        private readonly TextBox _input;
        private readonly Func<string, bool> _save;
        private string _saved;
        private string? _sent;
        private bool _detached;

        public InscriptionField(string saved, Func<string, bool> save)
        {
            _saved = saved;
            _save = save;
            _input = new TextBox
            {
                Text = saved, Watermark = "Inscribe here", MaxLength = AppraisalInscription.MaxLength, FontFamily = DerethPalette.Body,
                FontSize = 12, Foreground = DerethPalette.TextBrush, CaretBrush = DerethPalette.GoldBrush, Background = DerethPalette.GrooveBrush,
                BorderBrush = DerethPalette.GrooveEdgeBrush,
            };
            _input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Save(); e.Handled = true; }
                else if (e.Key == Key.Escape) { _input.Text = _saved; _sent = null; e.Handled = true; }
            };
            _input.LostFocus += (_, _) => Save();
            var panel = new StackPanel { Margin = new Thickness(10, 6, 10, 6), Spacing = 2, Children = { _input } };
            panel.Children.Add(Text("Enter saves · Esc cancels", DerethPalette.MutedBrush, 11));
            Panel = panel;
        }

        public Control Panel { get; }

        /// <summary>Whether the field holds text that is not saved or sent yet.</summary>
        public bool Unsaved => (_input.Text ?? string.Empty) != (_sent ?? _saved);

        /// <summary>A refreshed appraisal of the same item: its saved text is the new one. The typed text stays in the field.</summary>
        public void Rebase(string saved)
        {
            _saved = saved;
            _sent = null;
        }

        /// <summary>The window replaced the field: nothing typed in it is saved from now on, so the field sends nothing.</summary>
        public void Detach() => _detached = true;

        private void Save()
        {
            if (_detached) return;
            var text = _input.Text ?? string.Empty;
            if (text == (_sent ?? _saved)) return;
            if (_save(text)) _sent = text;
        }
    }

    /// <summary>Raised values are the theme's green, lowered ones its red (the invalid colour), and the rest the text colour.</summary>
    private static IBrush ToneBrush(AppraisalTone tone) => tone switch
    {
        AppraisalTone.Raised => DerethPalette.RaisedBrush,
        AppraisalTone.Lowered => DerethPalette.InvalidBrush,
        _ => DerethPalette.TextBrush,
    };

    /// <summary>A row with the label column the whole window shares, and the value in the rest.</summary>
    private static Grid LabelGrid() => new()
    {
        ColumnDefinitions = { new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Label", MinWidth = LabelMinWidth }, new ColumnDefinition(1, GridUnitType.Star) },
        ColumnSpacing = 8
    };

    private static TextBlock Label(string text)
    {
        var label = Text(text, DerethPalette.MutedBrush, 13);
        label.MaxWidth = LabelMaxWidth;
        label.TextWrapping = TextWrapping.Wrap;
        return label;
    }

    private static TextBlock Text(string text, IBrush brush, double size, FontFamily? font = null) => new()
    {
        Text = text, Foreground = brush, FontSize = size, FontFamily = font ?? FontFamily.Default,
    };
}
