using System;
using System.Drawing;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Small native/UI boundary used by the retail takeover state machine.</summary>
internal interface IRetailTakeoverPort
{
    bool IsVisible { get; }
    Rectangle GetBounds();
    void SetVisible(bool visible);
    void MoveTo(Point location);
    bool IsUiLocked { get; }
}

internal interface IRetailTakeoverSurface
{
    Point Location { get; }
    void SetLocation(Point location);
    bool Visible { get; set; }
}

/// <summary>Owns one retail root element until disposed or a native/UI operation fails.</summary>
internal sealed class RetailTakeoverLifecycle : IDisposable
{
    private readonly IRetailTakeoverPort _native;
    private readonly IRetailTakeoverSurface _surface;
    private bool _captured;
    private bool _disposed;

    public RetailTakeoverLifecycle(IRetailTakeoverPort native, IRetailTakeoverSurface surface)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    public bool CanDrag => !_disposed && !_native.IsUiLocked;

    public void Tick()
    {
        if (_disposed) return;
        try
        {
            if (!_native.IsVisible) return;
            var bounds = _native.GetBounds();
            _captured = true;
            _native.SetVisible(false);
            _surface.SetLocation(bounds.Location);
            _surface.Visible = true;
        }
        catch
        {
            Restore();
            _disposed = true;
            throw;
        }
    }

    public bool MoveTo(Point location)
    {
        if (!CanDrag || !_captured) return false;
        try
        {
            _native.MoveTo(location);
            return true;
        }
        catch
        {
            Restore();
            _disposed = true;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
    }

    private void Restore()
    {
        if (!_captured) return;
        _captured = false;
        _surface.Visible = false;
        _native.SetVisible(true);
    }
}
