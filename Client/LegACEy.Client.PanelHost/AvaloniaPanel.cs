using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Simple;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Styling;
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
    private PanelFrame _frame;
    private bool _disposed;
    private IStyle? _themeStyles;
    private Point _pointerPosition = new(-1, -1);

    private AvaloniaPanel(Control content, int width, int height)
    {
        _window = new Window
        {
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            Content = content
        };
        _window.Show();
        _frame = new PanelFrame(width, height);
        Tick();
    }

    /// <summary>The latest rendered BGRA frame. Its pixel buffer is reused across ticks.</summary>
    public PanelFrame Frame => _frame;

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

    /// <summary>Drain queued Avalonia work, run one render-timer tick, and capture the frame.</summary>
    /// <returns>True when the captured pixels differ from the previous frame.</returns>
    /// <exception cref="InvalidOperationException">Called from a thread other than the one that initialized Avalonia.</exception>
    public unsafe bool Tick()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AvaloniaPanel));
        Dispatcher.UIThread.VerifyAccess();

        // CaptureRenderedFrame runs the dispatcher jobs and forces one render-timer tick itself.
        using var bitmap = _window.CaptureRenderedFrame();
        if (bitmap == null) return false;

        using var locked = bitmap.Lock();
        if (locked.Format != PixelFormat.Bgra8888)
            throw new NotSupportedException($"The Skia backend returned unsupported pixel format {locked.Format}.");

        var width = locked.Size.Width;
        var height = locked.Size.Height;
        var changed = false;
        if (width != _frame.Width || height != _frame.Height)
        {
            _frame = new PanelFrame(width, height);
            changed = true;
        }

        for (var row = 0; row < height; row++)
        {
            var source = new ReadOnlySpan<byte>((byte*)locked.Address + (row * locked.RowBytes), _frame.Stride);
            var target = new Span<byte>(_frame.Pixels, row * _frame.Stride, _frame.Stride);
            if (source.SequenceEqual(target)) continue;
            source.CopyTo(target);
            changed = true;
        }
        return changed;
    }

    /// <summary>The pointer moved to a point in panel pixels. Avalonia updates hover state and raises pointer events.</summary>
    public void PointerMove(double x, double y)
    {
        VerifyUsable();
        _pointerPosition = new Point(x, y);
        _window.MouseMove(new Point(x, y));
    }

    /// <summary>The left button went down at a point in panel pixels.</summary>
    public void PointerDown(double x, double y)
    {
        VerifyUsable();
        _pointerPosition = new Point(x, y);
        _window.MouseMove(new Point(x, y));
        _window.MouseDown(new Point(x, y), MouseButton.Left);
    }

    /// <summary>The left button went up at a point in panel pixels.</summary>
    public void PointerUp(double x, double y)
    {
        VerifyUsable();
        _pointerPosition = new Point(x, y);
        _window.MouseUp(new Point(x, y), MouseButton.Left);
    }

    /// <summary>The pointer left the panel, so nothing in it should show a hover state.</summary>
    public void PointerLeave()
    {
        VerifyUsable();
        _pointerPosition = new Point(-1, -1);
        _window.MouseMove(new Point(-1, -1));
    }

    /// <summary>Send a wheel event in panel pixels through Avalonia.Headless.</summary>
    public void MouseWheel(double x, double y, double horizontalDelta, double verticalDelta, KeyModifiers modifiers = KeyModifiers.None)
    {
        VerifyUsable();
        var point = new Point(x, y);
        _pointerPosition = point;
        var rawModifiers = ToRawModifiers(modifiers);
        _window.MouseMove(point, rawModifiers);
        _window.MouseWheel(point, new Vector(horizontalDelta, verticalDelta), rawModifiers);
    }

    /// <summary>Send a key-down event through Avalonia.Headless with current modifiers.</summary>
    public void KeyDown(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        VerifyUsable();
        _window.KeyPress(key, ToRawModifiers(modifiers));
    }

    /// <summary>Send a key-up event through Avalonia.Headless with current modifiers.</summary>
    public void KeyUp(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        VerifyUsable();
        _window.KeyRelease(key, ToRawModifiers(modifiers));
    }

    /// <summary>Send committed text through Avalonia.Headless's text-input path.</summary>
    public void TextInput(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        VerifyUsable();
        _window.KeyTextInput(text);
    }

    /// <summary>Clear Avalonia keyboard focus, for example after a press outside all surfaces.</summary>
    public void ClearFocus()
    {
        VerifyUsable();
        _window.FocusManager?.ClearFocus();
    }

    /// <summary>Replace this panel's theme styles without rebuilding its control tree.</summary>
    public void ApplyTheme(IClientTheme theme)
    {
        if (theme == null) throw new ArgumentNullException(nameof(theme));
        VerifyUsable();
        if (_themeStyles != null)
            _window.Styles.Remove(_themeStyles);
        _themeStyles = theme.CreateStyles();
        _window.Styles.Add(_themeStyles);
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
        Dispatcher.UIThread.VerifyAccess();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Close();
    }
}

internal sealed class PanelApplication : Application
{
}
