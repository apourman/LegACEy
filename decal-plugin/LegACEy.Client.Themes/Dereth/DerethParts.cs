using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace LegACEy.Client.Themes;

/// <summary>The Dereth palette and fonts. Titles are Palatino, text is Segoe UI, as the game's installed fonts are.</summary>
public static class DerethPalette
{
    public static readonly Color Gold = Color.Parse("#DBB86A");
    /// <summary>The light gold of the radar's lettering, its tape's points and a plan's walls.</summary>
    public static readonly Color GoldLight = Color.Parse("#DDC47D");
    public static readonly Color Cream = Color.Parse("#F6EFD9");
    /// <summary>The dark gold: a bezel's rim and a plaque's border.</summary>
    public static readonly Color GoldDark = Color.Parse("#8A6326");
    public static readonly Color Text = Color.Parse("#F2EFE6");
    public static readonly Color Muted = Color.Parse("#A39A86");
    public static readonly Color Invalid = Color.Parse("#D9584A");
    /// <summary>The green of a value a buff or spell raised, as retail colours it.</summary>
    public static readonly Color Raised = Color.Parse("#8ED17A");
    public static readonly Color Groove = Color.Parse("#080705");
    public static readonly Color GrooveEdge = Color.Parse("#4A3F2A");
    public static readonly Color Navy = Color.Parse("#18160F");
    /// <summary>The radar's plaques: the coordinates under the scope, the tape above it and the level ladder beside it.</summary>
    public static readonly Color Plaque = Color.FromArgb(0xD9, 0x18, 0x16, 0x0F);
    /// <summary>The selection colour: a selected slot's wash, border and glow.</summary>
    public static readonly Color Teal = Color.Parse("#3FCF5F");
    /// <summary>Green text, such as the "N selected" line.</summary>
    public static readonly Color TealText = Color.Parse("#4CF08C");
    /// <summary>Retail's blue, lit on a settings card's Retail choice. Only that choice is blue.</summary>
    public static readonly Color RetailBlue = Color.Parse("#6F86C8");
    /// <summary>The lit Retail label on a settings card.</summary>
    public static readonly Color RetailLabel = Color.Parse("#D8E1FF");
    /// <summary>A settings card's frame.</summary>
    public static readonly Color Line = Color.Parse("#3A3122");
    public static readonly FontFamily Title = new("Palatino Linotype, Palatino, Georgia, serif");
    public static readonly FontFamily Body = new("Segoe UI, Tahoma, avares://LegACEy.Client.Themes/Assets#Liberation Sans");

    public static readonly IBrush GoldBrush = Brush(Gold);
    public static readonly IBrush GoldDarkBrush = Brush(GoldDark);
    public static readonly IBrush CreamBrush = Brush(Cream);
    public static readonly IBrush TextBrush = Brush(Text);
    public static readonly IBrush MutedBrush = Brush(Muted);
    public static readonly IBrush InvalidBrush = Brush(Invalid);
    public static readonly IBrush RaisedBrush = Brush(Raised);
    public static readonly IBrush GrooveBrush = Brush(Groove);
    public static readonly IBrush GrooveEdgeBrush = Brush(GrooveEdge);
    public static readonly IBrush TealBrush = Brush(Teal);
    public static readonly IBrush PlaqueBrush = Brush(Plaque);
    /// <summary>Gold at a quarter opacity: the wash of an accepted drop indicator.</summary>
    public static readonly IBrush GoldWashBrush = Brush(Gold.WithAlpha(0x40));
    /// <summary>Red at a quarter opacity: the wash of a refused drop indicator.</summary>
    public static readonly IBrush InvalidWashBrush = Brush(Invalid.WithAlpha(0x40));

    public static IBrush Brush(Color color) => new SolidColorBrush(color);

    private static readonly Dictionary<(Typeface, double), double> InkShifts = new();

