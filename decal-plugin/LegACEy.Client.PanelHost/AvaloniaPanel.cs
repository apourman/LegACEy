using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Specialized;
using Rectangle = System.Drawing.Rectangle;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Simple;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.Rendering;
using System.Threading;
using System.Diagnostics;
using LegACEy.Client.Themes;

namespace LegACEy.Client.PanelHost;

/// <summary>
/// Hosts one Avalonia control in an in-memory, Skia-rendered Avalonia.Headless window.
/// </summary>
/// <remarks>
/// Avalonia.Headless binds its dispatcher to the thread that initializes the runtime, and its
/// render timer only fires when that dispatcher is pumped, so no background thread renders.
/// Initialization and every <see cref="Tick"/> must therefore happen on the same thread: the
/// game's render thread in the plugin.
/// </remarks>
public sealed class AvaloniaPanel : IDisposable
{
    private static bool _runtimeInitialized;
    private readonly Window _window;
    private readonly int _ownerThreadId;
    private readonly RetainedFramebuffer _framebuffer;
    // The array behind the frame's dirty rectangles when a capture changed something: its one changed area, reused capture to capture.
    private readonly Rectangle[] _dirty = new Rectangle[1];
    // The frame's pixels as of the last capture, to tell a repaint that changed pixels from one that drew the same.
    private byte[] _captured = Array.Empty<byte>();
    private bool _disposed;
    private bool _forceFullFrame = true;
    private bool _hasInvalidation;
    private bool _renderingSuspended;
    private bool _resized;
    private readonly Stopwatch _captureClock = new();
    internal int FrameCaptureCount { get; private set; }
    // Set when a visual was attached or detached in the window, its theme was applied, or an observed resource took a new nested
    // resource; the next capture walks the render resources then, not every capture.
    private bool _walkRenderResources = true;
    private readonly IDisposable _treeChanges;
    internal int RenderResourceWalks { get; private set; }
    internal IReadOnlyCollection<AvaloniaObject> ObservedRenderResources => _renderResources;
    internal IReadOnlyCollection<INotifyCollectionChanged> ObservedRenderCollections => _renderCollections;
    internal void WalkRenderResources() => ObserveRenderResources();

    private readonly RendererInvalidationObserver _rendererObserver;
    private readonly HashSet<AvaloniaObject> _renderResources = new();
    private readonly HashSet<INotifyCollectionChanged> _renderCollections = new();
    // Scratch for ObserveRenderResources, reused every walk; the property lists are shared by all panels on the UI thread.
    private readonly HashSet<AvaloniaObject> _scanResources = new();
    private readonly HashSet<INotifyCollectionChanged> _scanCollections = new();
    private readonly Stack<Visual> _scanVisuals = new();
    private readonly List<object> _scanRemoved = new();
    private static readonly Dictionary<Type, AvaloniaProperty[]> ResourceProperties = new();
    private IStyle? _themeStyles;
    private IClientTheme? _theme;
    // Use one mouse device per window so implicit capture and click state survive
    // successive events. The pinned headless helper renders around every event;
    // raw delivery leaves dispatcher/render work to Tick instead.
    private readonly MouseDevice _mouseDevice = new();
    private RawInputModifiers _mouseButtons;
    private readonly Stopwatch _inputClock = Stopwatch.StartNew();
    private Point _pointerPosition = new(-1, -1);

    private readonly Control _content;
    private double _scale;

    private AvaloniaPanel(Control content, int width, int height, double scale)
    {
        _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        _content = content;
        _scale = scale;
        _window = CreateWindow(content, width, height, scale);
        _framebuffer = RetainedFramebuffer.Install(_window, width, height);
        _window.Show();
        _rendererObserver = new RendererInvalidationObserver(_window, () => _hasInvalidation = true);
        _window.LayoutUpdated += OnLayoutUpdated;
        _treeChanges = Visual.VisualParentProperty.Changed.AddClassHandler<Visual>(OnVisualParentChanged);
        PanelCompositor.Register(this);
        Tick();
    }

