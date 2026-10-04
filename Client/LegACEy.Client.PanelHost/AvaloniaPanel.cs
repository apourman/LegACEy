using System;
using System.Collections.Generic;
using Rectangle = System.Drawing.Rectangle;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Simple;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Styling;
using System.Threading;
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
    private Window _window;
    private readonly int _ownerThreadId;
    private PanelFrame _frame;
    private bool _disposed;
    private bool _forceFullFrame = true;
    private bool _hasInvalidation;
    private readonly HashSet<Control> _observedControls = new();
    private IStyle? _themeStyles;
    private IClientTheme? _theme;
    private Point _pointerPosition = new(-1, -1);

    private AvaloniaPanel(Control content, int width, int height)
    {
        _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        _window = CreateWindow(content, width, height);
        _frame = new PanelFrame(width, height);
        ObserveDescendants(Content);
        Tick();
    }

    /// <summary>The latest rendered BGRA frame. Its pixel buffer is reused across ticks.</summary>
    public PanelFrame Frame => _frame;

    /// <summary>The most recent exception raised while processing panel input or rendering.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Raised when an Avalonia event handler or panel rendering operation fails.</summary>
    public event Action<Exception>? Error;

    /// <summary>The control tree hosted by this panel.</summary>
    public Control Content => (Control)_window.Content!;

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
    public static AvaloniaPanel Create(Func<Control> createContent, int width, int height)
    {
        if (createContent == null) throw new ArgumentNullException(nameof(createContent));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        EnsureRuntimeInitialized();
        return new AvaloniaPanel(createContent(), width, height);
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
            Dispatcher.UIThread.RunJobs();
            if (!_forceFullFrame && !_hasInvalidation)
            {
                _frame.DirtyRectangles = Array.Empty<Rectangle>();
                return false;
            }

            // CaptureRenderedFrame drives the headless renderer once after dispatcher work.
            using var bitmap = _window.CaptureRenderedFrame();
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

            var resized = width != _frame.Width || height != _frame.Height;
            var dirty = _forceFullFrame || resized
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
        try { Dispatcher.UIThread.RunJobs(); }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>Resize the panel's framebuffer and request a full repaint.</summary>
    public void Resize(int width, int height)
    {
        VerifyUsable();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        _forceFullFrame = true;
        StopObservingControls();
        var content = Content;
        _window.Content = null;
        _window.Close();
        _window = CreateWindow(content, width, height);
        _frame = new PanelFrame(width, height);
        if (_theme != null)
            ApplyTheme(_theme);
        ObserveDescendants(content);
    }

    /// <summary>Tell the host that the backing texture was lost and needs a complete upload.</summary>
    public bool ContentLost()
    {
        VerifyUsable();
        _forceFullFrame = true;
        Content.InvalidateVisual();
        return true;
    }

    /// <summary>Request a render after a custom control invalidates through its own drawing logic.</summary>
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
            _window.MouseMove(new Point(x, y));
        });
    }

    /// <summary>The left button went down at a point in panel pixels.</summary>
    public void PointerDown(double x, double y)
    {
        VerifyUsable();
        RunInput(() =>
        {
            Invalidate();
            _pointerPosition = new Point(x, y);
            _window.MouseMove(new Point(x, y));
            _window.MouseDown(new Point(x, y), MouseButton.Left);
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
            _window.MouseUp(new Point(x, y), MouseButton.Left);
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
            _window.MouseMove(new Point(-1, -1));
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
            _window.MouseMove(point, rawModifiers);
            _window.MouseWheel(point, new Vector(horizontalDelta, verticalDelta), rawModifiers);
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

    private void ObserveDescendants(Control root)
    {
        ObserveControl(root);
        foreach (var descendant in root.GetVisualDescendants().OfType<Control>())
            ObserveControl(descendant);
    }

    private void ObserveControl(Control control)
    {
        if (!_observedControls.Add(control)) return;
        control.PropertyChanged += OnControlPropertyChanged;
        control.AttachedToVisualTree += OnControlAttachedToVisualTree;
    }

    private void OnControlAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
            ObserveDescendants(control);
    }

    private void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not Control control) return;
        if (e.Property.Name == "Bounds" || e.Property.Name == "Width" || e.Property.Name == "Height")
        {
            _hasInvalidation = true;
            return;
        }

        var origin = control.TranslatePoint(new Point(0, 0), Content);
        if (origin == null || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            _hasInvalidation = true;
            return;
        }

        var left = (int)Math.Floor(origin.Value.X);
        var top = (int)Math.Floor(origin.Value.Y);
        var right = (int)Math.Ceiling(origin.Value.X + control.Bounds.Width);
        var bottom = (int)Math.Ceiling(origin.Value.Y + control.Bounds.Height);
        var clipped = Rectangle.Intersect(new Rectangle(left, top, right - left, bottom - top),
            new Rectangle(0, 0, _frame.Width, _frame.Height));
        if (clipped.Width > 0 && clipped.Height > 0)
            _hasInvalidation = true;
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
        StopObservingControls();
        _window.Close();
    }

    private void StopObservingControls()
    {
        foreach (var control in _observedControls)
        {
            control.PropertyChanged -= OnControlPropertyChanged;
            control.AttachedToVisualTree -= OnControlAttachedToVisualTree;
        }
        _observedControls.Clear();
    }
}

internal sealed class PanelApplication : Application
{
}
