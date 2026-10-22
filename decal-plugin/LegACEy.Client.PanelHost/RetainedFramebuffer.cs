using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform.Surfaces;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Rectangle = System.Drawing.Rectangle;

namespace LegACEy.Client.PanelHost;

/// <summary>
/// The surface a panel's headless window renders into: the panel's frame, kept between renders, so Avalonia's compositor draws straight
/// into it and only inside its dirty area. Only that painted area is read back: the panel compares it with, and copies it into, its copy
/// of the last capture, to tell new pixels from the same ones drawn again.
/// </summary>
/// <remarks>
/// The headless window's own surface creates a new bitmap for every render (each one adds GC memory pressure) and keeps nothing, so the
/// compositor draws into a layer, blits all of it, and the frame then has to be copied out and diffed. This one object is the window's
/// surface, its render target and every lock on it, so a render allocates nothing here. It replaces the surface in the headless window's
/// private Surfaces field, and reads the compositor target's dirty area and full-redraw flag, all internal to Avalonia 11.3.22; they are
/// bound when the first panel is created and fail it loudly if an update moved them. Used on the UI thread only, as the panels are.
/// </remarks>
internal sealed class RetainedFramebuffer : IFramebufferPlatformSurface, IFramebufferRenderTargetWithProperties, ILockedFramebuffer
{
    private static readonly FieldInfo? Surfaces = typeof(AvaloniaHeadlessPlatform).Assembly.GetType("Avalonia.Headless.HeadlessWindowImpl")
        ?.GetField("<Surfaces>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly ITopLevelImpl _window;
    private readonly object _target;
    private PanelFrame _frame = null!;
    private GCHandle _pin;
    private bool _fresh;
    private bool _locked;
    private Rectangle _painted;

    private RetainedFramebuffer(ITopLevelImpl window, object target, int width, int height)
    {
        _window = window;
        _target = target;
        Allocate(width, height);
    }

    /// <summary>
    /// Render <paramref name="window"/> into a kept frame of the given size. Call before the window first renders: Avalonia makes its render
    /// target from the surface then.
    /// </summary>
    /// <exception cref="NotSupportedException">This Avalonia keeps the headless window's surfaces or the compositor's dirty area elsewhere.</exception>
    public static RetainedFramebuffer Install(Window window, int width, int height)
    {
        var platform = window.PlatformImpl!;
        if (Surfaces?.GetValue(platform) == null)
            throw new NotSupportedException("Avalonia's headless window no longer keeps its surfaces where LegACEy replaces them.");
        var framebuffer = new RetainedFramebuffer(platform, CompositorTarget.Of(window), width, height);
        Surfaces.SetValue(platform, new object[] { framebuffer });
        return framebuffer;
    }

    /// <summary>
    /// The frame Avalonia draws into. It is the panel's size, which is also the compositor's: the compositor rounds client size × scaling,
    /// and the panel sets its client size to its size ÷ scaling. (The headless window's own surface took the ceiling, a pixel more when
    /// the division is inexact.)
    /// </summary>
    public PanelFrame Frame => _frame;

    /// <summary>Whether Avalonia painted anything since the last <see cref="TakePainted"/>.</summary>
    public bool HasPainted => !_painted.IsEmpty;

    /// <summary>The union of the areas Avalonia painted since the last call, then clears it: renders can run between two captures.</summary>
    public Rectangle TakePainted()
    {
        var painted = _painted;
        _painted = Rectangle.Empty;
        return painted;
    }

    /// <summary>Replace the frame with a new one of this size, which the next render paints whole.</summary>
    /// <exception cref="InvalidOperationException">Avalonia is drawing into the frame.</exception>
    public void Resize(int width, int height)
    {
        if (_locked) throw new InvalidOperationException("A panel's frame cannot be replaced while Avalonia draws into it.");
        Release();
        Allocate(width, height);
    }

    /// <summary>
    /// Unpin the frame, while no lock is outstanding. Skia keeps a surface on the frame's address between locks, but every lock checks the
    /// address and size it is handed and makes a new surface when either changed, so a frame replaced or released is never drawn to again;
    /// a lock after the last release throws instead.
    /// </summary>
    public void Release()
    {
        if (_pin.IsAllocated) _pin.Free();
    }

    private void Allocate(int width, int height)
    {
        _frame = new PanelFrame(width, height);
        // Pinned while Avalonia may draw into it: Skia wraps the address and reuses it render after render.
        _pin = GCHandle.Alloc(_frame.Pixels, GCHandleType.Pinned);
        _fresh = true;
        _painted = Rectangle.Empty;
    }

    public IFramebufferRenderTarget CreateFramebufferRenderTarget() => this;

    public bool RetainsFrameContents => true;

    public ILockedFramebuffer Lock() => Lock(out _);

    public ILockedFramebuffer Lock(out FramebufferLockProperties properties)
    {
        if (_locked) throw new InvalidOperationException("A panel's frame is already locked.");
        if (!_pin.IsAllocated) throw new ObjectDisposedException(nameof(RetainedFramebuffer));
        _locked = true;
        properties = new FramebufferLockProperties(!_fresh);
        // The compositor has updated its scene and paints its dirty area once this returns. A new frame, or a full redraw it asked for, must
        // be painted whole; without a layer Avalonia 11.3.22 widens the area by its unused layer's size, which is empty, so widen it here.
        var whole = new Rectangle(0, 0, _frame.Width, _frame.Height);
        if (_fresh || CompositorTarget.FullRedrawRequested(_target))
            CompositorTarget.AddDirty(_target, new LtrbPixelRect { Right = whole.Width, Bottom = whole.Height });
        _fresh = false;
        var dirty = CompositorTarget.Dirty(_target);
        var area = Rectangle.Intersect(whole, Rectangle.FromLTRB(dirty.Left, dirty.Top, dirty.Right, dirty.Bottom));
        if (area.Width > 0 && area.Height > 0) _painted = _painted.IsEmpty ? area : Rectangle.Union(_painted, area);
        return this;
    }

    public IntPtr Address => _pin.IsAllocated ? _pin.AddrOfPinnedObject() : IntPtr.Zero;

    public PixelSize Size => new(_frame.Width, _frame.Height);

    public int RowBytes => _frame.Stride;

    public Vector Dpi => new Vector(96, 96) * _window.RenderScaling;

    public PixelFormat Format => PixelFormat.Bgra8888;

    /// <summary>Ends a lock, or the render target as the window closes. Neither unpins the frame; <see cref="Release"/> does.</summary>
    public void Dispose() => _locked = false;

    /// <summary>The compositor target's internals the frame reads and widens, compiled once.</summary>
    private static class CompositorTarget
    {
        private static readonly Type ServerType = typeof(Compositor).Assembly
            .GetType("Avalonia.Rendering.Composition.Server.ServerCompositionTarget") ?? throw Moved("ServerCompositionTarget");
        private static readonly ParameterExpression Target = Expression.Parameter(typeof(object));
        private static readonly ParameterExpression Area = Expression.Parameter(typeof(LtrbPixelRect));
        private static readonly UnaryExpression Server = Expression.Convert(Target, ServerType);
        private static readonly MemberExpression DirtyRects = Expression.Field(Server, ServerType.GetField("DirtyRects") ?? throw Moved("DirtyRects"));

        /// <summary>The area the compositor paints next: DirtyRects.CombinedRect.</summary>
        public static readonly Func<object, LtrbPixelRect> Dirty = Expression.Lambda<Func<object, LtrbPixelRect>>(
            Expression.Property(DirtyRects, DirtyRects.Type.GetProperty("CombinedRect") ?? throw Moved("CombinedRect")), Target).Compile();

        /// <summary>Set when the target's own properties (size, scaling, shown) changed: the whole window must be painted.</summary>
        public static readonly Func<object, bool> FullRedrawRequested = Expression.Lambda<Func<object, bool>>(Expression.Field(Server,
            ServerType.GetField("_fullRedrawRequested", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw Moved("_fullRedrawRequested")),
            Target).Compile();

        public static readonly Action<object, LtrbPixelRect> AddDirty = Expression.Lambda<Action<object, LtrbPixelRect>>(
            Expression.Call(DirtyRects, DirtyRects.Type.GetMethod("AddRect") ?? throw Moved("AddRect"), Area), Target, Area).Compile();

        /// <summary>A window's server-side compositor target: its CompositingRenderer's CompositionTarget's Server.</summary>
        public static object Of(Window window)
        {
            var renderer = ((IRenderRoot)window).Renderer;
            var target = renderer.GetType().GetProperty("CompositionTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(renderer);
            // CompositionTarget's Server hides its base class's, so only the declared one has the target's type.
            var server = target?.GetType().GetProperty("Server", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?.GetValue(target);
            return server != null && ServerType.IsInstanceOfType(server) ? server : throw Moved("CompositionTarget.Server");
        }

        private static NotSupportedException Moved(string member) =>
            new($"Avalonia's compositor no longer has {member} where LegACEy reads a panel's dirty area; revalidate RetainedFramebuffer.");
    }
}
