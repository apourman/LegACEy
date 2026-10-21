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
/// retail's UI is locked. It is fixed in shape: per-window scaling sizes it, and its content scales with it.
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

    /// <param name="port">The game's attack bar state and its controls.</param>
    /// <param name="startMove">Starts the window's move from the pointer's place, on a press on the plate that should drag it.</param>
    public DerethAttackBarWindow(IAttackBarPort port, Action startMove)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _startMove = startMove ?? throw new ArgumentNullException(nameof(startMove));

        var plate = new DerethGildedPlate(6, ornate: true, ornamentX: DerethAttackBarArt.PlateOrnamentX)
        {
            Width = DerethAttackBarArt.DesignWidth, Height = DerethAttackBarArt.DesignHeight, Child = _art
        };
        _groove = Hit(DerethAttackBarArt.GrooveHitRect, point => SetPointAt(point.X));
        // Each control is a click target over its drawn place, from the art's rectangles; the plaques are in retail's order, from the top.
        _root = new Canvas { Width = DerethAttackBarArt.DesignWidth, Height = DerethAttackBarArt.DesignHeight, Background = Brushes.Transparent };
        _root.Children.Add(plate);
        _root.Children.Add(_groove);
        for (var i = 0; i < 3; i++)
        {
            var height = (AttackHeight)i;
            _root.Children.Add(Hit(DerethAttackBarArt.PlaqueRect(i), _ => ChooseHeight(height)));
        }
        _root.Children.Add(Hit(DerethAttackBarArt.ToggleHitRect(0), _ => Flip(() => _port.AutoRepeatAttack = !_port.AutoRepeatAttack)));
        _root.Children.Add(Hit(DerethAttackBarArt.ToggleHitRect(1), _ => Flip(() => _port.AutoTarget = !_port.AutoTarget)));
        _root.Children.Add(Hit(DerethAttackBarArt.ToggleHitRect(2), _ => Flip(() => _port.ViewCombatTarget = !_port.ViewCombatTarget)));
        _root.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed && !_port.UiLocked) _startMove();
        };

        // Drawn at the window's height over its design height, so a window scaled up draws its content larger.
        Content = new HeightScaled(DerethAttackBarArt.DesignHeight) { Child = _root };
        _port.Changed += OnPortChanged;
        Sync();
    }

    /// <summary>The window's definition: no title bar, so the plate drags it, and no resizing, so per-window scaling sizes it.</summary>
    public static WindowDefinition DefinitionOf() => new(WindowId, "Attack", DerethAttackBarArt.DesignWidth, DerethAttackBarArt.DesignHeight,
        titleBarHeight: 1, theme: new DerethClientTheme(), dragsFromContent: true);

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
        var fraction = (x - DerethAttackBarArt.GrooveRect.X) / DerethAttackBarArt.GrooveRect.Width;
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

    /// <summary>A transparent click target over <paramref name="rect"/>. Its press is its own: it does not start a move of the window.</summary>
    private Border Hit(Rect rect, Action<Point> press)
    {
        var hit = new Border { Width = rect.Width, Height = rect.Height, Background = Brushes.Transparent };
        Canvas.SetLeft(hit, rect.X);
        Canvas.SetTop(hit, rect.Y);
        hit.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) press(e.GetPosition(_root));
        };
        return hit;
    }
}
