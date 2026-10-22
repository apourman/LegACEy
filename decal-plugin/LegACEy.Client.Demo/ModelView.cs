using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// A 3D view any LegACEy window can contain: the host draws <see cref="Model"/> on the game device over this control's
/// bounds, after the window's own frame. Dragging turns the model around its centre the way the pointer moves, however it is turned
/// already: sideways around the view's upright axis, up and down toward or away from the viewer, so an item that lies on its side can be
/// stood up. Dragging with Shift held moves the view up and down the model instead, and the wheel zooms toward the height under the pointer. <see cref="Border.Child"/>
/// shows through where nothing is drawn, for a status line while the model loads.
/// </summary>
public sealed class ModelView : Border
{
    // The model's height the view shows at zoom 1, as a share of the model: the renderer's framing. Tight, so the character fills its view.
    public const float Framing = 1.06f;
    public const float MinZoom = 0.3f;
    public const float MaxZoom = 12f;

    // The views in a visual tree, so the host finds them without walking every window each frame. UI thread only.
    private static readonly List<ModelView> AttachedViews = new();

    /// <summary>Every view now in a visual tree, in any window.</summary>
    public static IReadOnlyList<ModelView> Attached => AttachedViews;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachedViews.Add(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AttachedViews.Remove(this);
    }

    private Avalonia.Point? _dragFrom;
    private bool _panning;

    public ModelView()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        PointerPressed += (_, e) =>
        {
            _dragFrom = e.GetPosition(this);
            _panning = !TurnsOnly && (e.KeyModifiers & KeyModifiers.Shift) != 0;
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
                // About the view's axes, after the turn the model has, so a drag always turns it the way the pointer goes.
                Orientation = Quaternion.Normalize(Quaternion.Concatenate(Orientation, Quaternion.Concatenate(
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(point.X - from.X) * 0.01f),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, TurnsOnly ? 0 : (float)(point.Y - from.Y) * 0.01f))));
            _dragFrom = point;
        };
        PointerReleased += (_, e) => { _dragFrom = null; e.Pointer.Capture(null); };
        PointerWheelChanged += (_, e) =>
        {
            if (TurnsOnly) return;
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

    /// <summary>
    /// How the model is turned, applied to it before the view: Z is up, the camera looks along +Y and X runs across the view. Starts
    /// upright and facing the viewer (models face +Y).
    /// </summary>
    public Quaternion Orientation { get; set; } = Facing;

    private static readonly Quaternion Facing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.PI);

    public float Zoom { get; set; } = 1f;

    /// <summary>Dragging only turns the model about its upright axis: no tilt, no Shift-drag move, no wheel zoom.</summary>
    public bool TurnsOnly { get; set; }

    /// <summary>The height the view centres on, as a share of the model's height: 0 its feet, 1 its top.</summary>
    public float Focus { get; set; } = 0.5f;

    /// <summary>Back to the first view: facing the viewer, unzoomed, centred on the model's middle.</summary>
    public void ResetView()
    {
        Orientation = Facing;
        Zoom = 1f;
        Focus = 0.5f;
    }
}
