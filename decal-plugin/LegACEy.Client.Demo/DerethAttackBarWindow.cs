using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// LegACEy's attack bar, drawn as the mockup's gilded panel: retail's layout one for one at half its width, 320 by 58 DIPs, in the Dereth theme.
/// It shows in melee and missile only. A click on the bar sets the set point, a plaque sets the height and a gem flips an option, each through
/// <see cref="IAttackBarPort"/>; the charge and every value follow the port. A press on the plate away from the controls drags the window, unless
/// retail's UI is locked. A drag on an edge or a corner gives it more width or height: the bar lengthens and the rows spread
/// (see <see cref="DerethAttackLayout"/>), while the text keeps its size. Per-window scaling draws the whole bar larger or smaller.
/// </summary>
public sealed class DerethAttackBarWindow : UserControl, IDisposable
{
    /// <summary>The window's id, under which its place and size are kept for the character.</summary>
    public const string WindowId = "dereth-attack";

    private readonly IAttackBarPort _port;
    private readonly Action _startMove;
    private readonly DerethAttackBarArt _art = new();
    private readonly Border _groove;
    private readonly Canvas _root;
    private readonly DerethGildedPlate _plate;
    private readonly Border[] _plaques = new Border[3];
    private readonly Border[] _toggles = new Border[3];
    private readonly DerethCornerMarks _corners = new() { IsVisible = false };

    /// <param name="port">The game's attack bar state and its controls.</param>
    /// <param name="startMove">Starts the window's move from the pointer's place, on a press on the plate that should drag it.</param>
    public DerethAttackBarWindow(IAttackBarPort port, Action startMove)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _startMove = startMove ?? throw new ArgumentNullException(nameof(startMove));

