using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The few Direct3D 9 COM calls LegACEy draws with, made on the game's own objects through their vtables. A failed HRESULT throws a
/// <see cref="COMException"/> (Managed DirectX threw its own Direct3DException types there), which the callers' catch-all failure
/// paths turn into switching the UI off, as before.
/// </summary>
internal static unsafe class D3D9
{
    // Vtable slots, checked against the method order in mingw-w64 d3d9.h. IUnknown comes first in every interface:
    // QueryInterface 0, AddRef 1, Release 2.
    private const int ReleaseSlot = 2;
    // IDirect3DDevice9
    public const int ResetSlot = 16;
    private const int CreateTextureSlot = 23;
    private const int SetRenderTargetSlot = 37;
    private const int GetRenderTargetSlot = 38;
    private const int SetDepthStencilSurfaceSlot = 39;
    private const int GetDepthStencilSurfaceSlot = 40;
    private const int ClearSlot = 43;
    private const int SetTransformSlot = 44;
    private const int GetTransformSlot = 45;
    private const int SetViewportSlot = 47;
    private const int GetViewportSlot = 48;
    private const int SetMaterialSlot = 49;
    private const int SetLightSlot = 51;
    private const int LightEnableSlot = 53;
    private const int SetRenderStateSlot = 57;
    private const int GetRenderStateSlot = 58;
    private const int CreateStateBlockSlot = 59;
    private const int SetTextureSlot = 65;
    private const int SetTextureStageStateSlot = 67;
    private const int SetSamplerStateSlot = 69;
    public const int DrawPrimitiveSlot = 81;
    public const int DrawIndexedPrimitiveSlot = 82;
    private const int DrawPrimitiveUPSlot = 83;
    private const int SetFVFSlot = 89;
    private const int CreateVertexShaderSlot = 91;
    private const int SetVertexShaderSlot = 92;
    private const int SetVertexShaderConstantFSlot = 94;
    private const int CreatePixelShaderSlot = 106;
    private const int SetPixelShaderSlot = 107;
    private const int SetPixelShaderConstantFSlot = 109;
    // ID3DBlob (after IUnknown)
    private const int GetBufferPointerSlot = 3, GetBufferSizeSlot = 4;
    // IDirect3DSurface9 (after IDirect3DResource9 3-10 and GetContainer 11)
    private const int GetDescSlot = 12;
    // IDirect3DTexture9 (after IDirect3DResource9 3-10 and IDirect3DBaseTexture9 11-16)
    private const int GetSurfaceLevelSlot = 18;
    private const int LockRectSlot = 19;
    private const int UnlockRectSlot = 20;
    // IDirect3DStateBlock9 (GetDevice 3, Capture 4)
    private const int ApplySlot = 5;

    // Values from mingw-w64 d3d9types.h.
    public const uint FvfXyz = 0x002, FvfXyzRhw = 0x004, FvfNormal = 0x010, FvfDiffuse = 0x040, FvfTex1 = 0x100, FvfTex2 = 0x200, FvfTex3 = 0x300;
    public const int LineStrip = 3, TriangleList = 4, TriangleStrip = 5;
    public const int TransformView = 2, TransformProjection = 3, TransformWorld = 256;
    private const int StateBlockAll = 1, FormatA8R8G8B8 = 21, PoolDefault = 0, PoolManaged = 1, UsageRenderTarget = 1, ClearTarget = 1, ClearZBuffer = 2;

    public enum RenderState
    {
        ZEnable = 7, FillMode = 8, ShadeMode = 9, ZWriteEnable = 14, AlphaTestEnable = 15, SrcBlend = 19, DestBlend = 20,
        CullMode = 22, ZFunc = 23, AlphaRef = 24, AlphaFunc = 25, AlphaBlendEnable = 27, FogEnable = 28, SpecularEnable = 29, FogColor = 34, FogTableMode = 35, FogDensity = 38, RangeFogEnable = 48,
        StencilEnable = 52, TextureFactor = 60, Lighting = 137, Ambient = 139, FogVertexMode = 140, ColorVertex = 141, NormalizeNormals = 143, ColorWriteEnable = 168,
        BlendOp = 171, ScissorTestEnable = 174, SeparateAlphaBlendEnable = 206
    }

    public enum StageState { ColorOp = 1, ColorArg1 = 2, ColorArg2 = 3, AlphaOp = 4, AlphaArg1 = 5, AlphaArg2 = 6, TexCoordIndex = 11, TextureTransformFlags = 24 }

