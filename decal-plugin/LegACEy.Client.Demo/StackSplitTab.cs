using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The split amount of the selected stack, in a tab the client shows above the toolbar's selection plaque while a stack is selected: a box,
/// fewer and more buttons, a slider, and buttons for one, half and all of the stack. Each writes retail's amount, clamped to 1 and the count.
/// </summary>
public sealed class StackSplitTab : Decorator
{
    /// <summary>The tab's width, the selection plaque's, so it sits square over it.</summary>
    public const double TabWidth = 220;
    // How narrow and wide its edges can drag it; the slider takes the difference.
    public const double NarrowestWidth = 180, WidestWidth = 640;

    private static readonly IBrush TrackBrush = DerethPalette.Brush(Color.Parse("#0A0906"));
    private static readonly IBrush AmountEdge = DerethPalette.Brush(Color.Parse("#8A6326"));
    private static readonly IBrush ThumbBrush = DerethPalette.Brush(Color.Parse("#DDC47D"));

    private readonly IRetailToolbarPort _port;
    private readonly Action? _startMove;
    // The controls of the stack being split: its stack, the count it shows, whether a write is in progress, and its controls.
    private uint _amountStackId;
    private int _amountSize;
    private bool _amountBusy;
    private TextBox? _amountBox;
    private Slider? _amountSlider;

    /// <param name="startMove">Called when the frame, outside its controls, is pressed with the left button, to move the tab or drag its edge.</param>
    public StackSplitTab(IRetailToolbarPort port, Action? startMove = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _startMove = startMove;
        Width = TabWidth;
        Refresh();
        port.Changed += Refresh;
    }

    /// <summary>True while a stack is selected, when the client shows the tab.</summary>
    public bool IsShown => _amountBox is not null;

    /// <summary>The stack's amount box, while a stack is selected; null otherwise.</summary>
    public TextBox? AmountBox => _amountBox;

    /// <summary>The stack's amount slider, while a stack is selected; null otherwise.</summary>
    public Slider? AmountSlider => _amountSlider;

    public Button? FewerButton { get; private set; }
    public Button? MoreButton { get; private set; }
    public Button? OneButton { get; private set; }
    public Button? HalfButton { get; private set; }
    public Button? AllButton { get; private set; }

    /// <summary>The same controls while the stack stays selected, new ones for another stack, none once no stack is selected.</summary>
    private void Refresh()
    {
        if (_port.Selection is not { Kind: SelectionKind.Stack } stack)
        {
            // The old controls are no longer the current ones, so their focus-loss writes nothing.
            _amountStackId = 0;
            _amountBox = null;
            _amountSlider = null;
            FewerButton = MoreButton = OneButton = HalfButton = AllButton = null;
            Child = null;
            return;
        }
        if (_amountBox is null || _amountStackId != stack.Id) Build(stack.Id);
        ShowAmount(stack.StackCount);
    }

