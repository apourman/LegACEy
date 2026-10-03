using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Threading;

namespace LegACEy.Client.PanelHost;

/// <summary>
/// Hosts one Avalonia control in an in-memory Skia-rendered top level. The host is advanced
/// explicitly by the engine's render callback; it creates no background timer or UI thread.
/// </summary>
public sealed class AvaloniaPanel : IDisposable
{
    private static bool _runtimeInitialized;
    private readonly Window _window;
    private PanelFrame _frame;
    private bool _disposed;

    private AvaloniaPanel(Control content, int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

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
        Dispatcher.UIThread.RunJobs();
        _frame = new PanelFrame(width, height, new byte[checked(width * height * 4)]);
        RenderFrame();
    }

    public PanelFrame Frame => _frame;

    public static AvaloniaPanel Create(Control content, int width, int height)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        EnsureRuntimeInitialized();
        return new AvaloniaPanel(content, width, height);
    }

    /// <summary>Initialize Avalonia's in-memory top level and CPU Skia renderer once per process.</summary>
    public static void EnsureRuntimeInitialized()
    {
        if (_runtimeInitialized || Application.Current != null)
        {
            _runtimeInitialized = true;
            return;
        }

        AppBuilder.Configure<PanelApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        _runtimeInitialized = true;
    }

    /// <summary>Drain queued Avalonia work, advance the render clock, and capture the latest frame.</summary>
    public void Tick(TimeSpan elapsed)
    {
        ThrowIfDisposed();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        RenderFrame();
    }

    /// <summary>Resize the offscreen surface and force a complete frame after the next tick.</summary>
    public void Resize(int width, int height)
    {
        ThrowIfDisposed();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        _window.Width = width;
        _window.Height = height;
        Dispatcher.UIThread.RunJobs();
        _frame = new PanelFrame(width, height, new byte[checked(width * height * 4)]);
        RenderFrame();
    }

    /// <summary>Request a full redraw after the game recreates its D3D texture.</summary>
    public void ContentLost()
    {
        ThrowIfDisposed();
        _window.InvalidateVisual();
        Tick(TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Close();
    }

    private void RenderFrame()
    {
        using var bitmap = _window.CaptureRenderedFrame();
        if (bitmap == null) return;

        using var locked = bitmap.Lock();
        var width = locked.Size.Width;
        var height = locked.Size.Height;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(IntPtr.Add(locked.Address, row * locked.RowBytes), pixels, row * stride, stride);
        }

        if (locked.Format == PixelFormat.Rgba8888)
        {
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);
            }
        }
        else if (locked.Format != PixelFormat.Bgra8888)
        {
            throw new NotSupportedException($"The Skia backend returned unsupported pixel format {locked.Format}.");
        }

        _frame = new PanelFrame(width, height, pixels);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AvaloniaPanel));
    }
}

internal sealed class PanelApplication : Application
{
}