    /// <summary>
    /// The latest rendered BGRA frame. Avalonia draws straight into its pixel buffer, which is kept until the panel is resized; its dirty
    /// rectangles say what the last capture changed.
    /// </summary>
    public PanelFrame Frame => _framebuffer.Frame;

    /// <summary>Whether the last capture changed the frame's pixels; the frame's dirty rectangles say where.</summary>
    public bool Changed { get; private set; }

    /// <summary>Layout passes this panel's window ran in the last advance.</summary>
    public int LayoutPasses { get; private set; }

    /// <summary>
    /// The area, in pixels, of the bounding rectangle of what Avalonia painted for this panel between its previous capture and the last one;
    /// 0 when it painted nothing. It counts painting work, so it can be above 0 while <see cref="Changed"/> is false: a repaint that drew
    /// the same pixels.
    /// </summary>
    public long PaintedArea { get; private set; }

    /// <summary>How long the last capture took: this panel's own share of a frame, apart from Avalonia's shared work.</summary>
    public TimeSpan CaptureTime => _captureClock.Elapsed;

    /// <summary>
    /// Whether the compositor captures this panel. A hidden panel's window is hidden, so Avalonia paints nothing for it; shown again,
    /// its next capture is a full frame of its current content. Takes effect at the next advance.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>The most recent exception raised while processing panel input or rendering.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Raised when an Avalonia event handler or panel rendering operation fails.</summary>
    public event Action<Exception>? Error;

    /// <summary>The control tree hosted by this panel.</summary>
    public Control Content => _content;

    /// <summary>How big the content draws against its design size; see <see cref="Create"/>.</summary>
    public double Scale => _scale;

    /// <summary>
    /// Whether panels can draw at a scale other than 1. The headless window fixes its scaling at 1 behind a private field, which
    /// this Avalonia has; false if an update renamed it.
    /// </summary>
    public static bool CanScale => RenderScaling != null;

    private static readonly FieldInfo? RenderScaling = typeof(AvaloniaHeadlessPlatform).Assembly.GetType("Avalonia.Headless.HeadlessWindowImpl")
        ?.GetField("<RenderScaling>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>A point in panel pixels as the content measures it: the pixels divided by the scale.</summary>
    public Point ToContent(double x, double y) => new(x / _scale, y / _scale);

    /// <summary>True while Avalonia focus belongs to a text entry control.</summary>
    public bool WantsKeyboard => _window.FocusManager?.GetFocusedElement() is TextBox textBox && textBox.IsEffectivelyVisible && textBox.IsEnabled;

    /// <summary>The active standard cursor, if a control requested one.</summary>
    public Cursor? CursorKind
    {
        get
        {
            var visual = _window.GetVisualAt(_pointerPosition);
            while (visual != null)
            {
                if (visual is Control control && control.Cursor != null)
                    return control.Cursor;
                visual = visual.GetVisualParent();
            }
            return _window.Cursor;
        }
    }

    /// <summary>Initialize Avalonia if needed, then build the content and show it in a new panel.</summary>
    /// <param name="createContent">
    /// Builds the panel's control. It runs after initialization because constructing any Avalonia
    /// object first would bind the dispatcher to a placeholder that accepts every thread.
    /// </param>
    /// <param name="width">The panel's width in pixels.</param>
    /// <param name="height">The panel's height in pixels.</param>
    /// <param name="scale">How big the content draws: 0.85 lays it out 1/0.85 times the panel's size and draws it at 85%.</param>
    public static AvaloniaPanel Create(Func<Control> createContent, int width, int height, double scale = 1)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (scale != 1 && !CanScale) throw new NotSupportedException("This Avalonia cannot scale panels; check CanScale first.");
        if (createContent == null) throw new ArgumentNullException(nameof(createContent));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        EnsureRuntimeInitialized();
        return new AvaloniaPanel(createContent(), width, height, scale);
    }

