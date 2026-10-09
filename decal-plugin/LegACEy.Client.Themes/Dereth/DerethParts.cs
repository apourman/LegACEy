using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LegACEy.Client.Themes;

/// <summary>The Dereth palette and fonts. Titles are Palatino, text is Segoe UI, as the game's installed fonts are.</summary>
public static class DerethPalette
{
    public static readonly Color Gold = Color.Parse("#C9A45C");
    public static readonly Color Cream = Color.Parse("#EFE3C4");
    public static readonly Color Text = Color.Parse("#D7D9D6");
    public static readonly Color Muted = Color.Parse("#8E979E");
    public static readonly Color Invalid = Color.Parse("#D9584A");
    public static readonly Color Groove = Color.Parse("#05080C");
    public static readonly Color GrooveEdge = Color.Parse("#2A3542");
    public static readonly Color Navy = Color.Parse("#0A1219");
    /// <summary>The selection colour: a selected slot's wash, border and glow.</summary>
    public static readonly Color Teal = Color.Parse("#3FB3AE");
    /// <summary>Teal text, such as the "N selected" line.</summary>
    public static readonly Color TealText = Color.Parse("#5FC9C4");
    public static readonly FontFamily Title = new("Palatino Linotype, Palatino, Georgia, serif");
    public static readonly FontFamily Body = new("Segoe UI, Tahoma, avares://LegACEy.Client.Themes/Assets#Liberation Sans");

    public static readonly IBrush GoldBrush = Brush(Gold);
    public static readonly IBrush CreamBrush = Brush(Cream);
    public static readonly IBrush TextBrush = Brush(Text);
    public static readonly IBrush MutedBrush = Brush(Muted);
    public static readonly IBrush InvalidBrush = Brush(Invalid);
    public static readonly IBrush GrooveBrush = Brush(Groove);
    public static readonly IBrush GrooveEdgeBrush = Brush(GrooveEdge);
    public static readonly IBrush TealBrush = Brush(Teal);

    public static IBrush Brush(Color color) => new SolidColorBrush(color);

    /// <summary>The same colour at another alpha.</summary>
    public static Color WithAlpha(this Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}

/// <summary>Which sheet art a <see cref="DerethFrame"/> is drawn with.</summary>
public enum DerethFrameArt { Window, Slot, Button }

/// <summary>A corner of a window frame. None means the pointer is over no corner.</summary>
public enum DerethCorner { None, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>A frame of corner and edge pieces, mirrored to all four sides, with its child inside the edges.</summary>
public sealed class DerethFrame : Decorator
{
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
    /// the 8 px lip, as diagonals across the navy.
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
/// turns teal while focused. <see cref="Text"/> is what the
/// player has typed, and <see cref="TextChanged"/> fires on every edit. It knows nothing of what the text filters.
/// </summary>
public sealed class DerethSearchField : DerethThreeSlice
{
    private readonly TextBox _input;

    public DerethSearchField(string placeholder)
        : base(DerethSheet.Search(DerethFieldState.Normal).Left, DerethSheet.Search(DerethFieldState.Normal).Middle, DerethSheet.Search(DerethFieldState.Normal).Right)
    {
        Height = 25;
        // The box is bare: the sheet's field is its frame, so the box's own border, fill and minimum height are cleared.
        _input = new TextBox
        {
            Watermark = placeholder,
            Foreground = DerethPalette.TextBrush, CaretBrush = DerethPalette.GoldBrush, FontFamily = DerethPalette.Body, FontSize = 12,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(36, 0, 6, 0)
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
        Template = new FuncControlTemplate<Button>((_, scope) =>
        {
            var presenter = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                [!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty)
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

/// <summary>A window with the Dereth frame, a header of icon, title and close box, the title rule and content.</summary>
public sealed class DerethWindow : UserControl
{
    public DerethWindow(string title, Control? icon, Control content)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Height = 50 };
        if (icon != null)
        {
            var framed = new DerethFrame(DerethFrameArt.Button) { Width = 40, Height = 40, Child = icon };
            header.Children.Add(framed);
        }
        var name = new TextBlock
        {
            Text = title, Foreground = DerethPalette.CreamBrush, FontSize = 24, FontFamily = DerethPalette.Title,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var glyph = new DerethSprite(DerethSpriteArt.Close) { Width = 12, Height = 12 };
        var close = new DerethButton { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, Content = glyph };
        glyph.Follow(close);
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(6, 2, 6, 6) };
        body.Children.Add(header);
        // The rule runs nearly frame to frame, as in the concept.
        var rule = new DerethRule { Margin = new Thickness(-6, 0, -6, 0) };
        Grid.SetRow(rule, 1);
        body.Children.Add(rule);
        Grid.SetRow(content, 2);
        body.Children.Add(content);
        Content = new DerethFrame(DerethFrameArt.Window) { Child = body };
    }

    public event EventHandler? CloseRequested;
}

/// <summary>
/// A scrolling grid of fixed slots: 46 px cells on a 50 px pitch, as many columns as the width holds, centred, scrolling
/// vertically. It takes cells and knows nothing about what they show. It wraps a plain ScrollViewer, because a subclass
/// gets no template from the base theme.
/// </summary>
public sealed class DerethSlotGrid : UserControl
{
    public const double CellSize = 46;
    public const double Pitch = 50;

    private readonly WrapPanel _cells = new()
    {
        ItemWidth = Pitch, ItemHeight = Pitch, HorizontalAlignment = HorizontalAlignment.Center
    };

    public DerethSlotGrid()
    {
        Content = new ScrollViewer
        {
            Content = _cells,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    /// <summary>The cells in reading order. Each one is a <see cref="DerethSlot"/> or any control sized to the pitch.</summary>
    public Controls Cells => _cells.Children;
}

/// <summary>
/// A slot: a framed well around its content. Selected, it shows a teal wash behind the content, a 2 px teal border and a glow
/// inset inside the slot, so the glow never spills into a neighbouring slot.
/// </summary>
public sealed class DerethSlot : Grid
{
    private static readonly IBrush WashBrush = new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(DerethPalette.Teal.WithAlpha(0x70), 0),
            new GradientStop(DerethPalette.Teal.WithAlpha(0x30), 1)
        }
    };

    private readonly Border _wash;
    private readonly Border _outline;

    /// <summary>A slot holding the content: a 46 px framed well, left-aligned in its pitch, with a selected state.</summary>
    public DerethSlot(Control? content)
    {
        Width = DerethSlotGrid.CellSize;
        Height = DerethSlotGrid.CellSize;
        HorizontalAlignment = HorizontalAlignment.Left;
        Background = Brushes.Transparent;
        _wash = new Border { Margin = new Thickness(2), CornerRadius = new CornerRadius(3), Background = WashBrush, IsVisible = false, IsHitTestVisible = false };
        var inner = new Grid();
        inner.Children.Add(_wash);
        if (content != null) inner.Children.Add(content);
        Children.Add(new DerethFrame(DerethFrameArt.Slot) { Child = inner });
        _outline = new Border
        {
            BorderBrush = DerethPalette.TealBrush, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
            BoxShadow = new BoxShadows(new BoxShadow { IsInset = true, Blur = 10, Color = DerethPalette.Teal.WithAlpha(0xB0) }),
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
}