        _plate = new DerethGildedPlate(6, ornate: true) { Child = _art };
        _groove = Hit(point => SetPointAt(point.X));
        // Each control is a click target over its drawn place, placed from the layout at the window's size; the plaques are in retail's
        // order, from the top.
        _root = new Canvas { Background = Brushes.Transparent };
        _root.Children.Add(_plate);
        _root.Children.Add(_groove);
        for (var i = 0; i < 3; i++)
        {
            var height = (AttackHeight)i;
            _root.Children.Add(_plaques[i] = Hit(_ => ChooseHeight(height)));
        }
        _root.Children.Add(_toggles[0] = Hit(_ => Flip(() => _port.AutoRepeatAttack = !_port.AutoRepeatAttack)));
        _root.Children.Add(_toggles[1] = Hit(_ => Flip(() => _port.AutoTarget = !_port.AutoTarget)));
        _root.Children.Add(_toggles[2] = Hit(_ => Flip(() => _port.ViewCombatTarget = !_port.ViewCombatTarget)));
        _root.Children.Add(_corners);
        // Hovering shows where the window resizes, unless retail's UI is locked.
        _root.PointerEntered += (_, _) => _corners.IsVisible = !_port.UiLocked;
        _root.PointerExited += (_, _) => _corners.IsVisible = false;
        _root.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed && !_port.UiLocked) _startMove();
        };

        _root.SizeChanged += (_, e) => Place(e.NewSize);
        Content = _root;
        Place(new Size(DerethAttackBarArt.DesignWidth, DerethAttackBarArt.DesignHeight));
        _port.Changed += OnPortChanged;
        Sync();
    }

    /// <summary>The window's definition: no title bar, so the plate drags it, and resized freely within its limits unless retail's UI is locked.</summary>
    public static WindowDefinition DefinitionOf(Func<bool> uiLocked) => new(WindowId, "Attack", DerethAttackBarArt.DesignWidth,
        DerethAttackBarArt.DesignHeight, titleBarHeight: 1, theme: new DerethClientTheme(), resizing: Resizing(uiLocked), dragsFromContent: true);

    /// <summary>How the bar resizes: from 280 by 58 DIPs to 960 by 150, in either direction on its own, unless retail's UI is locked.</summary>
    public static WindowResizing Resizing(Func<bool> uiLocked) => new(
        new System.Drawing.Size(DerethAttackBarArt.MinimumWidth, DerethAttackBarArt.DesignHeight),
        new System.Drawing.Size(DerethAttackBarArt.MaximumWidth, DerethAttackBarArt.MaximumHeight), uiLocked);

    /// <summary>Whether the resize corners show: while the pointer is over the bar and retail's UI is unlocked.</summary>
    public bool CornersVisible => _corners.IsVisible;

    /// <summary>Whether the bar shows: in melee and missile, and hidden in peace and magic.</summary>
    public bool Shown => _root.IsVisible;

    /// <summary>
    /// Raised when <see cref="Shown"/> flips. The client host does not listen: its takeover closes the window outside melee and missile, so
    /// the window is never left open hidden, where its content would still take input.
    /// </summary>
    public event Action? ShownChanged;

    /// <summary>The face the bar shows, as last synced from the port.</summary>
    public DerethAttackFace Face => _art.Face;

    /// <summary>The hover text of the groove: the stance's value and the set point as a percentage, such as "Power 62%".</summary>
    public string? BarTooltip => ToolTip.GetTip(_groove) as string;

    public void Dispose() => _port.Changed -= OnPortChanged;

    private void OnPortChanged(object? sender, EventArgs e) => Sync();

    private void SetPointAt(double x)
    {
        var groove = new DerethAttackLayout(_root.Bounds.Size).Groove;
        var fraction = (x - groove.X) / groove.Width;
        // Retail's power bar clamps the set point to 0 to 1, so the bar does too.
        _port.SetPoint = DerethAttackBarArt.Clamp01(fraction);
        Sync();
    }

    private void ChooseHeight(AttackHeight height)
    {
        _port.Height = height;
        Sync();
    }

    private void Flip(Action change)
    {
        change();
        Sync();
    }

    /// <summary>Shows what the port holds: whether the bar is up, the face and the groove's hover text.</summary>
    private void Sync()
    {
        var stance = _port.Stance;
        var accuracy = stance == RetailStance.Missile;
        var shown = stance is RetailStance.Melee or RetailStance.Missile;
        if (shown != _root.IsVisible)
        {
            _root.IsVisible = shown;
            ShownChanged?.Invoke();
        }
        _art.Show(new DerethAttackFace(accuracy, _port.SetPoint, _port.Charge, (int)_port.Height,
            _port.AutoRepeatAttack, _port.AutoTarget, _port.ViewCombatTarget));
        var percent = (int)Math.Round(_port.SetPoint * 100);
        ToolTip.SetTip(_groove, $"{(accuracy ? "Accuracy" : "Power")} {percent}%");
    }

    /// <summary>Lays the plate, the click targets and the resize corners out for <paramref name="size"/>.</summary>
    private void Place(Size size)
    {
        var layout = new DerethAttackLayout(size);
        Put(_plate, new Rect(size));
        Put(_corners, new Rect(size));
        _plate.OrnamentX = layout.OrnamentX;
        Put(_groove, layout.GrooveHit);
        for (var i = 0; i < 3; i++)
        {
            Put(_plaques[i], layout.Plaque(i));
            Put(_toggles[i], layout.ToggleHit(i));
        }
    }

    private static void Put(Control control, Rect rect)
    {
        Canvas.SetLeft(control, rect.X);
        Canvas.SetTop(control, rect.Y);
        control.Width = rect.Width;
        control.Height = rect.Height;
    }

    /// <summary>A transparent click target. Its press is its own: it does not start a move of the window.</summary>
    private Border Hit(Action<Point> press)
    {
        var hit = new Border { Background = Brushes.Transparent };
        hit.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) press(e.GetPosition(_root));
        };
        return hit;
    }
}