    /// <summary>
    /// How far to move a line of <paramref name="face"/> at <paramref name="size"/> down (up when negative) so its capitals and digits sit in
    /// the middle of its line box. A line box centres the font's ascent and descent, and Palatino's ascent leaves room for accents above the
    /// capitals, so they would sit low in a plaque. Measured from the glyphs of the font the system gives, so a fallback font is centred too.
    /// </summary>
    public static double InkShift(Typeface face, double size)
    {
        if (InkShifts.TryGetValue((face, size), out var shift)) return shift;
        var text = new FormattedText("H0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black);
        var ink = text.BuildGeometry(default)?.Bounds ?? default;
        shift = ink.Height > 0 ? Math.Round(text.Height / 2 - (ink.Top + ink.Bottom) / 2, 2) : 0;
        return InkShifts[(face, size)] = shift;
    }

    /// <summary>
    /// Draws <paramref name="text"/> with its capitals and digits centred in its line box (see <see cref="InkShift"/>), by the font and size it
    /// has once it is shown; its layout is not changed.
    /// </summary>
    public static T CentreInk<T>(T text) where T : TextBlock
    {
        void Shift() => text.RenderTransform = new TranslateTransform(0, InkShift(new Typeface(text.FontFamily, text.FontStyle, text.FontWeight), text.FontSize));
        text.AttachedToVisualTree += (_, _) => Shift();
        text.PropertyChanged += (_, e) =>
        {
            if (text.IsAttachedToVisualTree() && (e.Property == TextBlock.FontSizeProperty || e.Property == TextBlock.FontFamilyProperty)) Shift();
        };
        return text;
    }

    /// <summary>The same colour at another alpha.</summary>
    public static Color WithAlpha(this Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}

/// <summary>Which sheet art a <see cref="DerethFrame"/> is drawn with.</summary>
public enum DerethFrameArt { Window, Button }

/// <summary>A corner of a window frame. None means the pointer is over no corner.</summary>
public enum DerethCorner { None, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>A frame of corner and edge pieces, mirrored to all four sides, with its child inside the edges.</summary>
public sealed class DerethFrame : Decorator
{
    private const float ButtonFillRadius = 2f;
    private static readonly IBrush GripFill = DerethPalette.Brush(Color.FromArgb(0x80, DerethPalette.Cream.R, DerethPalette.Cream.G, DerethPalette.Cream.B));
    private static readonly IPen GripPen = new Pen(DerethPalette.CreamBrush, 1.2);

    private readonly DerethFrameArt _kind;
    private NineSlice _art;
    private DerethCorner _hoveredCorner;

    public DerethFrame(DerethFrameArt art)
    {
        _kind = art;
        Show(DerethState.Normal);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>
    /// The corner the pointer is over. The host sets it on a resizable window's frame, which then brightens that corner
    /// and shows grip ticks. Only a window frame draws it; slot and button frames ignore it, so setting it on them is a no-op.
    /// </summary>
    public DerethCorner HoveredCorner
    {
        get => _hoveredCorner;
        set
        {
            if (_kind != DerethFrameArt.Window || _hoveredCorner == value) return;
            _hoveredCorner = value;
            InvalidateVisual();
        }
    }

    /// <summary>Draws the frame in a state's art; a state without its own region draws the normal art.</summary>
    internal void Show(DerethState state)
    {
        _art = DerethSheet.Frame(_kind, state);
        Padding = new Thickness(_art.Edge);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        var corner = _art.Corner;
        var edge = _art.Edge;
        if (width < 2 * corner || height < 2 * corner) return;
        // The button's art has a rounded, see-through shadow ring outside its gold, so its fill stays inside the ring, or red shows
        // through at the corners.
        if (_kind == DerethFrameArt.Button)
            context.FillRectangle(_art.Fill, new Rect(Bounds.Size).Deflate(1), ButtonFillRadius);
        else
            context.FillRectangle(_art.Fill, new Rect(Bounds.Size));
        DerethSheet.Draw(context, _art.TopSource, new Rect(corner, 0, width - 2 * corner, edge));
        DerethSheet.Draw(context, _art.TopSource, new Rect(corner, height - edge, width - 2 * corner, edge), flipY: true);
        DerethSheet.Draw(context, _art.LeftSource, new Rect(0, corner, edge, height - 2 * corner));
        DerethSheet.Draw(context, _art.LeftSource, new Rect(width - edge, corner, edge, height - 2 * corner), flipX: true);
        DerethSheet.Draw(context, _art.CornerSource, new Rect(0, 0, corner, corner));
        DerethSheet.Draw(context, _art.CornerSource, new Rect(width - corner, 0, corner, corner), flipX: true);
        DerethSheet.Draw(context, _art.CornerSource, new Rect(0, height - corner, corner, corner), flipY: true);
        DerethSheet.Draw(context, _art.CornerSource, new Rect(width - corner, height - corner, corner, corner), flipX: true, flipY: true);
        if (_kind == DerethFrameArt.Window && _hoveredCorner != DerethCorner.None) DrawGrip(context, width, height, corner);
    }

    /// <summary>
    /// The hovered corner brightens over its whole corner piece, and three grip ticks sit in the frame's inner corner, past
    /// the 8 px lip, as diagonals across the stone.
    /// </summary>
    private void DrawGrip(DrawingContext context, double width, double height, double corner)
    {
        var right = _hoveredCorner is DerethCorner.TopRight or DerethCorner.BottomRight;
        var bottom = _hoveredCorner is DerethCorner.BottomLeft or DerethCorner.BottomRight;
        context.FillRectangle(GripFill, new Rect(right ? width - corner : 0, bottom ? height - corner : 0, corner, corner));
        var outerX = right ? width : 0;
        var outerY = bottom ? height : 0;
        var dx = right ? -1 : 1;
        var dy = bottom ? -1 : 1;
        const double lip = 8;
        for (var reach = 11; reach <= 15; reach += 2)
            context.DrawLine(GripPen, new Point(outerX + dx * reach, outerY + dy * lip), new Point(outerX + dx * lip, outerY + dy * reach));
    }
}

/// <summary>A horizontal strip: a left cap, a stretched middle and a right cap, drawn from three sheet regions.</summary>
public class DerethThreeSlice : Decorator
{
    private Rect _left;
    private Rect _middle;
    private Rect _right;

    internal DerethThreeSlice(Rect left, Rect middle, Rect right)
    {
        Show(left, middle, right);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>Draws the strip from other regions, such as a state's art.</summary>
    internal void Show(Rect left, Rect middle, Rect right)
    {
        (_left, _middle, _right) = (left, middle, right);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var height = Bounds.Height;
        // Sheet pixels to DIPs cancel in the aspect ratio, so the cap keeps its source proportions at any scale.
        var leftWidth = _left.Width * height / _left.Height;
        var rightWidth = _right.Width * height / _right.Height;
        if (Bounds.Width < leftWidth + rightWidth) return;
        DerethSheet.Draw(context, _left, new Rect(0, 0, leftWidth, height));
        DerethSheet.Draw(context, _middle, new Rect(leftWidth, 0, Bounds.Width - leftWidth - rightWidth, height));
        DerethSheet.Draw(context, _right, new Rect(Bounds.Width - rightWidth, 0, rightWidth, height));
    }
}

/// <summary>The title rule under a window's header.</summary>
public sealed class DerethRule : DerethThreeSlice
{
    public DerethRule() : base(DerethSheet.RuleLeft, DerethSheet.RuleMiddle, DerethSheet.RuleRight) => Height = 5;
}

/// <summary>
/// The search field: the sheet's field around a text box that shows its placeholder while empty. It brightens on hover and
/// turns green while focused. <see cref="Text"/> is what the
/// player has typed, and <see cref="TextChanged"/> fires on every edit. It knows nothing of what the text filters.
/// </summary>
public sealed class DerethSearchField : DerethThreeSlice
{
    private readonly TextBox _input;

    public DerethSearchField(string placeholder)
        : base(DerethSheet.Search(DerethFieldState.Normal).Left, DerethSheet.Search(DerethFieldState.Normal).Middle, DerethSheet.Search(DerethFieldState.Normal).Right)
    {
        Height = 26;
        // The box is bare: the sheet's field is its frame, so the box's own border, fill and minimum height are cleared.
        _input = new TextBox
        {
            Watermark = placeholder,
            Foreground = DerethPalette.TextBrush, CaretBrush = DerethPalette.GoldBrush, FontFamily = DerethPalette.Body, FontSize = 12,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(34, 0, 8, 0)
        };
        _input.TextChanged += (_, _) => TextChanged?.Invoke(this, EventArgs.Empty);
        _input.GotFocus += (_, _) => ShowState();
        _input.LostFocus += (_, _) => ShowState();
        PropertyChanged += (_, e) => { if (e.Property == IsPointerOverProperty) ShowState(); };
        Child = _input;
    }

    private void ShowState()
    {
        var art = DerethSheet.Search(_input.IsFocused ? DerethFieldState.Focus : IsPointerOver ? DerethFieldState.Hover : DerethFieldState.Normal);
        Show(art.Left, art.Middle, art.Right);
    }

    public string Text => _input.Text ?? string.Empty;

    /// <summary>The text changed, by typing.</summary>
    public event EventHandler? TextChanged;
}

/// <summary>The sheet's whole pictures: the paging arrows and the close X.</summary>
public enum DerethSpriteArt { PagerPrevious, PagerNext, Close }

/// <summary>A whole sheet region drawn at the control's size, in a state.</summary>
internal sealed class DerethSprite : Control
{
    private readonly DerethSpriteArt _art;
    private Rect _source;

    public DerethSprite(DerethSpriteArt art)
    {
        _art = art;
        _source = DerethSheet.Sprite(art, DerethState.Normal);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>Follows a button's state: normal, hover or pressed.</summary>
    public void Follow(Button button) =>
        button.Classes.CollectionChanged += (_, _) =>
        {
            _source = DerethSheet.Sprite(_art, DerethButton.StateOf(button));
            InvalidateVisual();
        };

    public override void Render(DrawingContext context) => DerethSheet.Draw(context, _source, new Rect(Bounds.Size));
}

/// <summary>
/// A paging arrow: the sheet's arrow as a button, in its hover and pressed art, dimmed while it is disabled.
/// </summary>
public sealed class DerethPagerButton : Button
{
    private const double DisabledOpacity = 0.4;

    static DerethPagerButton()
    {
        IsEnabledProperty.Changed.AddClassHandler<DerethPagerButton>((button, _) => button.Opacity = button.IsEnabled ? 1 : DisabledOpacity);
    }

    public DerethPagerButton(DerethSpriteArt art)
    {
        // A transparent face is still hit-tested, so the arrow takes the pointer.
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Template = new FuncControlTemplate<Button>((_, _) =>
        {
            var sprite = new DerethSprite(art);
            sprite.Follow(this);
            return sprite;
        });
    }
}

/// <summary>
/// A button in the Dereth button frame, drawn in its hover or pressed region when the sheet has one. Its content is text
/// or a glyph.
/// </summary>
public sealed class DerethButton : Button
{
    private const double DisabledOpacity = 0.4;
    private DerethFrame? _frame;

    static DerethButton()
    {
        // A disabled button dims, as the pager arrows do: the sheet has no disabled art.
        IsEnabledProperty.Changed.AddClassHandler<DerethButton>((button, _) => button.Opacity = button.IsEnabled ? 1 : DisabledOpacity);
    }

    public DerethButton()
    {
        // A transparent face is still hit-tested, so the frame takes the pointer.
        Background = Brushes.Transparent;
        // Plain text content takes the button's ink; without it, it falls back to the default theme's dark text.
        Foreground = DerethPalette.CreamBrush;
        // Centred unless the caller says otherwise; the template passes the alignment on, or text sits in the top-left corner.
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Template = new FuncControlTemplate<Button>((_, scope) =>
        {
            var presenter = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                [!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty),
                [!ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(HorizontalContentAlignmentProperty),
                [!ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(VerticalContentAlignmentProperty)
            };
            scope.Register(presenter.Name, presenter);
            _frame = new DerethFrame(DerethFrameArt.Button) { Child = presenter };
            _frame.Show(StateOf(this));
            return _frame;
        });
        Classes.CollectionChanged += (_, _) => _frame?.Show(StateOf(this));
    }

    internal static DerethState StateOf(Button button) =>
        button.Classes.Contains(":pressed") ? DerethState.Pressed : button.Classes.Contains(":pointerover") ? DerethState.Hover : DerethState.Normal;
}

/// <summary>
/// A vector glyph scaled to fit <see cref="Size"/> and centred on the control by its own drawn bounds, so a glyph whose shape is off
/// centre in its path data, or a control a pixel wider than tall, still draws it in the middle.
/// </summary>
public sealed class DerethPathGlyph : Control
{
    private readonly Geometry _data;
    private readonly double _size;

    public DerethPathGlyph(string data, double size = DerethWindow.HeaderGlyphSize)
    {
        _data = Geometry.Parse(data);
        _size = size;
    }

    public IBrush? Stroke { get; init; }
    public IBrush? Fill { get; init; }
    public double StrokeThickness { get; init; } = 1;

    public override void Render(DrawingContext context)
    {
        var shape = _data.Bounds;
        var scale = _size / Math.Max(shape.Width, shape.Height);
        var centre = new Rect(Bounds.Size).Center;
        using (context.PushTransform(Matrix.CreateTranslation(-shape.Center.X, -shape.Center.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(centre.X, centre.Y)))
            // The pen is scaled with the shape, so it is divided back to its thickness on screen.
            context.DrawGeometry(Fill, Stroke == null ? null : new Pen(Stroke, StrokeThickness / scale, lineCap: PenLineCap.Round), _data);
    }
}

/// <summary>
/// A small square header button holding a glyph, drawn as vectors: a thin gold rim on the button art's red fill, so it stays sharp at
/// any size, where the button sheet's 8 DIP corners would fill a small button and blur.
/// </summary>
public sealed class DerethHeaderButton : Button
{
    private static readonly IBrush[] Fills = { DerethPalette.Brush(Color.Parse("#4A0606")), DerethPalette.Brush(Color.Parse("#6E0A08")), DerethPalette.Brush(Color.Parse("#280000")) };
    private static readonly IBrush[] Rims = { DerethPalette.GoldDarkBrush, DerethPalette.GoldBrush, DerethPalette.GoldDarkBrush };
    private Border? _face;

    public DerethHeaderButton(Control glyph)
    {
        Width = Height = DerethWindow.HeaderButtonSize;
        VerticalAlignment = VerticalAlignment.Center;
        Content = glyph;
        Template = new FuncControlTemplate<Button>((_, scope) =>
        {
            var presenter = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                [!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty),
                // The glyph fills the face and centres itself, so layout rounding never moves it off the middle.
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
            };
            scope.Register(presenter.Name, presenter);
            _face = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = presenter };
            Show();
            return _face;
        });
        Classes.CollectionChanged += (_, _) => Show();
    }

    private void Show()
    {
        if (_face == null) return;
        var state = (int)DerethButton.StateOf(this);
        _face.Background = Fills[state];
        _face.BorderBrush = Rims[state];
    }
}

/// <summary>
/// A window with the Dereth frame, a header of icon, title, actions and close box (or the title centred across the header), the title
/// rule and content.
/// </summary>
public sealed class DerethWindow : UserControl
{
    /// <summary>The header of a window without an icon: the title and the header buttons.</summary>
    public const int HeaderHeight = 28;

    /// <summary>The header of a window with an icon: as tall as a game icon, which a layout never squeezes.</summary>
    public const int IconHeaderHeight = 32;

    /// <summary>
    /// The strip from a Dereth window's top that drags it: the 8 px frame edge and the header. A window passes it as its
    /// title-bar height.
    /// </summary>
    public const int TitleBarHeight = 8 + HeaderHeight;

    /// <summary>The title-bar height of a window with an icon in its header.</summary>
    public const int IconTitleBarHeight = 8 + IconHeaderHeight;

    /// <summary>The gap between header buttons, and between the close box and a centred title.</summary>
    public const int ActionGap = 4;

    /// <summary>The side of every header button, the close box included: small, so the header's controls take little of the window.</summary>
    public const int HeaderButtonSize = 18;

    /// <summary>The side of a header button's glyph, inside the button's 1 DIP rim.</summary>
    public const int HeaderGlyphSize = 10;

    /// <param name="headerActions">Controls for the header, left of the close box. Null when the header has none.</param>
    /// <param name="stretchActions">The header actions take the header's free width, and the title only its own.</param>
    /// <param name="centreTitle">
    /// The title is centred across the header, as retail centres its panel titles, with the close box's width kept clear on both sides.
    /// For a window without an icon or header actions.
    /// </param>
    /// <param name="iconHeader">
    /// The header is <see cref="IconHeaderHeight"/> tall, for a window that shows an icon, or may show one later; otherwise
    /// <see cref="HeaderHeight"/>.
    /// </param>
    public DerethWindow(string title, Control? icon, Control content, Control? headerActions = null, bool stretchActions = false,
        bool centreTitle = false, bool iconHeader = false)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        var columns = headerActions == null ? "Auto,*,Auto" : stretchActions ? "Auto,Auto,*,Auto" : "Auto,*,Auto,Auto";
        var height = iconHeader ? IconHeaderHeight : HeaderHeight;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = centreTitle ? ActionGap : 8, Height = height };
        // The icon is bare, at its own size: a layout never squeezes an icon, and a frame round it would make the header taller.
        // It is always there, and takes no space while it has no icon, so a window that changes its icon keeps the same header.
        _emblem = new Decorator { VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(_emblem);
        SetIcon(icon);
        _name = new DerethTitleText
        {
            FullText = title, Foreground = DerethPalette.CreamBrush, FontFamily = DerethPalette.Title, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = centreTitle ? TextAlignment.Center : TextAlignment.Left
        };
        if (centreTitle)
        {
            Grid.SetColumnSpan(_name, 3);
            _name.Margin = new Thickness(HeaderButtonSize + ActionGap, 0);
        }
        else Grid.SetColumn(_name, 1);
        header.Children.Add(_name);
        // The X is smaller than the other glyphs: at their size its two strokes read heavier than their outlines.
        var close = new DerethHeaderButton(new DerethPathGlyph("M0,0 L8,8 M8,0 L0,8", 8) { Stroke = DerethPalette.CreamBrush, StrokeThickness = 1.5 });
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        if (headerActions != null)
        {
            Grid.SetColumn(headerActions, 2);
            header.Children.Add(headerActions);
        }
        Grid.SetColumn(close, headerActions == null ? 2 : 3);
        header.Children.Add(close);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(6, 0, 6, 6) };
        body.Children.Add(header);
        // The rule runs nearly frame to frame, as in the concept.
        var rule = new DerethRule { Margin = new Thickness(-6, 0, -6, 0) };
        Grid.SetRow(rule, 1);
        body.Children.Add(rule);
        Grid.SetRow(content, 2);
        body.Children.Add(content);
        Content = new DerethFrame(DerethFrameArt.Window) { Child = body };
    }

    private readonly Decorator _emblem;
    private readonly DerethTitleText _name;

    /// <summary>The title in the header.</summary>
    public string Title
    {
        get => _name.FullText;
        set => _name.FullText = value;
    }

    /// <summary>A shorter title shown when <see cref="Title"/> does not fit even at its smallest size. Null trims the title instead.</summary>
    public string? ShortTitle
    {
        get => _name.ShortText;
        set => _name.ShortText = value;
    }

    /// <summary>Replaces the header's icon. Null shows no icon, and the title moves to the edge.</summary>
    public void SetIcon(Control? icon)
    {
        _emblem.Child = icon;
        _emblem.IsVisible = icon != null;
    }

    public event EventHandler? CloseRequested;
}

/// <summary>
/// A title line: 13 DIPs, stepping down to 11 until it fits its width, as a long name needs; past that it shows its short
/// text, sized the same way, if it has one, and ends in an ellipsis.
/// </summary>
public sealed class DerethTitleText : TextBlock
{
    private const double Largest = 13, Smallest = 11;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    private string _fullText = string.Empty;
    private string? _shortText;

    /// <summary>The whole title.</summary>
    public string FullText
    {
        get => _fullText;
        set { _fullText = value ?? string.Empty; Text = _fullText; InvalidateMeasure(); }
    }

    /// <summary>The title shown when <see cref="FullText"/> does not fit at 11 DIPs, or null.</summary>
    public string? ShortText
    {
        get => _shortText;
        set { _shortText = value; InvalidateMeasure(); }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The size and text are picked from measured text, not by setting them and measuring: a change here invalidates the measure, so
        // each is set once.
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight);
        double TextWidth(string text, double size) => new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, null)
            .WidthIncludingTrailingWhitespace;
        var text = _shortText != null && TextWidth(_fullText, Smallest) > availableSize.Width ? _shortText : _fullText;
        var size = Largest;
        while (size > Smallest && TextWidth(text, size) > availableSize.Width)
            size -= 0.5;
        Text = text;
        FontSize = size;
        return base.MeasureOverride(availableSize);
    }
}

/// <summary>
/// A scrolling grid of fixed slots on the <see cref="ItemCellSize"/> pitch, as many columns as the width holds (or a fixed
/// <see cref="Columns"/>), centred, scrolling vertically. It takes cells and knows nothing about what they show. It wraps a plain ScrollViewer, because a subclass
/// gets no template from the base theme.
/// </summary>
public sealed class DerethSlotGrid : UserControl
{
    private readonly WrapPanel _cells = new()
    {
        ItemWidth = ItemCellSize.Standard.Pitch, ItemHeight = ItemCellSize.Standard.Pitch,
        // The margin keeps the top and bottom rows' drop frames off the scroll area's clipped edge.
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1)
    };

    public DerethSlotGrid()
    {
        var scroll = new ScrollViewer
        {
            Content = _cells,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Content = scroll;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _cells.Width = Columns > 0 ? Columns * _cells.ItemWidth : double.NaN;
        return base.MeasureOverride(availableSize);
    }

    /// <summary>A fixed number of columns, as retail's main pack keeps six whatever the width; zero for as many as the width holds.</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            InvalidateMeasure();
        }
    }
    private int _columns;