    public enum SamplerState { AddressU = 1, AddressV = 2, MagFilter = 5, MinFilter = 6, MipFilter = 7 }

    // D3DTOP, D3DTA, D3DTEXF, D3DTADDRESS, D3DCMP, D3DCULL, D3DFILL, D3DSHADE, D3DBLEND and D3DBLENDOP values.
    public const int OpDisable = 1, OpSelectArg1 = 2, OpModulate = 4, OpModulate2x = 5, OpAddSigned = 8, OpBlendDiffuseAlpha = 12, ArgDiffuse = 0, ArgCurrent = 1, ArgTexture = 2, ArgFactor = 3, ArgComplement = 0x10;
    public const int FilterNone = 0, FilterPoint = 1, FilterLinear = 2, AddressWrap = 1, AddressClamp = 3;
    public const int CmpLessEqual = 4, CmpGreaterEqual = 7, CullNone = 1, FillSolid = 3, ShadeGouraud = 2;
    public const int BlendOne = 2, BlendSrcColor = 3, BlendSrcAlpha = 5, BlendInvSrcAlpha = 6, BlendDestColor = 9, BlendOpAdd = 1;

    /// <summary>D3DSURFACE_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SurfaceDesc
    {
        public int Format, Type, Usage, Pool, MultiSampleType, MultiSampleQuality, Width, Height;
    }

    /// <summary>D3DLIGHT9.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Light
    {
        public int Type;
        public Vector4 Diffuse, Specular, Ambient;
        public Vector3 Position, Direction;
        public float Range, Falloff, Attenuation0, Attenuation1, Attenuation2, Theta, Phi;
    }

    /// <summary>D3DMATERIAL9.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Material
    {
        public Vector4 Diffuse, Ambient, Specular, Emissive;
        public float Power;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Viewport9
    {
        public int X, Y, Width, Height;
        public float MinZ, MaxZ;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LockedRect
    {
        public int Pitch;
        public byte* Bits;
    }

    private static void** Vt(IntPtr com) => *(void***)com;

    private static void Check(int result, string method)
    {
        if (result < 0) throw new COMException($"Direct3D 9 {method} failed: 0x{result:X8}.", result);
    }

    /// <summary>Releases a COM object this code created, once.</summary>
    public static void Release(IntPtr com)
    {
        if (com != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vt(com)[ReleaseSlot])(com);
    }

    /// <summary>Records every device state; <see cref="RestoreState"/> puts them back and frees the record.</summary>
    public static IntPtr SaveState(IntPtr device)
    {
        IntPtr block;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Vt(device)[CreateStateBlockSlot])(device, StateBlockAll, &block), "CreateStateBlock");
        return block;
    }

