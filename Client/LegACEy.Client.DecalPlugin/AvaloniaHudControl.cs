using System;
using System.Drawing;
using System.Runtime.InteropServices;
using LegACEy.Client.PanelHost;
using Microsoft.DirectX.Direct3D;
using VirindiViewService;
using VirindiViewService.Controls;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace LegACEy.Client.DecalPlugin;

/// <summary>A Virindi View Service control that draws an Avalonia panel's latest frame.</summary>
/// <remarks>
/// VVS caches each view and redraws a control only after <see cref="HudControl.Invalidate"/>,
/// so the owner invalidates this control whenever the panel reports a changed frame.
/// </remarks>
internal sealed class AvaloniaHudControl : HudControl
{
    private readonly AvaloniaPanel _panel;
    private DxTexture? _texture;
    private Surface? _staging;

    public AvaloniaHudControl(AvaloniaPanel panel)
    {
        _panel = panel;
    }

    /// <summary>The pointer moved over the panel; the point is in panel pixels.</summary>
    public event Action<Point>? PointerMoved;

    /// <summary>The left button went down over the panel; the point is in panel pixels.</summary>
    public event Action<Point>? PointerPressed;

    public override void MouseMove(Point pt)
    {
        base.MouseMove(pt);
        PointerMoved?.Invoke(ToPanel(pt));
    }

    public override void MouseDown(Point pt)
    {
        base.MouseDown(pt);
        PointerPressed?.Invoke(ToPanel(pt));
    }

    public override void DrawNow(DxTexture iSavedTarget)
    {
        base.DrawNow(iSavedTarget);
        var frame = _panel.Frame;
        var size = new Size(frame.Width, frame.Height);

        // VVS disposes its textures before a device reset, so recreate the texture on the next draw.
        if (_texture == null || _texture.IsDisposed || _texture.Width != frame.Width || _texture.Height != frame.Height)
        {
            _texture?.Dispose();
            using (var blank = new Bitmap(frame.Width, frame.Height, DrawingPixelFormat.Format32bppArgb))
                _texture = new DxTexture(blank);
        }

        Upload(frame, _texture.Underlying);
        iSavedTarget.DrawTexture(_texture, new Rectangle(Point.Empty, size), new Rectangle(ClipRegion.Location, size));
    }

    public override void Dispose()
    {
        base.Dispose();
        _texture?.Dispose();
        _texture = null;
        _staging?.Dispose();
        _staging = null;
    }

    private Point ToPanel(Point pt) => new(pt.X - ClipRegion.Left, pt.Y - ClipRegion.Top);

    /// <summary>
    /// Copy the BGRA frame into a system-memory surface and send it to the default-pool texture,
    /// the same upload path VVS uses for bitmap textures.
    /// </summary>
    private void Upload(PanelFrame frame, Texture texture)
    {
        var device = Service.Game_D3DDevice;
        if (_staging == null || _staging.Description.Width != frame.Width || _staging.Description.Height != frame.Height)
        {
            _staging?.Dispose();
            _staging = device.CreateOffscreenPlainSurface(frame.Width, frame.Height, Format.A8R8G8B8, Pool.SystemMemory);
        }

        var bits = _staging.LockRectangle(LockFlags.None, out var pitch);
        try
        {
            if (bits.InternalData == IntPtr.Zero || pitch < frame.Stride)
                throw new InvalidOperationException($"The staging surface returned an invalid lock (pitch {pitch}, row {frame.Stride}).");

            for (var row = 0; row < frame.Height; row++)
                Marshal.Copy(frame.Pixels, row * frame.Stride, IntPtr.Add(bits.InternalData, row * pitch), frame.Stride);
        }
        finally
        {
            _staging.UnlockRectangle();
        }

        using var level = texture.GetSurfaceLevel(0);
        device.UpdateSurface(_staging, level);
    }
}
