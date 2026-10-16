using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// A gold glow around the selected object, seen through walls. While retail draws a part, <c>CPhysicsPart::Draw</c> keeps the part in a
/// global; each of the device's indexed draws made for a part of the selected object is drawn again, flat white with no depth test, into
/// a mask. After retail's world (<c>SmartBox::Draw</c>), the mask is spread into a glow texture by drawing it several times offset, and the
/// glow is added to the screen in gold everywhere but the mask itself, which leaves a rim. Only the game thread draws.
/// </summary>
internal sealed unsafe class SelectionOutline : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xAD0 near 0x50E000, +0xA0 near 0x455000) for names; our objdump read of installed acclient.exe";
    private const uint SmartBoxDrawAddress = 0x00455610;
    // CPhysicsPart* being drawn by CPhysicsPart::Draw, zero otherwise; part +0xE0 is its CPhysicsObj, whose +0x8 is the object id.
    private static readonly int* DrawingPart = (int*)0x008EE3D8;
    private const uint Gold = 0xFFFFC040;
    // Glow taps: radius in pixels and brightness, eight directions each.
    private static readonly (float Radius, int Weight)[] Rings = { (2, 0x60), (4, 0x40), (6, 0x28) };

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("SmartBox::Draw", SmartBoxDrawAddress, NativeUiCatalogue.Bytes("8B 81 D8 00 00 00 85 C0 75 05 E9 A1 F6 FF FF C3"),
            Source, "ThisCall (): draws the world; hooked, the outline is added after it"),
        new NativeUiEntry("CPhysicsPart::Draw keeps the drawn part in 0x008EE3D8", 0x0050E30C,
            NativeUiCatalogue.Bytes("8B 0D 40 03 87 00 85 ED 0F 95 C2 89 35 D8 E3 8E 00 8B 01 52 8D 56 78 52 57 FF 50 70 83 E8 02 C7 05 D8 E3 8E 00 00 00 00 00"),
            Source, "the global read during the device's draws"),
        new NativeUiEntry("CPhysicsPart::get_physobj_id", 0x0050DF60, NativeUiCatalogue.Bytes("8B 81 E0 00 00 00 85 C0 74 04 8B 40 08 C3"),
            Source, "the layout read here: part +0xE0 is the CPhysicsObj, object +0x8 its id")
    };

    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate void SmartBoxDrawFn(IntPtr smartBox);

    [Function(CallingConventions.Stdcall)]
    private delegate int DrawIndexedFn(IntPtr device, int type, int baseVertex, uint minIndex, uint vertices, uint startIndex, uint primitives);

    [Function(CallingConventions.Stdcall)]
    private delegate int ResetFn(IntPtr device, IntPtr parameters);

    private readonly IntPtr _device;
    private readonly Action<string> _log;
    private readonly IHook<SmartBoxDrawFn> _smartBoxHook;
    private readonly IHook<DrawIndexedFn> _drawHook;
    private readonly IHook<ResetFn> _resetHook;
    private GCHandle _root;
    private IntPtr _mask, _glow;
    private int _width, _height;
    private bool _masked, _failed, _firstLogged, _missLogged;
    private int _draws, _missedFrames;

    private SelectionOutline(IntPtr device, Action<string> log)
    {
        _device = device;
        _log = log;
        _root = GCHandle.Alloc(this);
        _smartBoxHook = ReloadedHooks.Instance.CreateHook<SmartBoxDrawFn>(OnSmartBoxDraw, SmartBoxDrawAddress);
        _drawHook = ReloadedHooks.Instance.CreateHook<DrawIndexedFn>(OnDrawIndexed, Method(device, DrawIndexedPrimitiveSlot).ToInt64());
        _resetHook = ReloadedHooks.Instance.CreateHook<ResetFn>(OnReset, Method(device, ResetSlot).ToInt64());
        _resetHook.Activate();
        _drawHook.Activate();
        _smartBoxHook.Activate();
    }

    /// <summary>The outline, or null when the running client does not match its native entries.</summary>
    public static SelectionOutline? Create(IntPtr device, Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
        {
            log("The selection outline is off: the running client does not match its native entries.");
            return null;
        }
        return new SelectionOutline(device, log);
    }

    /// <summary>The object to outline, zero for none. Set each frame.</summary>
    public uint Selected { get; set; }

    private int OnDrawIndexed(IntPtr device, int type, int baseVertex, uint minIndex, uint vertices, uint startIndex, uint primitives)
    {
        var original = _drawHook.OriginalFunction;
        var result = original(device, type, baseVertex, minIndex, vertices, startIndex, primitives);
        if (result < 0 || Selected == 0 || _failed || device != _device || !DrawsSelected()) return result;
        try { DrawIntoMask(() => original(device, type, baseVertex, minIndex, vertices, startIndex, primitives)); }
        catch (Exception exception) { Fail(exception); }
        return result;
    }

    private bool DrawsSelected()
    {
        var part = *DrawingPart;
        if (part == 0) return false;
        var physicsObject = *(int*)(part + 0xE0);
        return physicsObject != 0 && *(uint*)(physicsObject + 0x8) == Selected;
    }

    /// <summary>Repeats one draw into the mask, flat white and through anything in front, then puts the device back.</summary>
    private void DrawIntoMask(Action draw)
    {
        var target = RenderTarget(_device);
        var depth = DepthStencil(_device);
        var saved = SaveState(_device);
        var viewport = Viewport(_device);
        try
        {
            EnsureTextures(target);
            var mask = SurfaceOf(_mask);
            try
            {
                SetRenderTarget(_device, mask);
                SetDepthStencil(_device, IntPtr.Zero);
                if (!_masked) ClearTargetTo(_device, 0);
                SetViewport(_device, viewport);
                SetRenderState(_device, RenderState.ZEnable, 0);
                SetRenderState(_device, RenderState.ZWriteEnable, 0);
                SetRenderState(_device, RenderState.AlphaBlendEnable, 0);
                SetRenderState(_device, RenderState.AlphaTestEnable, 0);
                SetRenderState(_device, RenderState.FogEnable, 0);
                SetRenderState(_device, RenderState.StencilEnable, 0);
                SetRenderState(_device, RenderState.ColorWriteEnable, 0xF);
                SetRenderState(_device, RenderState.TextureFactor, unchecked((int)0xFFFFFFFF));
                SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
                SetStageState(_device, 0, StageState.ColorArg1, ArgFactor);
                SetStageState(_device, 0, StageState.AlphaOp, OpSelectArg1);
                SetStageState(_device, 0, StageState.AlphaArg1, ArgFactor);
                SetStageState(_device, 1, StageState.ColorOp, OpDisable);
                SetStageState(_device, 1, StageState.AlphaOp, OpDisable);
                draw();
                _masked = true;
                _draws++;
            }
            finally { Release(mask); }
        }
        finally
        {
            SetRenderTarget(_device, target);
            SetDepthStencil(_device, depth);
            RestoreState(saved);
            Release(target);
            Release(depth);
        }
    }

    private void OnSmartBoxDraw(IntPtr smartBox)
    {
        _smartBoxHook.OriginalFunction(smartBox);
        if (!_masked && Selected != 0 && !_missLogged && ++_missedFrames == 300)
        {
            _missLogged = true;
            _log($"Selection outline: 300 world frames with 0x{Selected:X8} selected and none of its draws seen.");
        }
        if (!_masked || _failed) return;
        try { Composite(); }
        catch (Exception exception) { Fail(exception); }
        finally { _masked = false; _draws = 0; }
    }

    /// <summary>Spreads the mask into the glow texture, then adds the glow to the screen in gold outside the mask.</summary>
    private void Composite()
    {
        if (!_firstLogged)
        {
            _firstLogged = true;
            _log($"Selection outline: first frame drew {_draws} draws of 0x{Selected:X8} into the mask.");
        }
        var target = RenderTarget(_device);
        var saved = SaveState(_device);
        var glow = SurfaceOf(_glow);
        try
        {
            UseFixedFunction(_device, FvfXyzRhw | FvfTex1);
            SetRenderState(_device, RenderState.ZEnable, 0);
            SetRenderState(_device, RenderState.ZWriteEnable, 0);
            SetRenderState(_device, RenderState.Lighting, 0);
            SetRenderState(_device, RenderState.FogEnable, 0);
            SetRenderState(_device, RenderState.AlphaTestEnable, 0);
            SetRenderState(_device, RenderState.StencilEnable, 0);
            SetRenderState(_device, RenderState.ScissorTestEnable, 0);
            SetRenderState(_device, RenderState.CullMode, CullNone);
            SetRenderState(_device, RenderState.ColorWriteEnable, 0xF);
            SetRenderState(_device, RenderState.AlphaBlendEnable, 1);
            SetRenderState(_device, RenderState.SeparateAlphaBlendEnable, 0);
            SetRenderState(_device, RenderState.BlendOp, BlendOpAdd);
            SetRenderState(_device, RenderState.SrcBlend, BlendOne);
            SetRenderState(_device, RenderState.DestBlend, BlendOne);
            for (var stage = 0; stage < 2; stage++)
            {
                SetStageState(_device, stage, StageState.TexCoordIndex, 0);
                SetStageState(_device, stage, StageState.TextureTransformFlags, 0);
                SetSamplerState(_device, stage, SamplerState.MinFilter, FilterLinear);
                SetSamplerState(_device, stage, SamplerState.MagFilter, FilterLinear);
                SetSamplerState(_device, stage, SamplerState.MipFilter, FilterNone);
                SetSamplerState(_device, stage, SamplerState.AddressU, AddressClamp);
                SetSamplerState(_device, stage, SamplerState.AddressV, AddressClamp);
            }
            SetStageState(_device, 0, StageState.ColorOp, OpModulate);
            SetStageState(_device, 0, StageState.ColorArg1, ArgTexture);
            SetStageState(_device, 0, StageState.ColorArg2, ArgFactor);
            SetStageState(_device, 0, StageState.AlphaOp, OpSelectArg1);
            SetStageState(_device, 0, StageState.AlphaArg1, ArgFactor);

            // The spread: the mask added to the glow texture once per tap, offset and dimmed.
            SetRenderTarget(_device, glow);
            ClearTargetTo(_device, 0);
            SetTexture(_device, 0, _mask);
            SetTexture(_device, 1, IntPtr.Zero);
            SetStageState(_device, 1, StageState.ColorOp, OpDisable);
            SetStageState(_device, 1, StageState.AlphaOp, OpDisable);
            foreach (var (radius, weight) in Rings)
            {
                SetRenderState(_device, RenderState.TextureFactor, unchecked((int)(0xFF000000 | (uint)(weight * 0x010101))));
                for (var i = 0; i < 8; i++)
                {
                    var angle = i * MathF.PI / 4;
                    Quad(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
                }
            }

            // The rim: glow times gold, times one minus the mask, added to the screen.
            SetRenderTarget(_device, target);
            SetTexture(_device, 0, _glow);
            SetTexture(_device, 1, _mask);
            SetRenderState(_device, RenderState.TextureFactor, unchecked((int)Gold));
            SetStageState(_device, 1, StageState.ColorOp, OpModulate);
            SetStageState(_device, 1, StageState.ColorArg1, ArgTexture | ArgComplement);
            SetStageState(_device, 1, StageState.ColorArg2, ArgCurrent);
            SetStageState(_device, 1, StageState.AlphaOp, OpSelectArg1);
            SetStageState(_device, 1, StageState.AlphaArg1, ArgCurrent);
            SetStageState(_device, 2, StageState.ColorOp, OpDisable);
            SetStageState(_device, 2, StageState.AlphaOp, OpDisable);
            Quad(0, 0);
        }
        finally
        {
            Release(glow);
            SetRenderTarget(_device, target);
            RestoreState(saved);
            Release(target);
        }
    }

    /// <summary>One screen-sized textured quad, its texture shifted by (dx, dy) pixels.</summary>
    private void Quad(float dx, float dy)
    {
        float right = _width - 0.5f, bottom = _height - 0.5f;
        float u = dx / _width, v = dy / _height;
        var quad = stackalloc float[]
        {
            -0.5f, -0.5f, 0, 1, u, v,
            right, -0.5f, 0, 1, 1 + u, v,
            -0.5f, bottom, 0, 1, u, 1 + v,
            right, bottom, 0, 1, 1 + u, 1 + v
        };
        DrawPrimitiveUP(_device, TriangleStrip, 2, quad, 24);
    }

    /// <summary>Makes the mask and glow textures the size of the screen, remade when it changes.</summary>
    private void EnsureTextures(IntPtr target)
    {
        var desc = Describe(target);
        if (_mask != IntPtr.Zero && desc.Width == _width && desc.Height == _height) return;
        ReleaseTextures();
        _width = desc.Width;
        _height = desc.Height;
        _mask = CreateRenderTexture(_device, _width, _height);
        _glow = CreateRenderTexture(_device, _width, _height);
        _masked = false;
    }

    private void ReleaseTextures()
    {
        Release(_mask);
        Release(_glow);
        _mask = _glow = IntPtr.Zero;
        _masked = false;
    }

    // Default-pool textures must be gone before the device resets, or the reset fails.
    private int OnReset(IntPtr device, IntPtr parameters)
    {
        if (device == _device) ReleaseTextures();
        return _resetHook.OriginalFunction(device, parameters);
    }

    private void Fail(Exception exception)
    {
        _failed = true;
        try { _log($"The selection outline is off after an error: {exception}"); }
        catch { /* Reporting must never escape the native callback. */ }
    }

    public void Dispose()
    {
        // A hook that could not be disabled stays rooted, still calling back here.
        _smartBoxHook.Disable();
        _drawHook.Disable();
        _resetHook.Disable();
        ReleaseTextures();
        if (_root.IsAllocated) _root.Free();
    }
}
