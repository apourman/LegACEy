using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

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
            .SetupWithoutStarting();
        _runtimeInitialized = true;
    }

    /// <summary>Drain queued Avalonia work, run one render-timer tick, and capture the frame.</summary>
    /// <exception cref="InvalidOperationException">Called from a thread other than the one that initialized Avalonia.</exception>
    public void Tick()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AvaloniaPanel));
        Dispatcher.UIThread.VerifyAccess();

        // CaptureRenderedFrame runs the dispatcher jobs and forces one render-timer tick itself.
        using var bitmap = _window.CaptureRenderedFrame();
        if (bitmap == null) return;

        using var locked = bitmap.Lock();
        if (locked.Format != PixelFormat.Bgra8888)
            throw new NotSupportedException($"The Skia backend returned unsupported pixel format {locked.Format}.");

        var width = locked.Size.Width;
        var height = locked.Size.Height;
        if (width != _frame.Width || height != _frame.Height)
            _frame = new PanelFrame(width, height);

        for (var row = 0; row < height; row++)
            Marshal.Copy(IntPtr.Add(locked.Address, row * locked.RowBytes), _frame.Pixels, row * _frame.Stride, _frame.Stride);
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
