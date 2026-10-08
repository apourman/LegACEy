using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// A 3D view any LegACEy window can contain: the host draws <see cref="Model"/> on the game device over this control's
/// bounds, after the window's own frame. Dragging turns the model and the wheel zooms it. <see cref="Border.Child"/>
/// shows through where nothing is drawn, for a status line while the model loads.
/// </summary>
public sealed class ModelView : Border
{
    private Avalonia.Point? _dragFrom;

    public ModelView()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        PointerPressed += (_, e) => { _dragFrom = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true; };
        PointerMoved += (_, e) =>
        {
            if (_dragFrom is not { } from) return;
            var point = e.GetPosition(this);
            Yaw += (float)(point.X - from.X) * 0.01f;
            _dragFrom = point;
        };
        PointerReleased += (_, e) => { _dragFrom = null; e.Pointer.Capture(null); };
        PointerWheelChanged += (_, e) => { Zoom = Math.Max(0.6f, Math.Min(3f, Zoom * (e.Delta.Y > 0 ? 1.1f : 1 / 1.1f))); e.Handled = true; };
    }

    /// <summary>The model to draw, or null for none. Replaced as a whole, from the UI thread.</summary>
    public CharacterModel? Model { get; set; }

    /// <summary>Turn around Z, in radians. Starts facing the viewer (models face +Y; the camera looks along +Y).</summary>
    public float Yaw { get; set; } = (float)Math.PI;

    public float Zoom { get; set; } = 1f;
}
