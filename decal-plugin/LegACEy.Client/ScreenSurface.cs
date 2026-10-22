using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using LegACEy.Client.PanelHost;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// An Avalonia panel drawn straight onto the game's Direct3D 9 device at a screen position, with no window chrome around it.
/// </summary>
/// <remarks>
/// The texture lives in the managed pool, so Direct3D keeps a system-memory copy and restores it after a device reset; there is
/// nothing to recreate. The frame event advances every panel at once (<see cref="PanelCompositor"/>), then each surface uploads its
/// panel's frame there and draws it later from the post-UI EndScene seam.
/// </remarks>
internal sealed unsafe class ScreenSurface : IDisposable
{
    private readonly IntPtr _device;
    private readonly PanelFailureBudget _failureBudget = new();
    private IntPtr _texture;
    private bool _uploaded;
    private int _textureWidth;
    private int _textureHeight;

    public ScreenSurface(IntPtr device, AvaloniaPanel panel)
    {
        _device = device;
        Panel = panel;
        Panel.Visible = false;
    }

    public AvaloniaPanel Panel { get; }

    public Point Location { get; set; }

    /// <summary>Whether the surface draws. A hidden surface's panel is hidden too, so it costs nothing until it shows again.</summary>
    public bool Visible
    {
        get => Panel.Visible;
        set => Panel.Visible = value;
    }

    // Timings and counts since the last performance log line; the runtime reads and resets them.
    public FrameSamples TickTimes { get; } = new();

    public FrameSamples UploadTimes { get; } = new();

    public FrameSamples DrawTimes { get; } = new();

    public int ChangedFrames { get; set; }

    public int DirtyRectangles { get; set; }

    public Rectangle Bounds => new(Location, new Size(Panel.Frame.Width, Panel.Frame.Height));

    /// <summary>Upload the frame this game frame's compositor advance captured, without drawing it.</summary>
    public void Prepare()
    {
        if (!Visible)
            return;
        var changed = Panel.Changed;
        var elapsed = Panel.CaptureTime;
        TickTimes.Add(elapsed.TotalMilliseconds);
        if (changed) ChangedFrames++;
        DirtyRectangles += Panel.Frame.DirtyRectangles.Count;
        if (Panel.LastError != null)
            return;
        if (_failureBudget.Record(elapsed))
            throw new TimeoutException($"Panel capture exceeded 50 ms for {_failureBudget.ConsecutiveSlowTicks} consecutive frames ({elapsed.TotalMilliseconds:F1} ms).");
        // _uploaded is true only while a texture holds a complete frame.
        if (changed || !_uploaded)
            Upload(Panel.Frame);
    }