    /// <summary>
    /// The controls of a split. Typing, sliding or a button writes the amount; the box takes the clamped figure when it loses focus or
    /// Enter is pressed. Events from controls that are no longer the current ones write nothing.
    /// </summary>
    private void Build(uint stackId)
    {
        // 18 DIPs tall: the buttons beside it are 20, with their gold line 1 DIP inside the frame, so the edges line up.
        var box = new TextBox
        {
            Foreground = DerethPalette.CreamBrush, CaretBrush = DerethPalette.GoldBrush, FontFamily = DerethPalette.Body, FontSize = 12,
            Background = TrackBrush, BorderBrush = AmountEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 0), MinHeight = 0, Height = 18, Width = 58, TextAlignment = TextAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Minimum = 1, TickFrequency = 1, IsSnapToTickEnabled = true, Template = SliderTemplate(),
            VerticalAlignment = VerticalAlignment.Center, Height = 16
        };
        _amountStackId = stackId;
        _amountBox = box;
        _amountSlider = slider;
        box.TextChanged += (_, _) =>
        {
            // Only typing writes: the text the tab sets itself can raise this event after the refresh that set it.
            if (box == _amountBox && box.IsFocused && int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var typed)) Apply(typed, box);
        };
        // A click into the box selects its figure, so typing replaces it. The press that focuses the box places the caret after focus
        // arrives, so the release selects again.
        var focusing = false;
        box.GotFocus += (_, _) => { box.SelectAll(); focusing = true; };
        box.AddHandler(PointerReleasedEvent, (_, _) => { if (focusing) box.SelectAll(); focusing = false; }, RoutingStrategies.Bubble, handledEventsToo: true);
        box.LostFocus += (_, _) => Settle(box);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Settle(box); };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || slider != _amountSlider || _amountBusy) return;
            // Only the player's slide writes. Laying the slider out moves its value too (to 1 before its range arrives), and that write
            // would choose an amount nobody chose; the slider is put back on the amount instead.
            if (slider.IsPointerOver || slider.IsFocused) Apply((int)Math.Round(slider.Value), slider);
            else ShowAmount(_amountSize);
        };

        FewerButton = AmountButton("−", "One fewer", box, () => _port.SplitAmount - 1);
        MoreButton = AmountButton("+", "One more", box, () => _port.SplitAmount + 1);
        OneButton = AmountButton("1", "Split off one", box, () => 1);
        HalfButton = AmountButton("Half", "Split off half the stack", box, () => Math.Max(1, _amountSize / 2));
        AllButton = AmountButton("All", "Split off the whole stack", box, () => _amountSize);

        var amountRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"), ColumnSpacing = 4, Children = { FewerButton, box, MoreButton, slider } };
        Grid.SetColumn(box, 1);
        Grid.SetColumn(MoreButton, 2);
        Grid.SetColumn(slider, 3);
        var label = DerethPalette.CentreInk(new TextBlock
        {
            Text = "Split", FontFamily = DerethPalette.Title, FontSize = 12, Foreground = DerethPalette.GoldBrush, VerticalAlignment = VerticalAlignment.Center
        });
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, Children = { OneButton, HalfButton, AllButton } };
        var quickRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { label, quick } };
        Grid.SetColumn(quick, 1);
        var frame = new Border
        {
            Background = Brushes.Transparent,
            Child = new DerethFrame(DerethFrameArt.Window) { Child = new StackPanel { Spacing = 4, Margin = new Thickness(4, 2), Children = { quickRow, amountRow } } }
        };
        frame.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(frame).Properties.IsLeftButtonPressed) _startMove?.Invoke();
        };
        Child = frame;
    }

    /// <summary>A small framed button that writes an amount when clicked; a box being typed in takes the figure too.</summary>
    private Button AmountButton(string text, string tip, TextBox box, Func<int> amount)
    {
        var button = new DerethButton
        {
            Content = DerethPalette.CentreInk(new TextBlock { Text = text, FontFamily = DerethPalette.Body, FontSize = 11, Foreground = DerethPalette.CreamBrush, Margin = new Thickness(6, 0) }),
            Height = 20, MinWidth = 22, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => { if (box == _amountBox) Apply(amount(), null); };
        return button;
    }

    /// <summary>Shows the stack's amount at the port's figure, clamped to the count. A box being typed in keeps its text.</summary>
    private void ShowAmount(int size)
    {
        _amountSize = size;
        var amount = Between(_port.SplitAmount, 1, size);
        _amountBusy = true;
        try
        {
            _amountSlider!.Maximum = size;
            _amountSlider.Value = amount;
            if (!_amountBox!.IsFocused) _amountBox.Text = amount.ToString(CultureInfo.InvariantCulture);
        }
        finally { _amountBusy = false; }
    }

    /// <summary>Writes an amount, clamped to 1 and the count, and shows it in the control that did not set it; source null shows it in both.</summary>
    private void Apply(int amount, Control? source)
    {
        if (_amountBusy || _amountBox is null || _amountSlider is null) return;
        _amountBusy = true;
        try
        {
            amount = Between(amount, 1, _amountSize);
            _port.SplitAmount = amount;
            if (source != _amountBox) _amountBox.Text = amount.ToString(CultureInfo.InvariantCulture);
            if (source != _amountSlider) _amountSlider.Value = amount;
        }
        finally { _amountBusy = false; }
    }

    /// <summary>Enter or losing focus settles the box on the figure typed, or the amount in force when none parses.</summary>
    private void Settle(TextBox box)
    {
        if (box != _amountBox) return;
        Apply(int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var typed) ? typed : _port.SplitAmount, null);
    }

    /// <summary>
    /// A dark groove with a gold thumb, as the retail amount slider draws. The track takes the thumb's full height, and its clear buttons
    /// either side of the thumb are what Slider moves the thumb to a press on.
    /// </summary>
    private static FuncControlTemplate<Slider> SliderTemplate() => new((_, scope) =>
    {
        var track = new Track
        {
            Name = "PART_Track",
            Orientation = Orientation.Horizontal,
            [!RangeBase.MinimumProperty] = new TemplateBinding(RangeBase.MinimumProperty),
            [!RangeBase.MaximumProperty] = new TemplateBinding(RangeBase.MaximumProperty),
            [!RangeBase.ValueProperty] = new Binding(nameof(RangeBase.Value)) { Mode = BindingMode.TwoWay, RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) },
            DecreaseButton = TrackButton("PART_DecreaseButton"),
            IncreaseButton = TrackButton("PART_IncreaseButton"),
            Thumb = new Thumb { Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Width = 9, Height = 16, CornerRadius = new CornerRadius(2), Background = ThumbBrush, BorderBrush = AmountEdge, BorderThickness = new Thickness(1) }) }
        };
        scope.Register(track.Name, track);
        scope.Register(track.DecreaseButton.Name!, track.DecreaseButton);
        scope.Register(track.IncreaseButton.Name!, track.IncreaseButton);
        var groove = new Border
        {
            Height = 6, CornerRadius = new CornerRadius(3), Background = TrackBrush, BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center
        };
        return new Panel { Children = { groove, track } };
    });

    /// <summary>A clear button filling the track beside the thumb, so a press anywhere along it reaches the slider.</summary>
    private static RepeatButton TrackButton(string name) => new()
    {
        Name = name, Focusable = false,
        Template = new FuncControlTemplate<RepeatButton>((_, _) => new Border { Background = Brushes.Transparent })
    };

    /// <summary>The value held within [<paramref name="low"/>, <paramref name="high"/>]. netstandard2.0 has no Math.Clamp.</summary>
    private static int Between(int value, int low, int high) => Math.Min(Math.Max(value, low), high);
}
