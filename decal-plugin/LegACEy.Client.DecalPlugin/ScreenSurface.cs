using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using LegACEy.Client.PanelHost;
using Microsoft.DirectX;
using Microsoft.DirectX.Direct3D;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// An Avalonia panel drawn straight onto the game's Direct3D 9 device at a screen position,
/// with no window chrome around it.
/// </summary>
/// <remarks>
/// The texture lives in the managed pool, so Direct3D keeps a system-memory copy and restores it
/// after a device reset; there is nothing to recreate. It is drawn from Decal's RenderFrame, which
/// Retail surfaces are rendered from Decal's pre-UI frame callback; LegACEy windows prepare their
/// frame there and draw it later from the post-UI EndScene seam.
/// </remarks>
internal sealed class ScreenSurface : IDisposable
{
    private readonly Device _device;
    private Texture? _texture;
    private bool _uploaded;
    private int _textureWidth;
    private int _textureHeight;
    private readonly PanelFailureBudget _failureBudget = new();

    public ScreenSurface(Device device, AvaloniaPanel panel)
    {
        _device = device;
        Panel = panel;
    }

    public AvaloniaPanel Panel { get; }

    public Point Location { get; set; }

    public bool Visible { get; set; }

    public double LastTickMilliseconds { get; private set; }

    public double LastUploadMilliseconds { get; private set; }

    public int LastDirtyRectangleCount { get; private set; }

    public Rectangle Bounds => new(Location, new Size(Panel.Frame.Width, Panel.Frame.Height));

    /// <summary>Tick Avalonia, upload the frame if it changed, and draw it.</summary>
    public void Render()
    {
        if (!Visible)
        {
            Prepare();
            return;
        }

        Prepare();
        DrawNow();
    }

    /// <summary>Tick Avalonia and upload the frame without drawing it.</summary>
    public void Prepare()
    {
        if (!Visible)
        {
            Panel.DrainDispatcher();
            return;
        }
        var timer = Stopwatch.StartNew();
        var changed = Panel.Tick();
        timer.Stop();
        LastTickMilliseconds = timer.Elapsed.TotalMilliseconds;
        LastDirtyRectangleCount = Panel.Frame.DirtyRectangles.Count;
        if (Panel.LastError != null)
            return;
        if (_failureBudget.Record(timer.Elapsed))
            throw new TimeoutException($"Panel tick exceeded 50 ms for {_failureBudget.ConsecutiveSlowTicks} consecutive frames ({timer.Elapsed.TotalMilliseconds:F1} ms).");
        var frame = Panel.Frame;
        if (changed || !_uploaded || _texture == null)
            Upload(frame);
    }

    /// <summary>Draw the prepared frame at its current location.</summary>
    public void DrawNow()
    {
        if (!Visible || _texture == null)
            return;
        Draw(Panel.Frame.Width, Panel.Frame.Height);
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
        Panel.Dispose();
    }

    private void Upload(PanelFrame frame)
    {
        if (_texture != null && (_textureWidth != frame.Width || _textureHeight != frame.Height))
        {
            _texture.Dispose();
            _texture = null;
            _uploaded = false;
        }
        if (_texture == null)
        {
            _texture = new Texture(_device, frame.Width, frame.Height, 1, Usage.None, Format.A8R8G8B8, Pool.Managed);
            _textureWidth = frame.Width;
            _textureHeight = frame.Height;
        }

        var timer = Stopwatch.StartNew();
        try
        {
            var dirty = _uploaded ? frame.DirtyRectangles : new[] { new Rectangle(0, 0, frame.Width, frame.Height) };
            foreach (var rect in dirty)
            {
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                var bits = _texture.LockRectangle(0, rect, LockFlags.None, out var pitch);
                try
                {
                    for (var row = 0; row < rect.Height; row++)
                    {
                        var sourceOffset = ((rect.Y + row) * frame.Stride) + (rect.X * 4);
                        Marshal.Copy(frame.Pixels, sourceOffset, IntPtr.Add(bits.InternalData, row * pitch), rect.Width * 4);
                    }
                }
                finally
                {
                    _texture.UnlockRectangle(0);
                }
            }
        }
        finally
        {
            timer.Stop();
            LastUploadMilliseconds = timer.Elapsed.TotalMilliseconds;
        }
        _uploaded = true;
    }

