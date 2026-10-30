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
    private const float FieldOfView = (float)(Math.PI / 6);

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
                // About the view's axes, after the turn the model has, so a drag always turns it the way the pointer goes.
                Orientation = Quaternion.Normalize(Quaternion.Concatenate(Orientation, Quaternion.Concatenate(
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(point.X - from.X) * 0.01f),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(point.Y - from.Y) * 0.01f))));
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

    /// <summary>
    /// Null: the host draws the model over its window. Set: the host fills the view with this colour and draws the model before the window,
    /// so the window's text lies over it, as retail draws its examination figure; the window must leave the view see-through
    /// (<c>DerethWindow.SeeThrough</c>).
    /// </summary>
    public Color? Backdrop { get; set; }

    /// <summary>The model to draw, or null for none. Replaced as a whole, from the UI thread.</summary>
    public CharacterModel? Model { get; set; }

    /// <summary>
    /// How the model is turned, applied to it before the view: Z is up, the camera looks along +Y and X runs across the view. Starts
    /// upright and facing the viewer (models face +Y).
    /// </summary>
    public Quaternion Orientation { get; set; } = Facing;

    private static readonly Quaternion Facing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.PI);

    public float Zoom { get; set; } = 1f;

    /// <summary>Model parts the host outlines in <see cref="OutlineColour"/>; replaced as a whole.</summary>
    public IReadOnlyCollection<int> Outlined { get; set; } = Array.Empty<int>();

    /// <summary>The outline's colour, as RGB.</summary>
    public uint OutlineColour { get; set; }


    /// <summary>The height the view centres on, as a share of the model's height: 0 its feet, 1 its top.</summary>
    public float Focus { get; set; } = 0.5f;

    /// <summary>
    /// The world, view and projection the host draws a model with in a view of the size, turned, zoomed and centred as given; null when
    /// there is nothing to frame. The camera stands back far enough for the model's height, and further when the view is too narrow for
    /// its width at any turn (a shield, a sword lying down, a wide stance in a slim view), so it always shows whole. An
    /// empty model or no zoom gives no distance: CreateLookAt would make NaNs. System.Numerics builds the same right-handed matrices as
    /// D3DX's RotationZ, LookAtRH and PerspectiveFovRH.
    /// </summary>
    public static (Matrix4x4 World, Matrix4x4 View, Matrix4x4 Projection)? Camera(CharacterModel model, float width, float height, Quaternion orientation,
        float zoom, float focus)
    {
        var tall = model.Top - model.Bottom;
        var span = Math.Max(tall, 2 * model.Radius * height / width);
        var fit = span * Framing / 2 / (float)Math.Tan(FieldOfView / 2);
        if (!(fit > 0) || !(zoom > 0) || !(width > 0) || !(height > 0)) return null;
        var middle = model.Bottom + tall * focus;
        var distance = fit / zoom;
        // Turned around its centre at the height the view centres on, so the model spins in place where the player is looking.
        return (Matrix4x4.CreateTranslation(-model.CentreX, -model.CentreY, -middle) * Matrix4x4.CreateFromQuaternion(orientation) * Matrix4x4.CreateTranslation(0, 0, middle),
            Matrix4x4.CreateLookAt(new Vector3(0, -distance, middle), new Vector3(0, 0, middle), Vector3.UnitZ),
            Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, width / height, 0.1f, distance + 5));
    }

    /// <summary>The model part (see <see cref="ModelMesh.Parts"/>) drawn at a point of this view, the nearest where parts overlap; -1 where none is.</summary>
    public int PartAt(Point point)
    {
        float width = (float)Bounds.Width, height = (float)Bounds.Height;
        if (Model is not { } model || Camera(model, width, height, Orientation, Zoom, Focus) is not { } camera) return -1;
        var transform = camera.World * camera.View * camera.Projection;
        float x = (float)point.X, y = (float)point.Y;
        var part = -1;
        var nearest = float.MaxValue;
        foreach (var mesh in model.Meshes)
            for (var i = 0; i + 2 < mesh.Vertices.Count; i += 3)
            {
                Vector3 a = OnView(mesh.Vertices[i]), b = OnView(mesh.Vertices[i + 1]), c = OnView(mesh.Vertices[i + 2]);
                var area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
                if (area == 0) continue;
                var u = ((b.X - x) * (c.Y - y) - (c.X - x) * (b.Y - y)) / area;
                var v = ((c.X - x) * (a.Y - y) - (a.X - x) * (c.Y - y)) / area;
                var w = 1 - u - v;
                if (u < 0 || v < 0 || w < 0) continue;
                var depth = u * a.Z + v * b.Z + w * c.Z;
                if (depth >= nearest) continue;
                nearest = depth;
                part = mesh.Parts[i];
            }
        return part;

        // Where a vertex lands on this view, in its own units, with its depth (smaller is nearer).
        Vector3 OnView(ModelVertex vertex)
        {
            var clip = Vector4.Transform(new Vector3(vertex.X, vertex.Y, vertex.Z), transform);
            return new Vector3((clip.X / clip.W + 1) / 2 * width, (1 - clip.Y / clip.W) / 2 * height, clip.Z / clip.W);
        }
    }

    /// <summary>Back to the first view: facing the viewer, unzoomed, centred on the model's middle.</summary>
    public void ResetView()
    {
        Orientation = Facing;
        Zoom = 1f;
        Focus = 0.5f;
    }
}
