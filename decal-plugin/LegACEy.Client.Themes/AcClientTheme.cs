using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Themes;

/// <summary>Retail-art control faces and chrome, with native-size tiling and nearest-neighbour drawing.</summary>
public sealed class AcClientTheme : IClientTheme
{
    public const uint WindowChromeCenterId = 0x06004CC2;
    public const uint ButtonNormalId = 0x06004C4C;
    public const uint ButtonHoverId = 0x06004C4D;
    public const uint ButtonPressedId = 0x06004C4E;
    public const uint RowNormalId = 0x060012B3;
    public const uint RowSelectedId = 0x060012B4;
    public const uint CloseNormalId = 0x06004D0C;
    public const uint ClosePressedId = 0x06004D0D;
    public const uint CheckOffId = 0x06004D15;
    public const uint CheckOnId = 0x06004D17;
    public const uint VitalFillId = 0x06004C3E;
    private readonly IGameArtSource _art;

    public AcClientTheme(IGameArtSource art) => _art = art ?? throw new ArgumentNullException(nameof(art));

    public string Name => "Asheron's Call";
    public bool UsesAcChrome => true;
    public IGameArtSource ArtSource => _art;

    public IStyle CreateStyles()
    {
        var styles = new Styles();
        var surface = Color.FromRgb(0x23, 0x20, 0x19);
        var text = Color.FromRgb(0xe2, 0xd0, 0xa4);
        var accent = Color.FromRgb(0x9a, 0x79, 0x43);
        SimpleClientTheme.AddBase(styles, surface, text, accent);
        // One decoded bitmap per sprite per application of the theme, shared by its control styles.
        var brushes = new Dictionary<uint, IBrush>();
        IBrush Art(uint id, Color fallback)
        {
            if (brushes.TryGetValue(id, out var cached)) return cached;
            var bitmap = GameArtImageExtension.CreateBitmap(_art, id);
            IBrush brush = bitmap == null ? new SolidColorBrush(fallback) : new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                DestinationRect = new RelativeRect(0, 0, bitmap.Size.Width, bitmap.Size.Height, RelativeUnit.Absolute)
            };
            brushes.Add(id, brush);
            return brush;
        }

