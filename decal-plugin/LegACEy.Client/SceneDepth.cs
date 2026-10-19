using System;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's world drawn into a depth buffer the effects can read: at the start of each <c>SmartBox::Draw</c> retail's depth-stencil surface is
/// swapped for one of an INTZ texture (a driver format every D3D9 GPU since about 2008 has) of the screen's size, cleared, and left bound for
/// the rest of the frame, so retail's own later drawing sees the same depth as before. Off, with retail's buffer kept, when the driver has
/// no INTZ or the screen is multisampled. About 8 MB at 1920 × 1080.
/// </summary>
internal sealed class SceneDepth : IDisposable
{
    private const int Intz = 0x5A544E49; // MAKEFOURCC('I','N','T','Z')
    private readonly IntPtr _device;
    private readonly Action<string> _log;
    private IntPtr _texture, _surface, _retail;
    private int _width, _height;

    /// <summary>The depth texture's size, the screen's.</summary>
    public int Width => _width;

    public int Height => _height;
    private bool _off;

    public SceneDepth(IntPtr device, Action<string> log)
    {
        _device = device;
        _log = log;
    }

    /// <summary>The depth texture holding retail's world this frame, or zero when it is not in use.</summary>
    public IntPtr Texture { get; private set; }

    /// <summary>Called before retail draws its world: puts the readable depth buffer in place of retail's.</summary>
    public void BeginWorld()
    {
        Texture = IntPtr.Zero;
        if (_off) return;
        try
        {
            var target = RenderTarget(_device);
            var desc = Describe(target);
            D3D9.Release(target);
            if (desc.MultiSampleType != 0)
            {
                Off("the screen is antialiased (multisampled)");
                return;
            }
            if (_texture == IntPtr.Zero || desc.Width != _width || desc.Height != _height)
            {
                Release();
                _width = desc.Width;
                _height = desc.Height;
                _texture = CreateDepthTexture(_device, _width, _height, Intz);
                _surface = SurfaceOf(_texture);
            }
            var current = DepthStencil(_device);
            if (current == IntPtr.Zero) return; // retail draws without depth: nothing to stand in for
            if (current == _surface) D3D9.Release(current);
            else
            {
                D3D9.Release(_retail);
                _retail = current;
            }
            SetDepthStencil(_device, _surface);
            ClearDepthAndStencil(_device);
            Texture = _texture;
        }
        catch (Exception exception)
        {
            Off(exception.Message);
        }
    }

    /// <summary>Runs <paramref name="read"/> with retail's own buffer bound, so the depth texture can be sampled without also being the target.</summary>
    public void Reading(Action read)
    {
        SetDepthStencil(_device, _retail);
        try { read(); }
        finally { SetDepthStencil(_device, _surface); }
    }

    private void Off(string why)
    {
        _off = true;
        Release();
        _log($"Scene depth is off, so the storm fog is drawn in slices: {why}.");
    }

    /// <summary>Gives retail its own depth buffer back and frees the texture; needed before the device resets.</summary>
    public void Release()
    {
        Texture = IntPtr.Zero;
        if (_retail != IntPtr.Zero)
        {
            var current = DepthStencil(_device);
            try
            {
                if (current == _surface) SetDepthStencil(_device, _retail);
            }
            finally { D3D9.Release(current); }
        }
        D3D9.Release(_retail);
        D3D9.Release(_surface);
        D3D9.Release(_texture);
        _retail = _surface = _texture = IntPtr.Zero;
    }

    public void Dispose() => Release();
}
