using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Specialized;
using Rectangle = System.Drawing.Rectangle;
using System.Linq;
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
    private PanelFrame _frame;
    private bool _disposed;
    private bool _forceFullFrame = true;
    private bool _hasInvalidation;
    private bool _renderingSuspended;
    internal int FrameCaptureCount { get; private set; }
    private readonly RendererInvalidationObserver _rendererObserver;
    private readonly HashSet<AvaloniaObject> _renderResources = new();
    private readonly HashSet<INotifyCollectionChanged> _renderCollections = new();
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
    private readonly double _scale;

    private AvaloniaPanel(Control content, int width, int height, double scale)
    {
        _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        _content = content;
        _scale = scale;
        // The content lays out at design size, the panel's size divided by the scale, and draws scaled; hit tests follow the transform.
        _window = CreateWindow(scale == 1 ? content : new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = content },
            width, height);
        _frame = new PanelFrame(width, height);
        _rendererObserver = new RendererInvalidationObserver(_window, () => _hasInvalidation = true);
        Tick();
    }

    /// <summary>The latest rendered BGRA frame. Its pixel buffer is reused across ticks.</summary>
    public PanelFrame Frame => _frame;

    /// <summary>The most recent exception raised while processing panel input or rendering.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Raised when an Avalonia event handler or panel rendering operation fails.</summary>
    public event Action<Exception>? Error;

    /// <summary>The control tree hosted by this panel.</summary>
    public Control Content => _content;

    /// <summary>How big the content draws against its design size; see <see cref="Create"/>.</summary>
    public double Scale => _scale;

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
        if (createContent == null) throw new ArgumentNullException(nameof(createContent));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        EnsureRuntimeInitialized();
        return new AvaloniaPanel(createContent(), width, height, scale);
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

    private static Window CreateWindow(Control content, int width, int height)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            // The headless window reports no transparency support, so without this Avalonia fills the window white under the
            // content, which shows wherever the content is see-through (the drag icon, rounded corners).
            TransparencyBackgroundFallback = Brushes.Transparent,
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            Content = content
        };
        window.Show();
        return window;
    }

    /// <summary>Drain queued Avalonia work, run one render-timer tick, and capture the frame.</summary>
    /// <returns>True when the captured pixels differ from the previous frame.</returns>
    /// <exception cref="InvalidOperationException">Called from a thread other than the one that initialized Avalonia.</exception>
    public unsafe bool Tick()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AvaloniaPanel));
        VerifyThreadAccess();

        try
        {
            if (_renderingSuspended)
            {
                _renderingSuspended = false;
                _window.Show();
                Content.InvalidateVisual();
                _forceFullFrame = true;
                // Showing the headless window first commits its composition target;
                // advance that commit before the normal tick paints its latest content.
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            Dispatcher.UIThread.RunJobs();
            // Let Avalonia process layout and drawing invalidations before deciding
            // whether the rendered frame needs to be copied into our pixel buffer.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (!_forceFullFrame && !_hasInvalidation)
            {
                _frame.DirtyRectangles = Array.Empty<Rectangle>();
                return false;
            }

            // Read the frame produced by the timer without requesting another render.
            FrameCaptureCount++;
            using var bitmap = _window.GetLastRenderedFrame();
            if (bitmap == null) return false;

            using var locked = bitmap.Lock();
            if (locked.Format != PixelFormat.Bgra8888)
                throw new NotSupportedException($"The Skia backend returned unsupported pixel format {locked.Format}.");

            var width = locked.Size.Width;
            var height = locked.Size.Height;
            if (width != _frame.Width || height != _frame.Height)
            {
                _frame = new PanelFrame(width, height);
                _forceFullFrame = true;
            }

            var dirty = _forceFullFrame
                ? new[] { new Rectangle(0, 0, width, height) }
                : Array.Empty<Rectangle>();
            if (dirty.Length == 0)
                _frame.DirtyRectangles = Array.Empty<Rectangle>();
            var actualDirtyRectangles = new List<Rectangle>();
            var changed = false;
            for (var row = 0; row < height; row++)
            {
                var source = new ReadOnlySpan<byte>((byte*)locked.Address + (row * locked.RowBytes), _frame.Stride);
                var target = new Span<byte>(_frame.Pixels, row * _frame.Stride, _frame.Stride);
                var firstChangedPixel = -1;
                var lastChangedPixel = -1;
                for (var x = 0; x < width; x++)
                {
                    if (source.Slice(x * 4, 4).SequenceEqual(target.Slice(x * 4, 4))) continue;
                    if (firstChangedPixel < 0) firstChangedPixel = x;
                    lastChangedPixel = x;
                }
                source.CopyTo(new Span<byte>(_frame.Pixels, row * _frame.Stride, _frame.Stride));
                if (firstChangedPixel < 0) continue;
                changed = true;
                if (dirty.Length != 0) continue;
                var rowRect = new Rectangle(firstChangedPixel, row, lastChangedPixel - firstChangedPixel + 1, 1);
                if (actualDirtyRectangles.Count > 0 &&
                    actualDirtyRectangles[actualDirtyRectangles.Count - 1].Bottom == row &&
                    actualDirtyRectangles[actualDirtyRectangles.Count - 1].Left <= rowRect.Right &&
                    actualDirtyRectangles[actualDirtyRectangles.Count - 1].Right >= rowRect.Left)
                {
                    actualDirtyRectangles[actualDirtyRectangles.Count - 1] = Rectangle.Union(actualDirtyRectangles[actualDirtyRectangles.Count - 1], rowRect);
                }
                else
                    actualDirtyRectangles.Add(rowRect);
            }

            if (dirty.Length != 0)
                _frame.DirtyRectangles = dirty;
            else
                _frame.DirtyRectangles = actualDirtyRectangles;
            ObserveRenderResources();
            _hasInvalidation = false;
            _forceFullFrame = false;
            return dirty.Length != 0 || changed;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return false;
        }
    }

    /// <summary>Drain dispatcher work without rendering, for a surface hidden from the player.</summary>
    public void DrainDispatcher()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AvaloniaPanel));
        VerifyThreadAccess();
        _window.Hide();
        _renderingSuspended = true;
        try { Dispatcher.UIThread.RunJobs(); }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>
    /// Resize the panel's window and framebuffer and request a full repaint. The window is resized in place: a new window
    /// would detach the content, and a content that hears its detach (a plugin's window disposes its client then) would be lost.
    /// </summary>
    public void Resize(int width, int height)
    {
        VerifyUsable();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        _forceFullFrame = true;
        _window.Width = width;
        _window.Height = height;
        _frame = new PanelFrame(width, height);
        // Lay out and render the new size now, so the next capture is the new size and not the last frame of the old one.
        try
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        catch (Exception exception) { ReportError(exception); }
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
    /// The optional rectangle limits whether the request intersects this panel. Incremental dirty
    /// rectangles are derived from full-frame pixel differences; resize and content loss can force
    /// a full-frame upload.
    /// </summary>
    public void Invalidate(Rectangle? dirtyRegion = null)
    {
        VerifyUsable();
        var clipped = dirtyRegion.HasValue
            ? Rectangle.Intersect(dirtyRegion.Value, new Rectangle(0, 0, _frame.Width, _frame.Height))
            : new Rectangle(0, 0, _frame.Width, _frame.Height);
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
            _pointerPosition = new Point(x, y);
            SendPointer(RawPointerEventType.Move, new Point(x, y));
        });
    }

    /// <summary>The left button went down at a point in panel pixels, with the keyboard modifiers held.</summary>
    public void PointerDown(double x, double y, KeyModifiers modifiers = KeyModifiers.None)
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            _pointerPosition = new Point(x, y);
            // A press outside the focused text box leaves it, as a click elsewhere on a desktop does; a press on another box focuses that one.
            if (_window.FocusManager?.GetFocusedElement() is TextBox focused
                && !(_window.InputHitTest(new Point(x, y)) is Visual hit && (hit == focused || focused.IsVisualAncestorOf(hit))))
                _window.FocusManager?.ClearFocus();
            SendPointer(RawPointerEventType.Move, new Point(x, y), ToRawModifiers(modifiers));
            _mouseButtons |= RawInputModifiers.LeftMouseButton;
            SendPointer(RawPointerEventType.LeftButtonDown, new Point(x, y), ToRawModifiers(modifiers));
        });
    }

    /// <summary>The left button went up at a point in panel pixels.</summary>
    public void PointerUp(double x, double y)
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            _pointerPosition = new Point(x, y);
            _mouseButtons &= ~RawInputModifiers.LeftMouseButton;
            SendPointer(RawPointerEventType.LeftButtonUp, new Point(x, y));
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
            SendPointer(RawPointerEventType.Move, new Point(-1, -1));
        });
    }

    /// <summary>Send a wheel event in panel pixels through Avalonia.Headless.</summary>
    public void MouseWheel(double x, double y, double horizontalDelta, double verticalDelta, KeyModifiers modifiers = KeyModifiers.None)
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            var point = new Point(x, y);
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
        if (Content is ThemeWindowChrome rootChrome)
            rootChrome.ApplyTheme(theme);
        foreach (var chrome in Content.GetVisualDescendants().OfType<ThemeWindowChrome>())
            chrome.ApplyTheme(theme);
    }

    private void ObserveRenderResources()
    {
        // Composition can update mutable brushes without a SceneInvalidated event.
        // Follow their nested resources (including gradient stops), releasing old trees.
        var current = new HashSet<AvaloniaObject>();
        var collections = new HashSet<INotifyCollectionChanged>();
        foreach (var visual in _window.GetVisualDescendants().Prepend(_window))
            foreach (var property in AvaloniaPropertyRegistry.Instance.GetRegistered(visual.GetType()))
                CollectRenderResource(visual.GetValue(property), current, collections);

        foreach (var removed in _renderResources.Except(current).ToArray())
        {
            removed.PropertyChanged -= OnRenderResourceInvalidated;
            _renderResources.Remove(removed);
        }
        foreach (var added in current)
            if (_renderResources.Add(added))
                added.PropertyChanged += OnRenderResourceInvalidated;
        foreach (var removed in _renderCollections.Except(collections).ToArray())
        {
            removed.CollectionChanged -= OnRenderCollectionInvalidated;
            _renderCollections.Remove(removed);
        }
        foreach (var added in collections)
            if (_renderCollections.Add(added))
                added.CollectionChanged += OnRenderCollectionInvalidated;
    }

    private static void CollectRenderResource(object? value, HashSet<AvaloniaObject> resources,
        HashSet<INotifyCollectionChanged> collections)
    {
        if (value is AvaloniaObject resource && resource is not StyledElement && resources.Add(resource))
        {
            foreach (var property in AvaloniaPropertyRegistry.Instance.GetRegistered(resource.GetType()))
                CollectRenderResource(resource.GetValue(property), resources, collections);
        }
        else if (value is INotifyCollectionChanged collection && collections.Add(collection) && value is IEnumerable items)
        {
            foreach (var item in items)
                CollectRenderResource(item, resources, collections);
        }
    }

    private void OnRenderResourceInvalidated(object? sender, AvaloniaPropertyChangedEventArgs e) => _hasInvalidation = true;
    private void OnRenderCollectionInvalidated(object? sender, NotifyCollectionChangedEventArgs e) => _hasInvalidation = true;

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

    private void ReportError(Exception exception)
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
        StopObservingRenderResources();
        _rendererObserver.Dispose();
        _window.Close();
        _mouseDevice.Dispose();
    }


}

internal sealed class PanelApplication : Application
{
}