    /// <summary>
    /// The size <paramref name="control"/> takes when drawn at <paramref name="scale"/>, in DIPs. Layout rounding snaps each part of a scaled
    /// layout to whole pixels, so at 85% a 30 DIP slot (25.5 px) takes 26 px: a window sized from an unscaled measure cuts its last pixels off.
    /// The control is measured in a throwaway window at the scale and taken out of it again, so it can then be shown in its own panel.
    /// </summary>
    public static Size MeasureAt(Control control, Size available, double scale)
    {
        if (control == null) throw new ArgumentNullException(nameof(control));
        if (scale != 1 && !CanScale) scale = 1;
        EnsureRuntimeInitialized();
        var window = CreateWindow(control, 1, 1, scale);
        window.Show();
        try
        {
            control.Measure(available);
            return control.DesiredSize;
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    /// <summary>
    /// Initialize Avalonia with CPU Skia and a BGRA headless framebuffer once per process, binding
    /// its dispatcher to the calling thread.
    /// </summary>
    private static void EnsureRuntimeInitialized()
    {
        if (_runtimeInitialized)
        {
            Dispatcher.UIThread.VerifyAccess();
            return;
        }

        AppBuilder.Configure<PanelApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
                FrameBufferFormat = PixelFormat.Bgra8888
            })
            .AfterSetup(_ => ((PanelApplication)Application.Current!).Styles.Add(new SimpleTheme()))
            .SetupWithoutStarting();
        _runtimeInitialized = true;
    }

    private static Window CreateWindow(Control content, int width, int height, double scale)
    {
        var window = new Window
        {
            Width = width / scale,
            Height = height / scale,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            // The headless window reports no transparency support, so without this Avalonia fills the window white under the
            // content, which shows wherever the content is see-through (the drag icon, rounded corners).
            TransparencyBackgroundFallback = Brushes.Transparent,
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            Content = content
        };
        // The content lays out at design size, the panel's size divided by the scale, and renders at the scale as a display's
        // DPI does: layout rounding snaps every edge and border to whole panel pixels, where a scale transform smears 1 px borders.
        // The headless window fixes its scaling at 1, so this sets it.
        if (scale != 1) SetScaling(window, scale);
        return window;
    }

    private static void SetScaling(Window window, double scale)
    {
        var platform = window.PlatformImpl!;
        RenderScaling!.SetValue(platform, scale);
        platform.ScalingChanged?.Invoke(scale);
    }

