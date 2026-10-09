using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Data;
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
    public static readonly FontFamily Title = new("Palatino Linotype, Palatino, Georgia, serif");
    public static readonly FontFamily Body = new("Segoe UI, Tahoma, avares://LegACEy.Client.Themes/Assets#Liberation Sans");

    public static IBrush Brush(Color color) => new SolidColorBrush(color);
}

/// <summary>Which sheet art a <see cref="DerethFrame"/> is drawn with.</summary>
public enum DerethFrameArt { Window, Slot, Button }

/// <summary>A frame of corner and edge pieces, mirrored to all four sides, with its child inside the edges.</summary>
public sealed class DerethFrame : Decorator
{
    private readonly NineSlice _art;
    private readonly IBrush _fill;

    public DerethFrame(DerethFrameArt art)
    {
        _art = art switch
        {
            DerethFrameArt.Window => DerethSheet.WindowFrame,
            DerethFrameArt.Slot => DerethSheet.Slot,
            _ => DerethSheet.Button
        };
        _fill = DerethPalette.Brush(_art.Fill);
        Padding = new Thickness(_art.Edge);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        var corner = _art.Corner.Width;
        var edge = _art.Edge;
        if (width < 2 * corner || height < 2 * corner) return;
        context.FillRectangle(_fill, new Rect(Bounds.Size));
        DerethSheet.Draw(context, _art.Top, new Rect(corner, 0, width - 2 * corner, edge));
        DerethSheet.Draw(context, _art.Top, new Rect(corner, height - edge, width - 2 * corner, edge), flipY: true);
        DerethSheet.Draw(context, _art.Left, new Rect(0, corner, edge, height - 2 * corner));
        DerethSheet.Draw(context, _art.Left, new Rect(width - edge, corner, edge, height - 2 * corner), flipX: true);
        DerethSheet.Draw(context, _art.Corner, new Rect(0, 0, corner, corner));
        DerethSheet.Draw(context, _art.Corner, new Rect(width - corner, 0, corner, corner), flipX: true);
        DerethSheet.Draw(context, _art.Corner, new Rect(0, height - corner, corner, corner), flipY: true);
        DerethSheet.Draw(context, _art.Corner, new Rect(width - corner, height - corner, corner, corner), flipX: true, flipY: true);
    }
}

/// <summary>A horizontal strip: a left cap, a stretched middle and a right cap, drawn from three sheet regions.</summary>
public class DerethThreeSlice : Decorator
{
    private readonly Rect _left;
    private readonly Rect _middle;
    private readonly Rect _right;

    internal DerethThreeSlice(Rect left, Rect middle, Rect right)
    {
        _left = left;
        _middle = middle;
        _right = right;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var height = Bounds.Height;
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

/// <summary>The search field: the sheet's field with a placeholder. It is drawn only; nothing reads it yet.</summary>
public sealed class DerethSearchField : DerethThreeSlice
{
    public DerethSearchField(string placeholder)
        : base(DerethSheet.SearchLeft, DerethSheet.SearchMiddle, DerethSheet.SearchRight)
    {
        Height = 30;
        Child = new TextBlock
        {
            Text = placeholder, Foreground = DerethPalette.Brush(DerethPalette.Muted), FontFamily = DerethPalette.Body,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(36, 0, 0, 0)
        };
    }
}

/// <summary>The sheet's paging arrows.</summary>
public enum DerethSpriteArt { PagerPrevious, PagerNext }

/// <summary>A whole sheet region drawn at the control's size.</summary>
public sealed class DerethSprite : Control
{
    private readonly Rect _source;

    public DerethSprite(DerethSpriteArt art)
    {
        _source = art == DerethSpriteArt.PagerNext ? DerethSheet.ArrowNext : DerethSheet.ArrowPrevious;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public override void Render(DrawingContext context) => DerethSheet.Draw(context, _source, new Rect(Bounds.Size));
}

/// <summary>A button in the Dereth button frame. Its content is text or a glyph.</summary>
public sealed class DerethButton : Button
{
    // ponytail: the sheet has one state per region, so hover and pressed looks wait for the art (story 10).
    public DerethButton()
    {
        // A transparent face is still hit-tested, so the frame takes the pointer.
        Background = Brushes.Transparent;
        Template = new FuncControlTemplate<Button>((_, _) =>
        {
            var presenter = new ContentPresenter
            {
                [!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty)
            };
            return new DerethFrame(DerethFrameArt.Button) { Child = presenter };
        });
    }
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
            Text = title, Foreground = DerethPalette.Brush(DerethPalette.Cream), FontSize = 24, FontFamily = DerethPalette.Title,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var close = new DerethButton
        {
            Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center,
            Content = new Path
            {
                Data = Geometry.Parse("M 0,0 L 9,9 M 9,0 L 0,9"), Stroke = DerethPalette.Brush(DerethPalette.Gold), StrokeThickness = 1.6,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        CloseButton = close;

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(6, 2, 6, 6) };
        body.Children.Add(header);
        var rule = new DerethRule();
        Grid.SetRow(rule, 1);
        body.Children.Add(rule);
        Grid.SetRow(content, 2);
        body.Children.Add(content);
        Content = new DerethFrame(DerethFrameArt.Window) { Child = body };
    }

    public event EventHandler? CloseRequested;

    public Button CloseButton { get; }
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

    /// <summary>The cells in reading order. Each one is a <see cref="Cell"/> or any control sized to the pitch.</summary>
    public Controls Cells => _cells.Children;

    /// <summary>A slot holding the content: a 46 px framed well, left-aligned in its pitch.</summary>
    public static Grid Cell(Control? content = null)
    {
        var cell = new Grid { Width = CellSize, Height = CellSize, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent };
        cell.Children.Add(new DerethFrame(DerethFrameArt.Slot) { Child = content });
        return cell;
    }
}
