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
    private bool _originalVisible;
    private bool _disposed;

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

    public void Tick(bool isDragging = false)
    {
        if (_disposed) return;
        try
        {
            if (!_native.Exists)
            {
                _captured = false;
                _surface.Visible = false;
                return;
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
                return;
            }
            // Keep provisional drag coordinates until MoveTo commits them to retail.
            if (!isDragging)
                _surface.SetLocation(_native.GetBounds().Location);
            _surface.Visible = true;
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
            _native.MoveTo(location);
            return true;
        }
        catch
        {
            Fail();
            throw;
        }
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
