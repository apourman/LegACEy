using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Inspect window: a Dereth window with the appraised object's 3D paperdoll and nothing else, opened from the appraisal window's
/// Inspect button. One window serves the session: <see cref="Show"/> puts the next object in it.
/// </summary>
public sealed class InspectWindow
{
    public const string WindowId = "inspect";
    public const int Width = 260;
    public const int Height = 360;
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(200, 260));

    private static readonly IBrush StageBrush = new RadialGradientBrush
    {
        GradientStops = { new GradientStop(Color.Parse("#2A2519"), 0), new GradientStop(Color.Parse("#0A0805"), 1) },
    };

    private readonly ILookSource _looks;
    private readonly string _portalPath;
    private readonly Border _stage;

    public InspectWindow(ILookSource looks, string portalPath)
    {
        _looks = looks ?? throw new ArgumentNullException(nameof(looks));
        _portalPath = portalPath;
        _stage = new Border
        {
            Background = StageBrush, CornerRadius = new CornerRadius(3), BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1),
        };
        var hint = new TextBlock
        {
            Text = "Drag to turn · Shift-drag to move", FontSize = 11.5, FontFamily = DerethPalette.Body, Foreground = DerethPalette.MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0),
        };
        DockPanel.SetDock(hint, Dock.Bottom);
        var body = new DockPanel { Margin = new Thickness(4, 8, 4, 2) };
        body.Children.Add(hint);
        body.Children.Add(_stage);
        Window = new DerethWindow(string.Empty, null, body);
        Window.CloseRequested += (_, _) => Close?.Invoke();
    }

    public DerethWindow Window { get; }

    /// <summary>Runs when the player closes the window with its close box.</summary>
    public Action? Close { get; set; }

    /// <summary>The object the window shows, zero before the first.</summary>
    public uint ObjectId { get; private set; }

    /// <summary>Shows the object's paperdoll, under its name and icon. The same object already shown is left as it is, turned as the player left it.</summary>
    public void Show(uint objectId, string name, Control? icon)
    {
        Window.Title = name;
        Window.SetIcon(icon);
        if (objectId == ObjectId) return;
        ObjectId = objectId;
        _stage.Child = new PaperdollView(_looks, _portalPath, objectId);
    }
}