    /// <summary>The distance between the left edges of neighbouring columns: the pitch. Width that holds no whole column is split between the sides.</summary>
    public double ColumnPitch => _cells.ItemWidth;

    /// <summary>
    /// Where the columns sit in the width: centred by default, so the width no whole column fits is split between the sides. Left puts it all on
    /// the right, so the first column lines up with a label above the grid.
    /// </summary>
    public HorizontalAlignment ColumnAlignment
    {
        get => _cells.HorizontalAlignment;
        set => _cells.HorizontalAlignment = value;
    }

    /// <summary>The cells in reading order. Each one is a <see cref="DerethSlot"/> or any control sized to the pitch.</summary>
    public Controls Cells => _cells.Children;
}

/// <summary>
/// A slot: a dark well with a thin rim around its content, as in the concept art. Selected, it shows a green wash behind the
/// content, a 2 px green border and a glow inset inside the slot, so the glow never spills into a neighbouring slot.
/// </summary>
public sealed class DerethSlot : Grid
{
    private static readonly IBrush WellBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#2A2519"), 0), new GradientStop(Color.Parse("#17140D"), 1) }
    };
    private static readonly IBrush RimBrush = DerethPalette.Brush(Color.Parse("#4A3F2A"));

    private static readonly IBrush WashBrush = new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(DerethPalette.Teal.WithAlpha(0x70), 0),
            new GradientStop(DerethPalette.Teal.WithAlpha(0x30), 1)
        }
    };

    private static readonly IBrush CarriedBrush = DerethPalette.Brush(DerethPalette.TealText);
    private static readonly BoxShadows SelectedGlow = new(new BoxShadow { IsInset = true, Blur = 6, Color = DerethPalette.Teal.WithAlpha(0xB0) });
    private static readonly BoxShadows CarriedGlow = new(new BoxShadow { IsInset = true, Blur = 10, Spread = 1, Color = DerethPalette.TealText });

    private readonly double _side;
    private readonly bool _flush;
    private readonly Border _wash;
    private readonly Border _outline;
    private bool _carried;

    /// <summary>
    /// A slot holding the content: a framed well round an icon (see <see cref="ItemCellSize"/>), with a selected state. It sits at the
    /// start of its pitch: centred, the pitch's odd pixel would round it left and right in turn, and the gaps between slots would alternate.
    /// A <paramref name="flush"/> slot is only the icon's size, as retail's shortcut slots are, so slots touch: its well's rim shows round
    /// an empty slot and the icon covers it.
    /// </summary>
    public DerethSlot(Control? content, bool flush = false)
    {
        _side = flush ? ItemCellSize.Standard.Icon : ItemCellSize.Standard.Cell;
        // A flush slot asks no width and fills the width it is given, so touching slots share their row's width exactly at any scale;
        // each asking for its side, rounded up to whole pixels, they would run past it.
        _flush = flush;
        HorizontalAlignment = flush ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Background = Brushes.Transparent;
        _wash = new Border { Margin = new Thickness(2), CornerRadius = new CornerRadius(3), Background = WashBrush, IsVisible = false, IsHitTestVisible = false };
        var inner = new Grid();
        inner.Children.Add(_wash);
        if (content != null) inner.Children.Add(content);
        var well = new Border { Name = "Well", Background = WellBrush, BorderBrush = RimBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
        Children.Add(well);
        if (flush) Children.Add(inner);
        else well.Child = inner;
        _outline = new Border
        {
            BorderBrush = DerethPalette.TealBrush, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
            BoxShadow = SelectedGlow,
            IsVisible = false, IsHitTestVisible = false
        };
        Children.Add(_outline);
    }

    /// <summary>Whether the slot shows its selected state.</summary>
    public bool Selected
    {
        get => _outline.IsVisible;
        set
        {
            _wash.IsVisible = value;
            _outline.IsVisible = value;
        }
    }

    /// <summary>Whether a selected slot is carried by a drag of the selection: its border is thicker, brighter and glows more.</summary>
    public bool Carried
    {
        get => _carried;
        set
        {
            _carried = value;
            _outline.BorderThickness = new Thickness(value ? 3 : 2);
            _outline.BorderBrush = value ? CarriedBrush : DerethPalette.TealBrush;
            _outline.BoxShadow = value ? CarriedGlow : SelectedGlow;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(new Size(_side, _side));
        return new Size(_flush ? 0 : _side, _side);
    }
}
