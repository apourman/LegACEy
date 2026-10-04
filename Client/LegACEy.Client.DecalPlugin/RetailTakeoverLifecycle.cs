using System;
using System.Drawing;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Small native/UI boundary used by the retail takeover state machine.</summary>
internal interface IRetailTakeoverPort
{
    bool Exists { get; }
    bool IsVisible { get; }
    Rectangle GetBounds();
    void SetVisible(bool visible);
    void SetSaveLocation(bool save);
    void MoveTo(Point location);
    bool IsUiLocked { get; }
}

internal interface IRetailTakeoverSurface
{
    Point Location { get; }
    Size Size { get; }
    void SetLocation(Point location);
    bool Visible { get; set; }
}

/// <summary>Owns one retail root element until disposed or a native/UI operation fails.</summary>
internal sealed class RetailTakeoverLifecycle : IDisposable
{
    private readonly IRetailTakeoverPort _native;
    private readonly IRetailTakeoverSurface _surface;
    private bool _captured;
    private bool _originalVisible;
    private bool _disposed;
    private Size? _viewport;
    private Point _lastNativeLocation;
    private Point? _resizeLocation;

    public RetailTakeoverLifecycle(IRetailTakeoverPort native, IRetailTakeoverSurface surface)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    public static RetailTakeoverLifecycle StartSession(RetailTakeoverLifecycle? previous, IRetailTakeoverPort native, IRetailTakeoverSurface surface)
    {
        previous?.Dispose();
        return new RetailTakeoverLifecycle(native, surface);
    }

    public bool CanDrag
    {
        get
        {
            try { return !_disposed && _captured && _native.Exists && !_native.IsUiLocked; }
            catch { Fail(); throw; }
        }
    }

    /// <summary>Returns true when a viewport change requires cancelling the active drag.</summary>
    public bool Tick(bool isDragging = false, Size? viewport = null)
    {
        if (_disposed) return false;
        // Device transitions can briefly supply an empty viewport; retain the previous layout.
        if (viewport is Size empty && (empty.Width <= 0 || empty.Height <= 0)) return false;
        var viewportChanged = viewport.HasValue && _viewport.HasValue && viewport != _viewport;
        if (viewportChanged && _captured)
            _resizeLocation = _lastNativeLocation;
        if (viewport.HasValue)
            _viewport = viewport;
        try
        {
            if (!_native.Exists)
            {
                _captured = false;
                _surface.Visible = false;
                return viewportChanged;
            }
            if (_native.IsVisible)
            {
                _captured = true;
                _originalVisible = true;
                _native.SetVisible(false);
            }
            if (!_captured)
            {
                _surface.Visible = false;
                return viewportChanged;
            }
            var bounds = _native.GetBounds();
            if (_resizeLocation is Point saved && _viewport is Size screen)
            {
                var size = _surface.Size;
                var location = new Point(
                    Math.Max(0, Math.Min(screen.Width - Math.Max(size.Width, bounds.Width), saved.X)),
                    Math.Max(0, Math.Min(screen.Height - Math.Max(size.Height, bounds.Height), saved.Y)));
                bounds = MoveNative(location);
                // RenderFrame precedes retail layout: also recover on the following frame
                // in case the layout reset happens after this frame's viewport observation.
                if (!viewportChanged)
                    _resizeLocation = null;
                isDragging = false;
            }
            _lastNativeLocation = bounds.Location;
            // Keep provisional drag coordinates until MoveTo commits them to retail.
            if (!isDragging)
                _surface.SetLocation(bounds.Location);
            _surface.Visible = true;
            return viewportChanged;
        }
        catch
        {
            Fail();
            throw;
        }
    }

    public bool MoveTo(Point location)
    {
        if (!CanDrag || !_captured) return false;
        try
        {
            _lastNativeLocation = MoveNative(location).Location;
            _resizeLocation = null;
            return true;
        }
        catch
        {
            Fail();
            throw;
        }
    }

    private Rectangle MoveNative(Point location)
    {
        // Retail MoveTo notifies layout saving only when this flag is enabled.
        // Leave it enabled so retail can retain the player's position after unload.
        _native.SetSaveLocation(true);
        _native.MoveTo(location);
        return _native.GetBounds();
    }

    public void Dispose()
    {
        if (_disposed && !_captured) return;
        _disposed = true;
        Restore();
    }

    private void Fail()
    {
        _disposed = true;
        try { Restore(); }
        catch { /* Keep the captured state so Dispose can retry restoration. */ }
    }

    private void Restore()
    {
        if (!_captured) return;
        try { _surface.Visible = false; }
        finally
        {
            if (_native.Exists)
                _native.SetVisible(_originalVisible);
            _captured = false;
        }
    }
}
