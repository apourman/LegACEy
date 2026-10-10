using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// A 3D view any LegACEy window can contain: the host draws <see cref="Model"/> on the game device over this control's
/// bounds, after the window's own frame. Dragging sideways turns the model, dragging up and down moves the view along it, and the
/// wheel zooms toward the height under the pointer. <see cref="Border.Child"/>
/// shows through where nothing is drawn, for a status line while the model loads.
/// </summary>
public sealed class ModelView : Border
{
    // The model's height the view shows at zoom 1, as a share of the model: the renderer's framing.
    public const float Framing = 1.2f;
    public const float MaxZoom = 6f;

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
            // The model follows the pointer: dragging down shows more of what is above.
            Focus = Clamp01(Focus + (float)((point.Y - from.Y) / ViewHeight()) * Framing / Zoom);
            _dragFrom = point;
        };
        PointerReleased += (_, e) => { _dragFrom = null; e.Pointer.Capture(null); };
        PointerWheelChanged += (_, e) =>
        {
            // The height under the pointer stays under it as the zoom changes.
            var offset = (float)(0.5 - e.GetPosition(this).Y / ViewHeight()) * Framing;
            var under = Focus + offset / Zoom;
            Zoom = Math.Max(0.6f, Math.Min(MaxZoom, Zoom * (e.Delta.Y > 0 ? 1.1f : 1 / 1.1f)));
            Focus = Clamp01(under - offset / Zoom);
            e.Handled = true;
        };
    }

    private double ViewHeight() => Math.Max(1, Bounds.Height);

    private static float Clamp01(float value) => Math.Max(0, Math.Min(1, value));

    /// <summary>The model to draw, or null for none. Replaced as a whole, from the UI thread.</summary>
    public CharacterModel? Model { get; set; }

    /// <summary>Turn around Z, in radians. Starts facing the viewer (models face +Y; the camera looks along +Y).</summary>
    public float Yaw { get; set; } = (float)Math.PI;

    public float Zoom { get; set; } = 1f;

    /// <summary>The height the view centres on, as a share of the model's height: 0 its feet, 1 its top.</summary>
    public float Focus { get; set; } = 0.5f;

    /// <summary>Back to the first view: facing the viewer, unzoomed, centred on the model's middle.</summary>
    public void ResetView()
    {
        Yaw = (float)Math.PI;
        Zoom = 1f;
        Focus = 0.5f;
    }
}