        var panel = Art(WindowChromeCenterId, surface);
        var normal = Art(ButtonNormalId, surface);
        var hover = Art(ButtonHoverId, accent);
        var pressed = Art(ButtonPressedId, Color.FromRgb(0x18, 0x1b, 0x21));
        var row = Art(RowNormalId, surface);
        var selectedRow = Art(RowSelectedId, accent);
        AddFace<Button>(styles, normal, hover, pressed, ContentControl.ContentProperty);
        AddFace<RepeatButton>(styles, normal, hover, pressed, ContentControl.ContentProperty);
        var close = Art(CloseNormalId, surface);
        if (close is ImageBrush)
        {
            var closePressed = Art(ClosePressedId, accent);
            Add(styles, x => x.OfType<Button>().Class("theme-window-close"), TemplatedControl.BackgroundProperty, close);
            Add(styles, x => x.OfType<Button>().Class("theme-window-close").Class(":pointerover"), TemplatedControl.BackgroundProperty, close);
            Add(styles, x => x.OfType<Button>().Class("theme-window-close").Class(":pressed"), TemplatedControl.BackgroundProperty, closePressed is ImageBrush ? closePressed : close);
            Add(styles, x => x.OfType<Button>().Class("theme-window-close").Template().OfType<GameArtBorder>(), GameArtBorder.SliceProperty, false);
            Add(styles, x => x.OfType<Button>().Class("theme-window-close").Template().OfType<ContentPresenter>(), Visual.IsVisibleProperty, false);
        }
        AddFace<ListBoxItem>(styles, row, selectedRow, selectedRow, ContentControl.ContentProperty);
        AddFace<TabItem>(styles, normal, hover, pressed, HeaderedContentControl.HeaderProperty);
        AddFace<ToolTip>(styles, panel, panel, panel, ContentControl.ContentProperty);
        var toolTipTheme = new ControlTheme(typeof(ToolTip))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, panel),
                new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(text)),
                new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(accent)),
                new Setter(TemplatedControl.FontFamilyProperty, new FontFamily("Tahoma, avares://LegACEy.Client.Themes/Assets#Liberation Sans")),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 2)),
                new Setter(TemplatedControl.TemplateProperty, CreateFaceTemplate<ToolTip>(ContentControl.ContentProperty))
            }
        };
        Add(styles, x => x.Is<Control>(), ClientToolTips.ThemeProperty, toolTipTheme);
        AddState<ListBoxItem>(styles, ":selected", selectedRow);
        AddState<TabItem>(styles, ":selected", pressed);
        AddSurface<TextBox>(styles, panel);
        AddSurface<ListBox>(styles, panel);
        AddSurface<TabControl>(styles, panel);
        AddSurface<ScrollViewer>(styles, panel);
        var checkOff = Icon(Art(CheckOffId, surface));
        var checkOn = Icon(Art(CheckOnId, accent));
        AddSurface<CheckBox>(styles, checkOff);
        var checkHover = Icon(Art(0x06004D16, accent));
        AddState<CheckBox>(styles, ":pointerover", checkHover);
        AddState<CheckBox>(styles, ":pressed", checkOn);
        AddState<CheckBox>(styles, ":checked", checkOn);
        AddState<CheckBox>(styles, ":indeterminate", Icon(Art(0x06004D16, accent)));
        if (checkOff is ImageBrush && checkOn is ImageBrush)
        {
            Add(styles, x => x.OfType<CheckBox>().Class(":checked").Template().OfType<Avalonia.Controls.Shapes.Path>().Name("checkMark"), Visual.IsVisibleProperty, false);
            Add(styles, x => x.OfType<CheckBox>().Class(":indeterminate").Template().OfType<Avalonia.Controls.Shapes.Rectangle>().Name("indeterminateMark"), Visual.IsVisibleProperty, false);
        }
        AddSurface<ProgressBar>(styles, panel);
        Add(styles, x => x.OfType<ProgressBar>(), TemplatedControl.ForegroundProperty, Art(VitalFillId, Color.FromRgb(0xb8, 0x20, 0x20)));
        Add(styles, x => x.OfType<Border>().Class("theme-window-titlebar"), Border.BackgroundProperty, Art(0x06001399, surface));

        // Keep Avalonia's Track/Thumb/RepeatButton wiring; replace only their visible art.
        Add(styles, x => x.OfType<ScrollBar>().Template().OfType<RepeatButton>().Class("repeattrack"), TemplatedControl.BackgroundProperty, Brushes.Transparent);
        foreach (var horizontal in new[] { false, true })
        {
            var orientation = horizontal ? ":horizontal" : ":vertical";
            uint track = horizontal ? 0x06004C7Fu : 0x06004C5Fu;
            uint thumb = horizontal ? 0x06004C83u : 0x06004C63u;
            AddSurface<ScrollBar>(styles, Art(track, surface), orientation);
            Add(styles, x => x.OfType<ScrollBar>().Class(orientation).Template().OfType<Border>(), Border.BackgroundProperty, Art(track, surface));
            for (var state = 0; state < 3; state++)
            {
                var pseudoClass = state == 0 ? null : state == 1 ? ":pointerover" : ":pressed";
                Add(styles, x =>
                {
                    var selector = x.OfType<ScrollBar>().Class(orientation).Template().OfType<Thumb>();
                    return pseudoClass == null ? selector : selector.Class(pseudoClass);
                }, TemplatedControl.BackgroundProperty, Art(thumb + (uint)state, accent));
                foreach (var decrement in new[] { true, false })
                {
                    uint arrow = horizontal ? (decrement ? 0x06004C8Cu : 0x06004C89u) : (decrement ? 0x06004C6Cu : 0x06004C69u);
                    var part = decrement ? "PART_LineUpButton" : "PART_LineDownButton";
                    var arrowBrush = Art(arrow + (uint)state, accent);
                    Add(styles, x =>
                    {
                        var selector = x.OfType<ScrollBar>().Class(orientation).Template().OfType<RepeatButton>().Name(part);
                        return pseudoClass == null ? selector : selector.Class(pseudoClass);
                    }, TemplatedControl.BackgroundProperty, arrowBrush);
                    if (state == 0)
                    {
                        Add(styles, x => x.OfType<ScrollBar>().Class(orientation).Template().OfType<RepeatButton>().Name(part).Template().OfType<GameArtBorder>(), GameArtBorder.SliceProperty, false);
                        if (arrowBrush is ImageBrush)
                            Add(styles, x => x.OfType<ScrollBar>().Class(orientation).Template().OfType<RepeatButton>().Name(part).Child().OfType<Avalonia.Controls.Shapes.Path>(), Visual.IsVisibleProperty, false);
                    }
                }
            }
        }
        Add(styles, x => x.Is<TemplatedControl>(), TemplatedControl.FontFamilyProperty,
            new FontFamily("Tahoma, avares://LegACEy.Client.Themes/Assets#Liberation Sans"));
        Add(styles, x => x.Is<Control>(), Avalonia.Layout.Layoutable.UseLayoutRoundingProperty, true);
        return styles;
    }

    private static IBrush Icon(IBrush brush) => brush is ImageBrush image
        ? new ImageBrush(image.Source) { Stretch = Stretch.None }
        : brush;

    private static void AddFace<T>(Styles styles, IBrush normal, IBrush hover, IBrush pressed, AvaloniaProperty contentProperty) where T : ContentControl
    {
        AddSurface<T>(styles, normal);
        Add(styles, x => x.OfType<T>(), TemplatedControl.CornerRadiusProperty, new CornerRadius(0));
        Add(styles, x => x.OfType<T>(), TemplatedControl.TemplateProperty, CreateFaceTemplate<T>(contentProperty));
        AddState<T>(styles, ":pointerover", hover);
        AddState<T>(styles, ":pressed", pressed);
    }

    private static FuncControlTemplate<T> CreateFaceTemplate<T>(AvaloniaProperty contentProperty) where T : ContentControl =>
        new((control, scope) =>
        {
            var presenter = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                Background = Brushes.Transparent,
                [!TextElement.ForegroundProperty] = new TemplateBinding(TemplatedControl.ForegroundProperty),
                [!TextElement.FontFamilyProperty] = new TemplateBinding(TemplatedControl.FontFamilyProperty),
                [!ContentPresenter.ContentProperty] = new TemplateBinding(contentProperty),
                [!ContentPresenter.ContentTemplateProperty] = new TemplateBinding(typeof(T) == typeof(TabItem) ? HeaderedContentControl.HeaderTemplateProperty : ContentControl.ContentTemplateProperty),
                [!ContentPresenter.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty),
                [!ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty),
                [!ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(ContentControl.VerticalContentAlignmentProperty)
            };
            scope.Register(presenter.Name, presenter);
            return new GameArtBorder(outline: typeof(T) == typeof(ToolTip))
            {
                Child = presenter,
                [!GameArtBorder.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty),
                [!GameArtBorder.BorderBrushProperty] = new TemplateBinding(TemplatedControl.BorderBrushProperty)
            };
        });

    private static void AddSurface<T>(Styles styles, IBrush brush, string? state = null) where T : TemplatedControl
    {
        Add(styles, x => state == null ? x.OfType<T>() : x.OfType<T>().Class(state), TemplatedControl.BackgroundProperty, brush);
        if (state == null)
        {
            foreach (var pseudo in new[] { ":pointerover", ":pressed", ":focus" })
                AddState<T>(styles, pseudo, brush);
        }
    }

    private static void AddState<T>(Styles styles, string state, IBrush brush) where T : TemplatedControl =>
        Add(styles, x => x.OfType<T>().Class(state), TemplatedControl.BackgroundProperty, brush);

    private static void Add(Styles styles, Func<Selector?, Selector> selector, AvaloniaProperty property, object value)
    {
        var style = new Style(selector);
        style.Setters.Add(new Setter(property, value));
        styles.Add(style);
    }
}