    /// <summary>Draws a 2 px outline just inside the bounds in a solid colour, then puts every device state back.</summary>
    public static void DrawOutline(Device device, Rectangle bounds, Color color)
    {
        using var saved = new StateBlock(device, StateBlockType.All);
        saved.Capture();
        try
        {
            device.VertexShader = null;
            device.PixelShader = null;
            device.VertexFormat = CustomVertex.TransformedColored.Format;
            device.SetTexture(0, null);
            device.SetRenderState(RenderStates.ZEnable, false);
            device.SetRenderState(RenderStates.ZBufferWriteEnable, false);
            device.SetRenderState(RenderStates.Lighting, false);
            device.SetRenderState(RenderStates.FogEnable, false);
            device.SetRenderState(RenderStates.AlphaTestEnable, false);
            device.SetRenderState(RenderStates.AlphaBlendEnable, false);
            device.SetRenderState(RenderStates.StencilEnable, false);
            device.SetRenderState(RenderStates.ScissorTestEnable, false);
            device.SetRenderState(RenderStates.CullMode, (int)Cull.None);
            device.SetRenderState(RenderStates.ColorWriteEnable, 0xF);
            device.SetTextureStageState(0, TextureStageStates.ColorOperation, (int)TextureOperation.SelectArg1);
            device.SetTextureStageState(0, TextureStageStates.ColorArgument1, (int)TextureArgument.Diffuse);
            device.SetTextureStageState(0, TextureStageStates.AlphaOperation, (int)TextureOperation.SelectArg1);
            device.SetTextureStageState(0, TextureStageStates.AlphaArgument1, (int)TextureArgument.Diffuse);
            device.SetTextureStageState(1, TextureStageStates.ColorOperation, (int)TextureOperation.Disable);
            device.SetTextureStageState(1, TextureStageStates.AlphaOperation, (int)TextureOperation.Disable);

            var argb = color.ToArgb();
            for (var inset = 0; inset < 2; inset++)
            {
                // Transformed vertices at whole coordinates sit on pixel centres in Direct3D 9.
                float left = bounds.Left + inset, top = bounds.Top + inset;
                float right = bounds.Right - 1 - inset, bottom = bounds.Bottom - 1 - inset;
                var strip = new[]
                {
                    new CustomVertex.TransformedColored(left, top, 0, 1, argb),
                    new CustomVertex.TransformedColored(right, top, 0, 1, argb),
                    new CustomVertex.TransformedColored(right, bottom, 0, 1, argb),
                    new CustomVertex.TransformedColored(left, bottom, 0, 1, argb),
                    new CustomVertex.TransformedColored(left, top, 0, 1, argb)
                };
                device.DrawUserPrimitives(PrimitiveType.LineStrip, 4, strip);
            }
        }
        finally
        {
            saved.Apply();
        }
    }

    /// <summary>Draw one textured quad with premultiplied alpha, then put every device state back.</summary>
    private void Draw(int width, int height)
    {
        using var saved = new StateBlock(_device, StateBlockType.All);
        saved.Capture();
        try
        {
            _device.VertexShader = null;
            _device.PixelShader = null;
            _device.VertexFormat = CustomVertex.TransformedTextured.Format;
            _device.SetTexture(0, _texture);
            _device.SetTexture(1, null);

            _device.SetRenderState(RenderStates.ZEnable, false);
            _device.SetRenderState(RenderStates.ZBufferWriteEnable, false);
            _device.SetRenderState(RenderStates.Lighting, false);
            _device.SetRenderState(RenderStates.FogEnable, false);
            _device.SetRenderState(RenderStates.AlphaTestEnable, false);
            _device.SetRenderState(RenderStates.StencilEnable, false);
            _device.SetRenderState(RenderStates.ScissorTestEnable, false);
            _device.SetRenderState(RenderStates.CullMode, (int)Cull.None);
            _device.SetRenderState(RenderStates.FillMode, (int)FillMode.Solid);
            _device.SetRenderState(RenderStates.ColorWriteEnable, 0xF);
            _device.SetRenderState(RenderStates.AlphaBlendEnable, true);
            _device.SetRenderState(RenderStates.SeparateAlphaBlendEnable, false);
            _device.SetRenderState(RenderStates.BlendOperation, (int)BlendOperation.Add);
            _device.SetRenderState(RenderStates.SourceBlend, (int)Blend.One);
            _device.SetRenderState(RenderStates.DestinationBlend, (int)Blend.InvSourceAlpha);

            _device.SetTextureStageState(0, TextureStageStates.ColorOperation, (int)TextureOperation.SelectArg1);
            _device.SetTextureStageState(0, TextureStageStates.ColorArgument1, (int)TextureArgument.TextureColor);
            _device.SetTextureStageState(0, TextureStageStates.AlphaOperation, (int)TextureOperation.SelectArg1);
            _device.SetTextureStageState(0, TextureStageStates.AlphaArgument1, (int)TextureArgument.TextureColor);
            _device.SetTextureStageState(0, TextureStageStates.TextureCoordinateIndex, 0);
            _device.SetTextureStageState(0, TextureStageStates.TextureTransform, (int)TextureTransform.Disable);
            _device.SetTextureStageState(1, TextureStageStates.ColorOperation, (int)TextureOperation.Disable);
            _device.SetTextureStageState(1, TextureStageStates.AlphaOperation, (int)TextureOperation.Disable);

            _device.SetSamplerState(0, SamplerStageStates.MinFilter, (int)TextureFilter.Point);
            _device.SetSamplerState(0, SamplerStageStates.MagFilter, (int)TextureFilter.Point);
            _device.SetSamplerState(0, SamplerStageStates.MipFilter, (int)TextureFilter.None);
            _device.SetSamplerState(0, SamplerStageStates.AddressU, (int)TextureAddress.Clamp);
            _device.SetSamplerState(0, SamplerStageStates.AddressV, (int)TextureAddress.Clamp);

            // Pixel centres sit at half-pixel offsets in Direct3D 9, so shift by half a pixel for 1:1 texels.
            float left = Location.X - 0.5f, top = Location.Y - 0.5f;
            float right = left + width, bottom = top + height;
            var quad = new[]
            {
                new CustomVertex.TransformedTextured(left, top, 0, 1, 0, 0),
                new CustomVertex.TransformedTextured(right, top, 0, 1, 1, 0),
                new CustomVertex.TransformedTextured(left, bottom, 0, 1, 0, 1),
                new CustomVertex.TransformedTextured(right, bottom, 0, 1, 1, 1)
            };
            _device.DrawUserPrimitives(PrimitiveType.TriangleStrip, 2, quad);
        }
        finally
        {
            saved.Apply();
        }
    }
}
