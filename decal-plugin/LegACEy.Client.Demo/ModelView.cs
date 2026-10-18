using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// A 3D view any LegACEy window can contain: the host draws <see cref="Model"/> on the game device over this control's
/// bounds, after the window's own frame. Dragging turns the model: sideways around its upright axis, up and down tilting it toward or
/// away from the viewer, so an item that lies on its side can be stood up. Dragging with Shift held moves the view up and down the
/// model instead, and the wheel zooms toward the height under the pointer. <see cref="Border.Child"/>
/// shows through where nothing is drawn, for a status line while the model loads.
/// </summary>
public sealed class ModelView : Border
{
    // The model's height the view shows at zoom 1, as a share of the model: the renderer's framing. Tight, so the character fills its view.
    public const float Framing = 1.06f;
    public const float MinZoom = 0.3f;
    public const float MaxZoom = 12f;

    private Avalonia.Point? _dragFrom;
    private bool _panning;

    public ModelView()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        PointerPressed += (_, e) =>
        {
            _dragFrom = e.GetPosition(this);
            _panning = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            e.Pointer.Capture(this);
            e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (_dragFrom is not { } from) return;
            var point = e.GetPosition(this);
            if (_panning)
                // The model follows the pointer: dragging down shows more of what is above.
                Focus = Clamp01(Focus + (float)((point.Y - from.Y) / ViewHeight()) * Framing / Zoom);
            else
            {
                Yaw += (float)(point.X - from.X) * 0.01f;
                Pitch += (float)(point.Y - from.Y) * 0.01f;
            }
            _dragFrom = point;
        };
        PointerReleased += (_, e) => { _dragFrom = null; e.Pointer.Capture(null); };
        PointerWheelChanged += (_, e) =>
        {
            // The height under the pointer stays under it as the zoom changes.
            var offset = (float)(0.5 - e.GetPosition(this).Y / ViewHeight()) * Framing;
            var under = Focus + offset / Zoom;
            Zoom = Math.Max(MinZoom, Math.Min(MaxZoom, Zoom * (e.Delta.Y > 0 ? 1.1f : 1 / 1.1f)));
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

    /// <summary>Tilt around the horizontal axis across the view, in radians: dragging down tips the model's top toward the viewer. Starts upright.</summary>
    public float Pitch { get; set; }

    public float Zoom { get; set; } = 1f;

    /// <summary>The height the view centres on, as a share of the model's height: 0 its feet, 1 its top.</summary>
    public float Focus { get; set; } = 0.5f;

    /// <summary>Back to the first view: facing the viewer, unzoomed, centred on the model's middle.</summary>
    public void ResetView()
    {
        Yaw = (float)Math.PI;
        Pitch = 0f;
        Zoom = 1f;
        Focus = 0.5f;
    }
}