    public static void RestoreState(IntPtr block)
    {
        try { Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(block)[ApplySlot])(block), "StateBlock.Apply"); }
        finally { Release(block); }
    }

    /// <summary>An A8R8G8B8 texture in the managed pool, which Direct3D restores itself after a device reset; <paramref name="levels"/> mip levels.</summary>
    public static IntPtr CreateTexture(IntPtr device, int width, int height, int levels = 1)
    {
        IntPtr texture;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, int, int, int, IntPtr*, IntPtr, int>)Vt(device)[CreateTextureSlot])(
            device, width, height, levels, 0, FormatA8R8G8B8, PoolManaged, &texture, IntPtr.Zero), "CreateTexture");
        return texture;
    }

    /// <summary>Copies a rectangle of 32-bit pixels, at the same place in both, into one of the texture's levels (the top by default).</summary>
    public static void Upload(IntPtr texture, Rectangle area, byte[] pixels, int stride, int level = 0)
    {
        var rect = stackalloc int[] { area.Left, area.Top, area.Right, area.Bottom };
        LockedRect locked;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, LockedRect*, int*, int, int>)Vt(texture)[LockRectSlot])(texture, level, &locked, rect, 0), "LockRect");
        var unlock = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vt(texture)[UnlockRectSlot];
        try
        {
            var rowBytes = area.Width * 4;
            for (var row = 0; row < area.Height; row++)
                pixels.AsSpan((area.Y + row) * stride + area.X * 4, rowBytes).CopyTo(new Span<byte>(locked.Bits + row * locked.Pitch, rowBytes));
        }
        catch
        {
            unlock(texture, level); // the copy's exception is the one worth reporting
            throw;
        }
        Check(unlock(texture, level), "UnlockRect");
    }

    /// <summary>An A8R8G8B8 render-target texture in the default pool: it must be released before the device resets.</summary>
    public static IntPtr CreateRenderTexture(IntPtr device, int width, int height)
    {
        IntPtr texture;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, int, int, int, IntPtr*, IntPtr, int>)Vt(device)[CreateTextureSlot])(
            device, width, height, 1, UsageRenderTarget, FormatA8R8G8B8, PoolDefault, &texture, IntPtr.Zero), "CreateTexture");
        return texture;
    }

    /// <summary>The texture's top level, with a reference the caller releases.</summary>
    public static IntPtr SurfaceOf(IntPtr texture)
    {
        IntPtr surface;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Vt(texture)[GetSurfaceLevelSlot])(texture, 0, &surface), "GetSurfaceLevel");
        return surface;
    }

    public static SurfaceDesc Describe(IntPtr surface)
    {
        SurfaceDesc desc;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, SurfaceDesc*, int>)Vt(surface)[GetDescSlot])(surface, &desc), "GetDesc");
        return desc;
    }

    /// <summary>Render target 0, with a reference the caller releases.</summary>
    public static IntPtr RenderTarget(IntPtr device)
    {
        IntPtr surface;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Vt(device)[GetRenderTargetSlot])(device, 0, &surface), "GetRenderTarget");
        return surface;
    }

    /// <summary>Sets render target 0, which also resets the viewport to the whole target.</summary>
    public static void SetRenderTarget(IntPtr device, IntPtr surface) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)Vt(device)[SetRenderTargetSlot])(device, 0, surface), "SetRenderTarget");

    /// <summary>The depth-stencil surface, with a reference the caller releases; zero when none is set.</summary>
    public static IntPtr DepthStencil(IntPtr device)
    {
        // D3DERR_NOTFOUND when none is set; the zero it leaves is the answer.
        var surface = IntPtr.Zero;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vt(device)[GetDepthStencilSurfaceSlot])(device, &surface);
        return surface;
    }

    public static void SetDepthStencil(IntPtr device, IntPtr surface) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vt(device)[SetDepthStencilSurfaceSlot])(device, surface), "SetDepthStencilSurface");

    /// <summary>Clears the whole render target to one colour.</summary>
    public static void ClearTargetTo(IntPtr device, uint argb) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int*, int, uint, float, int, int>)Vt(device)[ClearSlot])(device, 0, null, ClearTarget, argb, 1, 0), "Clear");

    /// <summary>The address of a device method, for hooking it.</summary>
    public static IntPtr Method(IntPtr device, int slot) => (IntPtr)Vt(device)[slot];

    public static Rectangle Viewport(IntPtr device)
    {
        Viewport9 viewport;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Viewport9*, int>)Vt(device)[GetViewportSlot])(device, &viewport), "GetViewport");
        return new Rectangle(viewport.X, viewport.Y, viewport.Width, viewport.Height);
    }

    public static void SetViewport(IntPtr device, Rectangle area)
    {
        var viewport = new Viewport9 { X = area.X, Y = area.Y, Width = area.Width, Height = area.Height, MinZ = 0, MaxZ = 1 };
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Viewport9*, int>)Vt(device)[SetViewportSlot])(device, &viewport), "SetViewport");
    }

    /// <summary>Clears the depth buffer inside one screen rectangle.</summary>
    public static void ClearDepth(IntPtr device, Rectangle area, float z)
    {
        var rect = stackalloc int[] { area.Left, area.Top, area.Right, area.Bottom };
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int*, int, int, float, int, int>)Vt(device)[ClearSlot])(device, 1, rect, ClearZBuffer, 0, z, 0), "Clear");
    }

    // D3DMATRIX is row-major with row vectors, the same layout and convention as System.Numerics.Matrix4x4.
    public static void SetTransform(IntPtr device, int state, Matrix4x4 matrix) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, Matrix4x4*, int>)Vt(device)[SetTransformSlot])(device, state, &matrix), "SetTransform");

    public static Matrix4x4 GetTransform(IntPtr device, int state)
    {
        Matrix4x4 matrix;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, Matrix4x4*, int>)Vt(device)[GetTransformSlot])(device, state, &matrix), "GetTransform");
        return matrix;
    }

    public static void SetMaterial(IntPtr device, Material material) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Material*, int>)Vt(device)[SetMaterialSlot])(device, &material), "SetMaterial");

    public static void SetLight(IntPtr device, int index, Light light) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, Light*, int>)Vt(device)[SetLightSlot])(device, index, &light), "SetLight");

    public static void LightEnable(IntPtr device, int index, bool enable) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)Vt(device)[LightEnableSlot])(device, index, enable ? 1 : 0), "LightEnable");

    public static void SetRenderState(IntPtr device, RenderState state, int value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, RenderState, int, int>)Vt(device)[SetRenderStateSlot])(device, state, value), "SetRenderState");

    public static int GetRenderState(IntPtr device, RenderState state)
    {
        int value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, RenderState, int*, int>)Vt(device)[GetRenderStateSlot])(device, state, &value), "GetRenderState");
        return value;
    }

    public static void SetTexture(IntPtr device, int stage, IntPtr texture) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)Vt(device)[SetTextureSlot])(device, stage, texture), "SetTexture");

    public static void SetStageState(IntPtr device, int stage, StageState state, int value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, StageState, int, int>)Vt(device)[SetTextureStageStateSlot])(device, stage, state, value), "SetTextureStageState");

    public static void SetSamplerState(IntPtr device, int sampler, SamplerState state, int value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, SamplerState, int, int>)Vt(device)[SetSamplerStateSlot])(device, sampler, state, value), "SetSamplerState");

    /// <summary>Fixed-function drawing: no shaders, and the given flexible vertex format.</summary>
    public static void UseFixedFunction(IntPtr device, uint vertexFormat)
    {
        var vt = Vt(device);
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[SetVertexShaderSlot])(device, IntPtr.Zero), "SetVertexShader");
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[SetPixelShaderSlot])(device, IntPtr.Zero), "SetPixelShader");
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vt[SetFVFSlot])(device, vertexFormat), "SetFVF");
    }

    /// <summary>Draws with these shaders, fed the given flexible vertex format.</summary>
    public static void UseShaders(IntPtr device, IntPtr vertexShader, IntPtr pixelShader, uint vertexFormat)
    {
        var vt = Vt(device);
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[SetVertexShaderSlot])(device, vertexShader), "SetVertexShader");
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[SetPixelShaderSlot])(device, pixelShader), "SetPixelShader");
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vt[SetFVFSlot])(device, vertexFormat), "SetFVF");
    }

    public static void SetVertexConstants(IntPtr device, int register, ReadOnlySpan<Vector4> values)
    {
        fixed (Vector4* data = values)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, Vector4*, int, int>)Vt(device)[SetVertexShaderConstantFSlot])(device, register, data, values.Length), "SetVertexShaderConstantF");
    }

    public static void SetPixelConstants(IntPtr device, int register, ReadOnlySpan<Vector4> values)
    {
        fixed (Vector4* data = values)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, Vector4*, int, int>)Vt(device)[SetPixelShaderConstantFSlot])(device, register, data, values.Length), "SetPixelShaderConstantF");
    }

    /// <summary>
    /// Compiles one entry point of HLSL source with Windows' own d3dcompiler_47 and creates the shader: a vertex shader for a vs_ target, a
    /// pixel shader otherwise. Throws with the compiler's message when it fails.
    /// </summary>
    public static IntPtr CompileShader(IntPtr device, string source, string entry, string target)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(source);
        var result = D3DCompile(bytes, bytes.Length, entry, IntPtr.Zero, IntPtr.Zero, entry, target, 0, 0, out var code, out var errors);
        try
        {
            if (result < 0)
            {
                var message = errors == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(BlobPointer(errors), (int)BlobSize(errors));
                throw new COMException($"Compiling {entry} ({target}) failed: 0x{result:X8} {message}", result);
            }
            IntPtr shader;
            var slot = target.StartsWith("vs_", StringComparison.Ordinal) ? CreateVertexShaderSlot : CreatePixelShaderSlot;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vt(device)[slot])(device, BlobPointer(code), &shader), "CreateShader");
            return shader;
        }
        finally
        {
            Release(code);
            Release(errors);
        }
    }

    private static IntPtr BlobPointer(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Vt(blob)[GetBufferPointerSlot])(blob);

    private static nint BlobSize(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, nint>)Vt(blob)[GetBufferSizeSlot])(blob);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int D3DCompile(byte[] source, nint size, string name, IntPtr defines, IntPtr include, string entry, string target,
        uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    public static void DrawPrimitiveUP(IntPtr device, int type, int count, void* vertices, int stride) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, void*, int, int>)Vt(device)[DrawPrimitiveUPSlot])(device, type, count, vertices, stride), "DrawPrimitiveUP");
}