    /// <summary>
    /// Show this panel, run Avalonia's shared work once (see <see cref="PanelCompositor.Advance"/>) and capture this panel alone, for
    /// single-panel use. In the plugin only the compositor advances. A failure in the shared work goes to the error handler of the panel
    /// that raised it, or of this one when no panel did.
    /// </summary>
    /// <returns>True when the captured pixels differ from the previous frame.</returns>
    /// <exception cref="InvalidOperationException">Called from a thread other than the one that initialized Avalonia.</exception>
    public bool Tick()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AvaloniaPanel));
        VerifyThreadAccess();
        Visible = true;
        try { PanelCompositor.Step(); }
        catch (Exception exception)
        {
            Changed = false;
            ReportError(exception);
            return false;
        }
        Capture();
        return Changed;
    }

    /// <summary>Capture the frame Avalonia rendered, if this panel is shown and its content changed. A failure goes to the error handler.</summary>
    internal void Capture()
    {
        // A disposed panel an advance is still walking reports nothing, not its last advance's pixels.
        Changed = false;
        PaintedArea = 0;
        if (_disposed) return;
        _captureClock.Restart();
        Changed = CaptureFrame();
        _captureClock.Stop();
    }

    /// <summary>
    /// Before the shared step: hide or show the window as <see cref="Visible"/> asks, and start this advance's layout count. True when the
    /// window was shown again or resized: Avalonia then needs a render to settle before the one that paints the latest content.
    /// </summary>
    internal bool BeginStep()
    {
        LayoutPasses = 0;
        var settle = _resized;
        _resized = false;
        if (Visible != _renderingSuspended) return settle;
        _renderingSuspended = !Visible;
        if (!Visible)
        {
            _window.Hide();
            return false;
        }
        _window.Show();
        Content.InvalidateVisual();
        _forceFullFrame = true;
        return true;
    }

    /// <summary>
    /// After a pass of the shared step threw: lay this panel out and commit Avalonia's drawing again. The throw left the rest of the
    /// pass undone (other windows' queued layout among it), and Avalonia commits no window's drawing again until a commit completes.
    /// </summary>
    internal void Resume()
    {
        if (_disposed) return;
        _window.UpdateLayout();
        ((IRenderRoot)_window).Renderer.Paint(new Rect(_window.ClientSize));
    }

    /// <summary>Whether this panel's window shows a visual of <paramref name="type"/>, its overlays and adorners included.</summary>
    internal bool Shows(Type type) => _window.GetSelfAndVisualDescendants().Any(type.IsInstanceOfType);

    private bool CaptureFrame()
    {
        try
        {
            var frame = Frame;
            // A repaint Avalonia did by itself (an unobserved brush mutated in place) is captured too.
            if (_renderingSuspended || (!_forceFullFrame && !_hasInvalidation && !_framebuffer.HasPainted))
            {
                frame.DirtyRectangles = Array.Empty<Rectangle>();
                return false;
            }

            // Avalonia already drew into the frame. What it painted changed, unless it drew the same pixels again (a style or hover that
            // looks the same).
            FrameCaptureCount++;
            var painted = _framebuffer.TakePainted();
            PaintedArea = (long)painted.Width * painted.Height;
            var changed = _forceFullFrame ? new Rectangle(0, 0, frame.Width, frame.Height) : painted;
            if (!KeepCaptured(frame, changed) && !_forceFullFrame) changed = Rectangle.Empty;
            _dirty[0] = changed;
            frame.DirtyRectangles = changed.IsEmpty ? Array.Empty<Rectangle>() : _dirty;
            if (_walkRenderResources) ObserveRenderResources();
            _hasInvalidation = false;
            _forceFullFrame = false;
            return !changed.IsEmpty;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return false;
        }
    }

    /// <summary>Bring the copy of the last capture up to date over <paramref name="area"/>; true when any of its pixels differed.</summary>
    private bool KeepCaptured(PanelFrame frame, Rectangle area)
    {
        if (_captured.Length != frame.Pixels.Length) _captured = new byte[frame.Pixels.Length];
        var differed = false;
        for (var row = area.Top; row < area.Bottom; row++)
        {
            var now = new ReadOnlySpan<byte>(frame.Pixels, row * frame.Stride + area.X * 4, area.Width * 4);
            var then = new Span<byte>(_captured, row * frame.Stride + area.X * 4, area.Width * 4);
            if (now.SequenceEqual(then)) continue;
            now.CopyTo(then);
            differed = true;
        }
        return differed;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => LayoutPasses++;

    /// <summary>
    /// Hide the panel and run queued Avalonia work without rendering. The plugin hides panels through <see cref="Visible"/> instead,
    /// and the compositor's shared pump serves them.
    /// </summary>
    public void DrainDispatcher()
    {
        VerifyUsable();
        Visible = false;
        try
        {
            BeginStep();
            Dispatcher.UIThread.RunJobs();
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>
    /// Resize the panel's window and framebuffer and request a full repaint. The window is resized in place: a new window
    /// would detach the content, and a content that hears its detach (a plugin's window disposes its client then) would be lost.
    /// </summary>
    /// <param name="scale">A new scale the content draws at, as <see cref="Create"/> takes it; null keeps the scale.</param>
    public void Resize(int width, int height, double? scale = null)
    {
        VerifyUsable();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (scale is { } wanted && wanted != _scale)
        {
            if (wanted <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
            if (!CanScale) throw new NotSupportedException("This Avalonia cannot scale panels; check CanScale first.");
            _scale = wanted;
            SetScaling(_window, wanted);
        }
        _forceFullFrame = true;
        _window.Width = width / _scale;
        _window.Height = height / _scale;
        _framebuffer.Resize(width, height);
        // The next advance lays out and renders the new size before it captures, so the capture is not the old size's last frame.
        _resized = true;
    }

    /// <summary>Tell the host that the backing texture was lost and needs a complete upload.</summary>
    public bool ContentLost()
    {
        VerifyUsable();
        _forceFullFrame = true;
        Content.InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Request a render after a custom control invalidates through its own drawing logic.
    /// The optional rectangle limits whether the request intersects this panel. Dirty rectangles are
    /// the area Avalonia painted; resize and content loss can force a full-frame upload.
    /// </summary>
    public void Invalidate(Rectangle? dirtyRegion = null)
    {
        VerifyUsable();
        var clipped = dirtyRegion.HasValue
            ? Rectangle.Intersect(dirtyRegion.Value, new Rectangle(0, 0, Frame.Width, Frame.Height))
            : new Rectangle(0, 0, Frame.Width, Frame.Height);
        if (clipped.Width > 0 && clipped.Height > 0)
            _hasInvalidation = true;
    }

    /// <summary>The pointer moved to a point in panel pixels. Avalonia updates hover state and raises pointer events.</summary>
    public void PointerMove(double x, double y)
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            _pointerPosition = ToContent(x, y);
            SendPointer(RawPointerEventType.Move, ToContent(x, y));
        });
    }

    /// <summary>The left button went down at a point in panel pixels, with the keyboard modifiers held.</summary>
    public void PointerDown(double x, double y, KeyModifiers modifiers = KeyModifiers.None)
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            _pointerPosition = ToContent(x, y);
            // A press outside the focused text box leaves it, as a click elsewhere on a desktop does; a press on another box focuses that one.
            if (_window.FocusManager?.GetFocusedElement() is TextBox focused
                && !(_window.InputHitTest(ToContent(x, y)) is Visual hit && (hit == focused || focused.IsVisualAncestorOf(hit))))
                _window.FocusManager?.ClearFocus();
            SendPointer(RawPointerEventType.Move, ToContent(x, y), ToRawModifiers(modifiers));
            _mouseButtons |= RawInputModifiers.LeftMouseButton;
            SendPointer(RawPointerEventType.LeftButtonDown, ToContent(x, y), ToRawModifiers(modifiers));
        });
    }

    /// <summary>The left button went up at a point in panel pixels.</summary>
    public void PointerUp(double x, double y)
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            _pointerPosition = ToContent(x, y);
            _mouseButtons &= ~RawInputModifiers.LeftMouseButton;
            SendPointer(RawPointerEventType.LeftButtonUp, ToContent(x, y));
        });
    }

    /// <summary>The right button went down and up at a point in panel pixels.</summary>
    public void RightClick(double x, double y)
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            _pointerPosition = ToContent(x, y);
            SendPointer(RawPointerEventType.Move, ToContent(x, y));
            SendPointer(RawPointerEventType.RightButtonDown, ToContent(x, y), RawInputModifiers.RightMouseButton);
            SendPointer(RawPointerEventType.RightButtonUp, ToContent(x, y));
        });
    }

    /// <summary>The pointer left the panel, so nothing in it should show a hover state.</summary>
    public void PointerLeave()
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            _pointerPosition = new Point(-1, -1);
            // Not a move to (-1, -1): below 100% the remembered pixel point truncates to (0, 0), inside the window, and the next repaint hovers it again.
            SendPointer(RawPointerEventType.LeaveWindow, new Point(-1, -1));
        });
    }

    /// <summary>Send a wheel event in panel pixels through Avalonia.Headless.</summary>
    public void MouseWheel(double x, double y, double horizontalDelta, double verticalDelta, KeyModifiers modifiers = KeyModifiers.None)
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            var point = ToContent(x, y);
            _pointerPosition = point;
            var rawModifiers = ToRawModifiers(modifiers);
            SendPointer(RawPointerEventType.Move, point, rawModifiers);
            _window.PlatformImpl!.Input?.Invoke(new RawMouseWheelEventArgs(_mouseDevice,
                (ulong)_inputClock.ElapsedMilliseconds, _window, point,
                new Vector(horizontalDelta, verticalDelta), rawModifiers | _mouseButtons));
        });
    }

    /// <summary>Send a key-down event through Avalonia.Headless with current modifiers.</summary>
    public void KeyDown(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        VerifyUsable();
        RunInput(() => { Invalidate(); _window.KeyPress(key, ToRawModifiers(modifiers)); });
    }

    /// <summary>Send a key-up event through Avalonia.Headless with current modifiers.</summary>
    public void KeyUp(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        VerifyUsable();
        RunInput(() => { Invalidate(); _window.KeyRelease(key, ToRawModifiers(modifiers)); });
    }

    /// <summary>Send committed text through Avalonia.Headless's text-input path.</summary>
    public void TextInput(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        VerifyUsable();
        RunInput(() => { Invalidate(); _window.KeyTextInput(text); });
    }

    /// <summary>Clear Avalonia keyboard focus, for example after a press outside all surfaces.</summary>
    public void ClearFocus()
    {
        VerifyUsable();
        RunInput(() => { Invalidate(); _window.FocusManager?.ClearFocus(); });
    }

    /// <summary>Replace this panel's theme styles without rebuilding its control tree.</summary>
    public void ApplyTheme(IClientTheme theme)
    {
        if (theme == null) throw new ArgumentNullException(nameof(theme));
        VerifyUsable();
        if (_themeStyles != null)
            _window.Styles.Remove(_themeStyles);
        RenderOptions.SetBitmapInterpolationMode(_window, BitmapInterpolationMode.None);
        _themeStyles = theme.CreateStyles();
        _window.Styles.Add(_themeStyles);
        _theme = theme;
        _walkRenderResources = true;
        if (Content is ThemeWindowChrome rootChrome)
            rootChrome.ApplyTheme(theme);
        foreach (var chrome in Content.GetVisualDescendants().OfType<ThemeWindowChrome>())
            chrome.ApplyTheme(theme);
    }

    private void ObserveRenderResources()
    {
        // Composition can update mutable brushes without a SceneInvalidated event.
        // Follow their nested resources (including gradient stops), releasing old trees.
        // Only after a tree or theme change: on a large window the walk costs milliseconds. Between walks a brush assigned in code is not
        // followed, but its repaint is still captured, since what Avalonia painted gates capture too.
        // Scratch sets and the walk stack are reused; only properties that can hold a resource are read (reading value-typed ones would box).
        RenderResourceWalks++;
        var current = _scanResources;
        var collections = _scanCollections;
        current.Clear();
        collections.Clear();
        _scanVisuals.Clear();
        _scanRemoved.Clear();
        _scanVisuals.Push(_window);
        while (_scanVisuals.Count > 0)
        {
            var visual = _scanVisuals.Pop();
            foreach (var property in ResourcePropertiesOf(visual.GetType()))
                CollectRenderResource(visual.GetValue(property), current, collections);
            // Visual children are an IAvaloniaList: index it rather than box an enumerator per visual.
            var children = (IReadOnlyList<Visual>)visual.GetVisualChildren();
            for (var i = children.Count - 1; i >= 0; i--) _scanVisuals.Push(children[i]);
        }

        foreach (var resource in _renderResources)
            if (!current.Contains(resource)) _scanRemoved.Add(resource);
        foreach (AvaloniaObject removed in _scanRemoved)
        {
            removed.PropertyChanged -= OnRenderResourceInvalidated;
            _renderResources.Remove(removed);
        }
        _scanRemoved.Clear();
        foreach (var added in current)
            if (_renderResources.Add(added))
                added.PropertyChanged += OnRenderResourceInvalidated;
        foreach (var collection in _renderCollections)
            if (!collections.Contains(collection)) _scanRemoved.Add(collection);
        foreach (INotifyCollectionChanged removed in _scanRemoved)
        {
            removed.CollectionChanged -= OnRenderCollectionInvalidated;
            _renderCollections.Remove(removed);
        }
        _scanRemoved.Clear();
        foreach (var added in collections)
            if (_renderCollections.Add(added))
                added.CollectionChanged += OnRenderCollectionInvalidated;
        // Cleared only once the walk completed, so a walk that threw runs again at the next capture.
        _walkRenderResources = false;
    }

    /// <summary>The registered properties of a type that can hold a brush, transform or collection: not value types, not strings.</summary>
    private static AvaloniaProperty[] ResourcePropertiesOf(Type type)
    {
        if (!ResourceProperties.TryGetValue(type, out var properties))
            ResourceProperties[type] = properties = AvaloniaPropertyRegistry.Instance.GetRegistered(type)
                .Where(property => !property.PropertyType.IsValueType && property.PropertyType != typeof(string))
                .ToArray();
        return properties;
    }

    private static void CollectRenderResource(object? value, HashSet<AvaloniaObject> resources,
        HashSet<INotifyCollectionChanged> collections)
    {
        if (value is AvaloniaObject resource && resource is not StyledElement && resources.Add(resource))
        {
            foreach (var property in ResourcePropertiesOf(resource.GetType()))
                CollectRenderResource(resource.GetValue(property), resources, collections);
        }
        else if (value is INotifyCollectionChanged collection && collections.Add(collection) && value is IEnumerable items)
        {
            foreach (var item in items)
                CollectRenderResource(item, resources, collections);
        }
    }

    private void OnRenderResourceInvalidated(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        _hasInvalidation = true;
        // A resource that took a new nested one (a brush's transform, its gradient stop list) needs the new one followed.
        if (!e.Property.PropertyType.IsValueType) _walkRenderResources = true;
    }

    private void OnRenderCollectionInvalidated(object? sender, NotifyCollectionChangedEventArgs e) =>
        _hasInvalidation = _walkRenderResources = true;

    // Raised for every visual of every window: only an attach or detach in this panel's window walks again.
    private void OnVisualParentChanged(Visual visual, AvaloniaPropertyChangedEventArgs e)
    {
        if (InWindow(visual) || InWindow(e.OldValue as Visual)) _walkRenderResources = true;
    }

    private bool InWindow(Visual? visual) => visual?.GetVisualRoot() == _window;

    private void StopObservingRenderResources()
    {
        foreach (var resource in _renderResources)
            resource.PropertyChanged -= OnRenderResourceInvalidated;
        _renderResources.Clear();
        foreach (var collection in _renderCollections)
            collection.CollectionChanged -= OnRenderCollectionInvalidated;
        _renderCollections.Clear();
    }

    private void SendPointer(RawPointerEventType type, Point point, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        // Track adjusts a captured thumb's drag origin during arrange. Flush layout
        // before the next event so rapid reversals use the latest geometry, without
        // pumping dispatcher jobs or painting a frame for each mouse movement.
        _window.UpdateLayout();
        _window.PlatformImpl!.Input?.Invoke(new RawPointerEventArgs(_mouseDevice,
            (ulong)_inputClock.ElapsedMilliseconds, _window, type, point, modifiers | _mouseButtons));
    }

    private void RunInput(Action action)
    {
        try { action(); }
        catch (Exception exception) { ReportError(exception); }
    }

    internal void ReportError(Exception exception)
    {
        LastError = exception;
        try { Error?.Invoke(exception); }
        catch { /* Error reporting must not leak back into Decal's callback. */ }
    }

    private static RawInputModifiers ToRawModifiers(KeyModifiers modifiers)
    {
        var raw = RawInputModifiers.None;
        if ((modifiers & KeyModifiers.Shift) != 0) raw |= RawInputModifiers.Shift;
        if ((modifiers & KeyModifiers.Control) != 0) raw |= RawInputModifiers.Control;
        if ((modifiers & KeyModifiers.Alt) != 0) raw |= RawInputModifiers.Alt;
        if ((modifiers & KeyModifiers.Meta) != 0) raw |= RawInputModifiers.Meta;
        return raw;
    }

    private void VerifyUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AvaloniaPanel));
        VerifyThreadAccess();
    }

    private void VerifyThreadAccess()
    {
        if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("An Avalonia panel must be used on the thread that created it.");
        Dispatcher.UIThread.VerifyAccess();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PanelCompositor.Unregister(this);
        StopObservingRenderResources();
        _treeChanges.Dispose();
        _rendererObserver.Dispose();
        _window.LayoutUpdated -= OnLayoutUpdated;
        try { _window.Close(); }
        finally
        {
            _framebuffer.Release();
            _mouseDevice.Dispose();
        }
    }


}

internal sealed class PanelApplication : Application
{
}