    /// <summary>Draw the prepared frame at its current location.</summary>
    public void DrawNow()
    {
        if (!Visible || !_uploaded)
            return;
        var start = Stopwatch.GetTimestamp();
        Draw(Panel.Frame.Width, Panel.Frame.Height);
        DrawTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    public void Dispose()
    {
        Release(_texture);
        _texture = IntPtr.Zero;
        _uploaded = false;
        Panel.Dispose();
    }

    private void Upload(PanelFrame frame)
    {
        if (_texture != IntPtr.Zero && (_textureWidth != frame.Width || _textureHeight != frame.Height))
        {
            Release(_texture);
            _texture = IntPtr.Zero;
            _uploaded = false;
        }
        if (_texture == IntPtr.Zero)
        {
            _texture = CreateTexture(_device, frame.Width, frame.Height);
            _textureWidth = frame.Width;
            _textureHeight = frame.Height;
        }

        var start = Stopwatch.GetTimestamp();
        IReadOnlyList<Rectangle> dirty = _uploaded ? frame.DirtyRectangles : new[] { new Rectangle(0, 0, frame.Width, frame.Height) };
        for (var i = 0; i < dirty.Count; i++)
        {
            var rect = dirty[i];
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            D3D9.Upload(_texture, rect, frame.Pixels, frame.Stride);
        }
        UploadTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        _uploaded = true;
    }

    /// <summary>The states both overlay draws share: no depth, fog, lighting, alpha test, stencil, scissor or culling; stage 1 off.</summary>
    private static void SetOverlayStates(IntPtr device, uint vertexFormat)
    {
        UseFixedFunction(device, vertexFormat);
        SetRenderState(device, RenderState.ZEnable, 0);
        SetRenderState(device, RenderState.ZWriteEnable, 0);
        SetRenderState(device, RenderState.Lighting, 0);
        SetRenderState(device, RenderState.FogEnable, 0);
        SetRenderState(device, RenderState.AlphaTestEnable, 0);
        SetRenderState(device, RenderState.StencilEnable, 0);
        SetRenderState(device, RenderState.ScissorTestEnable, 0);
        SetRenderState(device, RenderState.CullMode, CullNone);
        SetRenderState(device, RenderState.ColorWriteEnable, 0xF);
        SetStageState(device, 1, StageState.ColorOp, OpDisable);
        SetStageState(device, 1, StageState.AlphaOp, OpDisable);
    }

    /// <summary>Draws a 2 px outline just inside the bounds in a solid colour, then puts every device state back.</summary>
    public static void DrawOutline(IntPtr device, Rectangle bounds, Color color)
    {
        var saved = SaveState(device);
        try
        {
            SetOverlayStates(device, FvfXyzRhw | FvfDiffuse);
            SetTexture(device, 0, IntPtr.Zero);
            SetRenderState(device, RenderState.AlphaBlendEnable, 0);
            SetStageState(device, 0, StageState.ColorOp, OpSelectArg1);
            SetStageState(device, 0, StageState.ColorArg1, ArgDiffuse);
            SetStageState(device, 0, StageState.AlphaOp, OpSelectArg1);
            SetStageState(device, 0, StageState.AlphaArg1, ArgDiffuse);

            var argb = (uint)color.ToArgb();
            var strip = stackalloc ColoredVertex[5];
            for (var inset = 0; inset < 2; inset++)
            {
                // Transformed vertices at whole coordinates sit on pixel centres in Direct3D 9.
                float left = bounds.Left + inset, top = bounds.Top + inset;
                float right = bounds.Right - 1 - inset, bottom = bounds.Bottom - 1 - inset;
                strip[0] = new(left, top, argb);
                strip[1] = new(right, top, argb);
                strip[2] = new(right, bottom, argb);
                strip[3] = new(left, bottom, argb);
                strip[4] = new(left, top, argb);
                DrawPrimitiveUP(device, LineStrip, 4, strip, sizeof(ColoredVertex));
            }
        }
        finally
        {
            RestoreState(saved);
        }
    }

    /// <summary>Draw one textured quad with premultiplied alpha, then put every device state back.</summary>
    private void Draw(int width, int height)
    {
        var saved = SaveState(_device);
        try
        {
            SetOverlayStates(_device, FvfXyzRhw | FvfTex1);
            SetTexture(_device, 0, _texture);
            SetTexture(_device, 1, IntPtr.Zero);
            SetRenderState(_device, RenderState.FillMode, FillSolid);
            SetRenderState(_device, RenderState.AlphaBlendEnable, 1);
            SetRenderState(_device, RenderState.SeparateAlphaBlendEnable, 0);
            SetRenderState(_device, RenderState.BlendOp, BlendOpAdd);
            SetRenderState(_device, RenderState.SrcBlend, BlendOne);
            SetRenderState(_device, RenderState.DestBlend, BlendInvSrcAlpha);

            SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
            SetStageState(_device, 0, StageState.ColorArg1, ArgTexture);
            SetStageState(_device, 0, StageState.AlphaOp, OpSelectArg1);
            SetStageState(_device, 0, StageState.AlphaArg1, ArgTexture);
            SetStageState(_device, 0, StageState.TexCoordIndex, 0);
            SetStageState(_device, 0, StageState.TextureTransformFlags, 0);

            SetSamplerState(_device, 0, SamplerState.MinFilter, FilterPoint);
            SetSamplerState(_device, 0, SamplerState.MagFilter, FilterPoint);
            SetSamplerState(_device, 0, SamplerState.MipFilter, FilterNone);
            SetSamplerState(_device, 0, SamplerState.AddressU, AddressClamp);
            SetSamplerState(_device, 0, SamplerState.AddressV, AddressClamp);

            // Pixel centres sit at half-pixel offsets in Direct3D 9, so shift by half a pixel for 1:1 texels.
            float left = Location.X - 0.5f, top = Location.Y - 0.5f;
            float right = left + width, bottom = top + height;
            // Four XYZRHW + TEX1 vertices, 24 bytes each.
            var quad = stackalloc float[]
            {
                left, top, 0, 1, 0, 0,
                right, top, 0, 1, 1, 0,
                left, bottom, 0, 1, 0, 1,
                right, bottom, 0, 1, 1, 1
            };
            DrawPrimitiveUP(_device, TriangleStrip, 2, quad, 24);
        }
        finally
        {
            RestoreState(saved);
        }
    }

    /// <summary>An XYZRHW | DIFFUSE vertex.</summary>
    private readonly struct ColoredVertex(float x, float y, uint color)
    {
        public readonly float X = x, Y = y, Z = 0, Rhw = 1;
        public readonly uint Color = color;
    }
}
