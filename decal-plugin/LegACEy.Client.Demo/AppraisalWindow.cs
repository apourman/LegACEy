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
/// when it does not fit, and the inscription stays pinned under it. The window opens fitted to its content (up to <see cref="MaxHeight"/>, a
/// retail creature taller, with room under its lines for its figure's feet)
/// and resizes from its edges. One instance serves the session: <see cref="Show"/> swaps in the next appraisal at the window's size.
/// The retail layout (<see cref="RetailAppraisalLayout"/>) draws the same content as retail's examination panel does, in the Dereth frame:
/// no icon, a creature's header box pinned over its lines with its figure standing behind them, and a parchment inscription box.
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
    // A window with a retail figure opens taller, with room under its lines for the figure's feet, as retail's panel has.
    private const int RetailFigureMaxHeight = 720;
    private const double FloorRoom = 7 * RetailLineHeight;
    /// <summary>The smallest the player can resize the window to: the header, the summary and a few rows.</summary>
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(260, 160));
    // The Dereth frame and header, the title rule and the body's bottom margin, around the body.
    private const int Chrome = DerethWindow.IconTitleBarHeight + 5 + 6 + 8;
    // The frame and the window's side margins, and the body's padding, around the text.
    private const int BodyInset = 40;
    // The retail layout's line height, a blank line's height, and the parchment inscription box's height, as in retail's panel.
    private const double RetailLineHeight = 19;
    private const int ParchmentHeight = 78;
    // The retail body's side margins.
    private const double BodySide = 6;
    private static readonly IBrush ParchmentInkBrush = DerethPalette.Brush(Color.Parse("#2A2014"));

    // The inventory's figure glyph, for the Inspect button.
    private const string FigureGlyph = "M7,1.5 A2.2,2.2 0 1 1 6.99,1.5 Z M2.5,13 V9.5 Q2.5,6.8 7,6.8 Q11.5,6.8 11.5,9.5 V13 Z";

    private readonly DerethWindow _window;
    private readonly ScrollViewer _scroll;
    private readonly ContentControl _foot;
    // The retail layout's creature header box, and the figure behind the body; empty otherwise.
    private readonly ContentControl _top;
    private readonly ContentControl _behind;
    private readonly Button _inspectButton;
    private AppraisalContent _content;
    private bool _retail;
    // Opens the Inspect window for the shown object; null hides the button.
    private Action<AppraisalContent>? _inspect;
    // The inscription field in the foot, and the item it writes for. Null when the foot holds no field.
    private InscriptionField? _field;
    private uint _footObject;

    private AppraisalWindow(DerethWindow window, ScrollViewer scroll, ContentControl foot, ContentControl top, ContentControl behind, int height,
        InscriptionField? field, uint footObject, Button inspectButton, AppraisalContent content, bool retail)
    {
        _window = window;
        _scroll = scroll;
        _foot = foot;
        _top = top;
        _behind = behind;
        _retail = retail;
        _inspectButton = inspectButton;
        _content = content;
        inspectButton.Click += (_, _) => _inspect?.Invoke(_content);
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
    /// whether it was sent. <paramref name="measure"/> measures a control as the window draws it, at the UI size: there each line rounds to whole
    /// pixels, so the unscaled measure used without it can come out short and scroll the last line. <paramref name="inspect"/> opens the
    /// Inspect window for an object; null leaves the Inspect button out. <paramref name="retail"/> draws the retail layout, where
    /// <paramref name="figure"/> gives a creature's figure to stand behind its lines (null for none).
    /// </summary>
    public static AppraisalWindow Create(AppraisalContent content, IGameArtSource? art, Func<uint, string, bool> inscribe,
        Func<Control, Size, Size>? measure = null, Action<AppraisalContent>? inspect = null, bool retail = false, Func<AppraisalContent, Control?>? figure = null)
    {
        var layout = Layout(content, inscribe, retail, art);
        var scroll = Scroll(layout.Body);
        var foot = new ContentControl { Content = layout.Foot };
        var shown = Figure(content, retail, figure);
        var top = new ContentControl { Content = layout.Top, Margin = HeaderMargin(layout.MiddleMargin) };
        var behind = new ContentControl { Content = shown, IsHitTestVisible = false, Margin = layout.MiddleMargin };
        scroll.Margin = layout.MiddleMargin;
        var glyph = new DerethPathGlyph(FigureGlyph) { Stroke = DerethPalette.MutedBrush };
        var button = new DerethHeaderButton(glyph) { Name = "InspectButton" };
        ToolTip.SetTip(button, "Inspect");
        var window = new DerethWindow(content.Name, retail ? null : Icon(content.Visual, art), Frame(top, behind, scroll, foot), button, iconHeader: true);
        // The whole window measured as drawn, chrome and all, with the body unscrolled; the 0.001 drops float noise from whole pixels. A
        // figure gets room under the lines for its feet, below the body so the body never scrolls for it.
        var needed = measure == null ? layout.Needed : measure(window, new Size(Width, double.PositiveInfinity)).Height;
        var height = shown != null
            ? Math.Min(RetailFigureMaxHeight, (int)Math.Ceiling(needed + FloorRoom - 0.001))
            : Math.Min(MaxHeight, (int)Math.Ceiling(needed - 0.001));
        if (shown != null) window.SeeThrough = behind;
        var view = new AppraisalWindow(window, scroll, foot, top, behind, height, layout.Field, content.ObjectId, button, content, retail);
        view.SetInspect(inspect);
        window.CloseRequested += (_, _) => view.Close?.Invoke();
        return view;
    }

    /// <summary>
    /// Shows another appraisal in this window, at the size the window has (the player's to set): the body scrolls when it does not fit. The
    /// inscription field with unsaved text is kept only for the same item, and then it takes the refreshed saved text (Escape restores that).
    /// Another item's appraisal replaces the field: the typed text is dropped, unsent, and so does a change of layout. The figure of the
    /// creature already shown stays, turned as it was.
    /// </summary>
    public void Show(AppraisalContent content, IGameArtSource? art, Func<uint, string, bool> inscribe, Action<AppraisalContent>? inspect = null,
        bool retail = false, Func<AppraisalContent, Control?>? figure = null)
    {
        var layout = Layout(content, inscribe, retail, art);
        var sameLayout = retail == _retail;
        if (!(sameLayout && content.ObjectId == _content.ObjectId && _behind.Content != null)) _behind.Content = Figure(content, retail, figure);
        _content = content;
        _retail = retail;
        SetInspect(inspect);
        _window.Title = content.Name;
        _window.SetIcon(retail ? null : Icon(content.Visual, art));
        _scroll.Content = layout.Body;
        _behind.Margin = _scroll.Margin = layout.MiddleMargin;
        _window.SeeThrough = _behind.Content != null ? _behind : null;
        _top.Content = layout.Top;
        _top.Margin = HeaderMargin(layout.MiddleMargin);
        if (_field is { Unsaved: true } kept && layout.Field != null && content.ObjectId == _footObject && sameLayout)
        {
            kept.Rebase(content.Inscription?.Text ?? string.Empty);
            return;
        }
        _field?.Detach();
        _foot.Content = layout.Foot;
        _field = layout.Field;
        _footObject = content.ObjectId;
    }

    // The header box reaches the frame as the body does; a margin on the box itself would be clipped to its slot.
    private static Thickness HeaderMargin(Thickness middle) => new(middle.Left, 0, middle.Right, 0);

    private void SetInspect(Action<AppraisalContent>? inspect)
    {
        _inspect = inspect;
        _inspectButton.IsVisible = inspect != null;
    }

    /// <summary>The window is replaced by a new one: its inscription field no longer saves what is typed in it.</summary>
    public void Retire() => _field?.Detach();

    /// <summary>
    /// The body, the header box and the inscription for the content, the height the window needs to show all of it, and the margin of the
    /// body and the figure behind it: a retail creature's reach the frame, as retail's do.
    /// </summary>
    private static (Control Body, Control? Top, Control? Foot, int Needed, InscriptionField? Field, Thickness MiddleMargin) Layout(AppraisalContent content,
        Func<uint, string, bool> inscribe, bool retail, IGameArtSource? art)
    {
        var body = content.Formula != null ? SpellBody(content, content.Formula, art) : retail ? RetailBody(content) : SectionsBody(content);
        var top = retail && content.Creature is { } creature ? HeaderBox(creature) : null;
        InscriptionField? field = null;
        var foot = content.Inscription is { } inscription ? Inscription(inscription, content.ObjectId, inscribe, retail, out field) : null;
        // A window measured before it is shown has no template yet, so the parts are measured and the chrome added to them.
        var needed = Chrome + new[] { body, top, foot }.Sum(part =>
        {
            if (part == null) return 0;
            part.Measure(new Size(Width - BodyInset, double.PositiveInfinity));
            return (int)Math.Ceiling(part.DesiredSize.Height);
        });
        var middle = top != null ? new Thickness(-DerethWindow.Inset, 0, -DerethWindow.Inset, -DerethWindow.Inset) : default;
        return (body, top, foot, needed, field, middle);
    }

    /// <summary>
    /// A spell, in either layout as retail's examination lays one out (page 0x10000153 of LayoutDesc 0x2100006B): its type line and its
    /// figures under the name, a rule, the description, a rule, then FORMULA and the components' icons in a centred row.
    /// </summary>
    private static Control SpellBody(AppraisalContent content, IReadOnlyList<uint> formula, IGameArtSource? art)
    {
        var body = new StackPanel { Margin = new Thickness(BodySide, 6, BodySide, 8), Spacing = 2 };
        if (content.TypeLine.Length > 0) body.Children.Add(RetailText(content.TypeLine, DerethPalette.CreamBrush));
        if (content.Subtitle.Length > 0) body.Children.Add(RetailText(content.Subtitle, DerethPalette.TextBrush));
        body.Children.Add(new DerethRule { Margin = new Thickness(0, 6, 0, 6) });
        if (content.Description.Length > 0) body.Children.Add(RetailText(content.Description, DerethPalette.TextBrush, wrap: true));
        body.Children.Add(new DerethRule { Margin = new Thickness(0, 6, 0, 4) });
        body.Children.Add(new TextBlock
        {
            Text = "FORMULA", Foreground = DerethPalette.TextBrush, FontFamily = DerethPalette.Title, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center
        });
        var icons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var icon in formula)
            icons.Children.Add(new Image { Source = GameArtImageExtension.CreateBitmap(art, icon), Width = 32, Height = 32 });
        body.Children.Add(icons);
        return body;
    }

    /// <summary>The sections layout: the summary, then each section under its heading, then the description under a rule.</summary>
    private static Control SectionsBody(AppraisalContent content)
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
        return body;
    }

    /// <summary>
    /// The retail layout's lines in Palatino, a blank line between groups. An item's lines wrap across the window in one colour. A
    /// creature's labels sit on the left with the values at the right edge, each value coloured by its tone; a label too long for a narrow
    /// window is cut short before a value is. A ruled group has retail's line under it, frame to frame. The figure stands behind the lines.
    /// </summary>
    private static Control RetailBody(AppraisalContent content)
    {
        var creature = content.Creature != null;
        var body = new StackPanel { Margin = new Thickness(BodySide, 4, BodySide, 6) };
        if (creature) body.Effect = new DropShadowEffect { BlurRadius = 2, OffsetX = 0, OffsetY = 1, Color = Colors.Black, Opacity = 1 };
        var first = true;
        foreach (var group in RetailAppraisalLayout.Groups(content))
        {
            if (!first) body.Children.Add(new Border { Height = RetailLineHeight * group.BlankLinesBefore });
            first = false;
            foreach (var line in group.Lines)
            {
                if (!creature)
                {
                    body.Children.Add(RetailText(line.Value, DerethPalette.TextBrush, wrap: true));
                    continue;
                }
                var label = RetailText(line.Label ?? string.Empty, DerethPalette.TextBrush);
                label.HorizontalAlignment = HorizontalAlignment.Left;
                var value = RetailText(line.Value, line.Tone == AppraisalTone.Normal ? DerethPalette.TextBrush : ToneBrush(line.Tone));
                Grid.SetColumn(value, 1);
                body.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { label, value } });
            }
            if (group.Ruled)
                body.Children.Add(new Border { Height = 1, Margin = new Thickness(-BodySide, 4, -BodySide, 0), Background = DerethPalette.GoldDarkBrush });
        }
        return body;
    }

    private static TextBlock RetailText(string text, IBrush brush, bool wrap = false) => new()
    {
        Text = text, Foreground = brush, FontFamily = DerethPalette.Title, FontSize = 13, LineHeight = RetailLineHeight,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    /// <summary>
    /// Retail's header box: the creature's lines centred, and its level under the caption in a cell on the right. Its rules run frame to
    /// frame, as retail's do.
    /// </summary>
    private static Control HeaderBox(AppraisalCreatureHeader header)
    {
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 3, 4, 4) };
        foreach (var text in header.Lines)
        {
            var line = RetailText(text, DerethPalette.TextBrush);
            line.HorizontalAlignment = HorizontalAlignment.Center;
            lines.Children.Add(line);
        }
        var caption = new TextBlock
        {
            Text = header.Character ? "Character Level" : "Level", Foreground = DerethPalette.TextBrush, FontFamily = DerethPalette.Title, FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var level = new TextBlock
        {
            Text = header.Level, Foreground = DerethPalette.TextBrush, FontFamily = DerethPalette.Title, FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var cell = new Border
        {
            BorderBrush = DerethPalette.GoldDarkBrush, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(2),
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { caption, level } },
        };
        Grid.SetColumn(cell, 1);
        return new Border
        {
            Margin = new Thickness(0, 3, 0, 0), BorderBrush = DerethPalette.GoldDarkBrush, BorderThickness = new Thickness(0, 1),
            Background = DerethPalette.Brush(DerethPalette.Groove.WithAlpha(0xCC)),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,70"), Children = { lines, cell } },
        };
    }

    /// <summary>The retail layout's figure for a creature, standing still behind its lines; nothing for an item or the sections layout.</summary>
    private static Control? Figure(AppraisalContent content, bool retail, Func<AppraisalContent, Control?>? figure) =>
        retail && content.Creature != null ? figure?.Invoke(content) : null;

    /// <summary>
    /// The frame's content: the header box, the scrolling body over the figure, and the inscription pinned under it. As retail's, the
    /// figure stands behind the lines: the host draws it under the window, which leaves its area see-through.
    /// </summary>
    private static Grid Frame(Control top, Control behind, Control scroll, Control foot)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(top);
        Grid.SetRow(behind, 1);
        grid.Children.Add(behind);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Grid.SetRow(foot, 2);
        grid.Children.Add(foot);
        return grid;
    }

    private static ScrollViewer Scroll(Control body) => new()
    {
        Content = body,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    internal static Control? Icon(ItemVisual? visual, IGameArtSource? art)
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
        value.TextWrapping = TextWrapping.Wrap;
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
    /// nobody inscribed says so; in the retail layout its parchment box stays blank.
    /// </summary>
    private static Control Inscription(AppraisalInscription inscription, uint itemId, Func<uint, string, bool> inscribe, bool retail,
        out InscriptionField? field)
    {
        if (inscription.Mode == InscriptionMode.Editable)
        {
            var signer = inscription.Scribe.Length > 0 ? inscription.Scribe : inscription.Writer;
            field = new InscriptionField(inscription.Text, text => inscribe(itemId, text), retail, signer);
            return field.Panel;
        }
        field = null;
        // A long inscription scrolls inside the box, as the field does.
        if (retail)
        {
            var text = new ScrollViewer
            {
                Content = ParchmentText(inscription.Text), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
            return Parchment(text, Signature(inscription.Scribe));
        }
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
    /// placeholder shows while it is empty. Once the window has replaced the field (<see cref="Detach"/>), it no longer saves. The parchment
    /// field works as retail's: Enter starts a new line, so only leaving the field saves, and the signer's name shows in its corner from the
    /// moment the character enters it.
    /// </summary>
    private sealed class InscriptionField
    {
        private readonly TextBox _input;
        private readonly Func<string, bool> _save;
        private string _saved;
        private string? _sent;
        private bool _detached;

        /// <param name="parchment">Draws the field as retail's parchment box, with "&lt;Inscribe here&gt;" centred while it is empty and
        /// unfocused, and <paramref name="signer"/> signed at the bottom right while it is focused or written on.</param>
        public InscriptionField(string saved, Func<string, bool> save, bool parchment = false, string signer = "")
        {
            _saved = saved;
            _save = save;
            _input = parchment
                ? new TextBox
                {
                    Text = saved, MaxLength = AppraisalInscription.MaxLength, FontFamily = DerethPalette.Title, FontSize = 13,
                    Foreground = ParchmentInkBrush, CaretBrush = ParchmentInkBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Padding = new Thickness(0), MinHeight = 0, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top,
                    AcceptsReturn = true, NewLine = "\n",
                }
                : new TextBox
                {
                    Text = saved, Watermark = "Inscribe here", MaxLength = AppraisalInscription.MaxLength, FontFamily = DerethPalette.Body,
                    FontSize = 12, Foreground = DerethPalette.TextBrush, CaretBrush = DerethPalette.GoldBrush, Background = DerethPalette.GrooveBrush,
                    BorderBrush = DerethPalette.GrooveEdgeBrush,
                };
            _input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && !parchment) { Save(); e.Handled = true; }
                else if (e.Key == Key.Escape) { _input.Text = _saved; _sent = null; e.Handled = true; }
            };
            _input.LostFocus += (_, _) => Save();
            if (parchment)
            {
                // The text box's own watermark follows its text to the top left; retail centres the placeholder in the box.
                var placeholder = ParchmentText("<Inscribe here>");
                placeholder.HorizontalAlignment = HorizontalAlignment.Center;
                placeholder.VerticalAlignment = VerticalAlignment.Center;
                placeholder.IsHitTestVisible = false;
                var signature = Signature(signer);
                void Show()
                {
                    var empty = string.IsNullOrEmpty(_input.Text);
                    placeholder.IsVisible = empty && !_input.IsFocused;
                    if (signature != null) signature.IsVisible = !empty || _input.IsFocused;
                }
                _input.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty || e.Property == InputElement.IsFocusedProperty) Show();
                };
                Show();
                Panel = Parchment(new Grid { Children = { _input, placeholder } }, signature);
                return;
            }
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

    /// <summary>
    /// Retail's parchment inscription box at the window's foot: the text from its top left in dark ink, and the signature at its bottom
    /// right.
    /// </summary>
    private static Control Parchment(Control text, TextBlock? signature)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { text } };
        if (signature != null)
        {
            signature.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(signature, 1);
            grid.Children.Add(signature);
        }
        return new Border
        {
            Height = ParchmentHeight, Margin = new Thickness(2, 4, 2, 0), BorderBrush = DerethPalette.GoldDarkBrush, BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 3), Child = grid,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#C9B48A"), 0), new GradientStop(Color.Parse("#A88F62"), 1) }
            },
        };
    }

    /// <summary>A name signed as retail signs an inscription, "--Name"; none without a name.</summary>
    private static TextBlock? Signature(string name) => name.Length > 0 ? ParchmentText("--" + name) : null;

    private static TextBlock ParchmentText(string text) => new()
    {
        Text = text, Foreground = ParchmentInkBrush, FontFamily = DerethPalette.Title, FontSize = 13, TextWrapping = TextWrapping.Wrap,
    };

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
