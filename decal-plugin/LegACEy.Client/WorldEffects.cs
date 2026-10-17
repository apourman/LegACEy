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
/// Effects drawn into retail's 3D world: the selection outline and the storm fog preview (<see cref="StormFog"/>).
/// The outline is a gold glow around the selected object, seen through walls. While retail draws a part, <c>CPhysicsPart::Draw</c> keeps the part in a
/// global; each of the device's indexed draws made for a part of the selected object is drawn again, flat white with no depth test, into
/// a mask. After retail's world (<c>SmartBox::Draw</c>), the mask is spread into a glow texture by drawing it several times offset, and the
/// glow is added to the screen in gold everywhere but the mask itself, which leaves a rim. Only the game thread draws.
/// </summary>
internal sealed unsafe class WorldEffects : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xAD0 near 0x50E000, +0xA0 near 0x455000) for names; our objdump read of installed acclient.exe";
    private const uint SmartBoxDrawAddress = 0x00455610, GetObjectAddress = 0x0050FF60, PartArrayDrawAddress = 0x00518E00;
    // CPhysicsPart* being drawn by CPhysicsPart::Draw, zero otherwise; part +0xE0 is its CPhysicsObj, whose +0x8 is the object id.
    private static readonly int* DrawingPart = (int*)0x008EE3D8;
    // The player's CPhysicsObj, kept by CPhysicsObj::SetPlayer.
    private static readonly int* PlayerObject = (int*)0x00844D68;
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
            Source, "the layout read here: part +0xE0 is the CPhysicsObj, object +0x8 its id"),
        new NativeUiEntry("CPhysicsObj::SetPlayer", 0x0050FF90, NativeUiCatalogue.Bytes("8B 44 24 04 A3 68 4D 84 00 C3"),
            Source, "Cdecl (CPhysicsObj*): the player global read here"),
        new NativeUiEntry("Position::get_offset", 0x0050AA4D, NativeUiCatalogue.Bytes("D9 44 24 14 D8 47 3C 8B 44 24 30 D9 44 24 18 83 C4 0C D8 47 40"),
            Source, "the layout read here: Position +0x4 is the cell id, +0x3C the origin; a part's draw position is part +0x78"),
        // Ends before the call into CObjectMaint, in case a hook patches it.
        new NativeUiEntry("CPhysicsObj::leave_cell clears the object's cell", 0x00511A77, NativeUiCatalogue.Bytes("C7 86 90 00 00 00 00 00 00 00 5E C2 04 00"),
            Source, "the layout read here: CPhysicsObj +0x90 is its CObjCell, zero once out of the world (picked up, gone)"),
        new NativeUiEntry("CPhysicsObj::GetObjectA", GetObjectAddress, NativeUiCatalogue.Bytes("8B 0D 64 4D 84 00 85 C9 74 0B 8B 44 24 04 50"),
            Source, "Cdecl (object id): CObjectMaint lookup, null when the object is not in the world"),
        new NativeUiEntry("CPartArray::Draw", PartArrayDrawAddress, NativeUiCatalogue.Bytes("56 57 8B F9 8B 47 58 33 F6 85 C0 76 1C 8D 49 00 8B 47 5C 8B 0C B0 85 C9 74 07 6A 00"),
            Source, "ThisCall (Position*, unused): CPhysicsPart::Draw(0) for each part; part count +0x58, parts +0x5C"),
        new NativeUiEntry("CPhysicsObj::SetLuminosity reads the part array", 0x00512374, NativeUiCatalogue.Bytes("8B 4E 10 85 C9 74 76"),
            Source, "the layout read here: CPhysicsObj +0x10 is its CPartArray")
    };

    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate void SmartBoxDrawFn(IntPtr smartBox);

    [Function(CallingConventions.Stdcall)]
    private delegate int DrawIndexedFn(IntPtr device, int type, int baseVertex, uint minIndex, uint vertices, uint startIndex, uint primitives);

    [Function(CallingConventions.Stdcall)]
    private delegate int ResetFn(IntPtr device, IntPtr parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetObjectFn(uint objectId);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void DrawPartsFn(IntPtr partArray, IntPtr position);

    private readonly GetObjectFn _getObject = Marshal.GetDelegateForFunctionPointer<GetObjectFn>(new IntPtr(GetObjectAddress));
    private readonly DrawPartsFn _drawParts = Marshal.GetDelegateForFunctionPointer<DrawPartsFn>(new IntPtr(PartArrayDrawAddress));
    // While the selection, culled this frame, is drawn into the mask only.
    private bool _forcing;

    private readonly IntPtr _device;
    private readonly Action<string> _log;
    private readonly IHook<SmartBoxDrawFn> _smartBoxHook;
    private readonly IHook<DrawIndexedFn> _drawHook;
    private readonly IHook<ResetFn> _resetHook;
    private readonly StormFog _fog;
    private GCHandle _root;
    private IntPtr _mask, _glow;
    private int _width, _height;
    private bool _masked, _failed, _firstLogged;
    private int _draws;
    // Timing for the log: time this frame spent in the effects' own work, the worst frame since the last summary, and the last world frame.
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _effectTicks, _worstEffectTicks, _lastWorld, _lastSummary, _lastHitchLog, _windowTicks, _windowFrames;

    private WorldEffects(IntPtr device, Action<string> log)
    {
        _device = device;
        _log = log;
        _fog = new StormFog(device, log);
        _root = GCHandle.Alloc(this);
        _smartBoxHook = ReloadedHooks.Instance.CreateHook<SmartBoxDrawFn>(OnSmartBoxDraw, SmartBoxDrawAddress);
        _drawHook = ReloadedHooks.Instance.CreateHook<DrawIndexedFn>(OnDrawIndexed, Method(device, DrawIndexedPrimitiveSlot).ToInt64());
        _resetHook = ReloadedHooks.Instance.CreateHook<ResetFn>(OnReset, Method(device, ResetSlot).ToInt64());
        _resetHook.Activate();
        _drawHook.Activate();
        _smartBoxHook.Activate();
    }

    /// <summary>The effects, or null when the running client does not match their native entries.</summary>
    public static WorldEffects? Create(IntPtr device, Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
        {
            log("The selection outline and storm fog are off: the running client does not match their native entries.");
            return null;
        }
        return new WorldEffects(device, log);
    }

    /// <summary>The object to outline, zero for none. Set each frame.</summary>
    public uint Selected { get; set; }

    /// <summary>Whether the storm fog draws; logged when it changes.</summary>
    public bool FogOn
    {
        get => _fogOn;
        set
        {
            if (value == _fogOn) return;
            var average = _windowFrames == 0 ? 0 : _windowTicks * 1000.0 / Stopwatch.Frequency / _windowFrames;
            _log($"World effects: storm fog {(value ? "on" : "off")}; with it {(_fogOn ? "on" : "off")}, world frames averaged {average:F1} ms over {_windowFrames}.");
            _windowTicks = _windowFrames = 0;
            _fogOn = value;
        }
    }

    private bool _fogOn = true;

    private int OnDrawIndexed(IntPtr device, int type, int baseVertex, uint minIndex, uint vertices, uint startIndex, uint primitives)
    {
        var original = _drawHook.OriginalFunction;
        if (_forcing)
        {
            // Not on screen: the selection is drawn here only so the outline shows through what culled it.
            try { if (DrawsSelected()) Timed(() => DrawIntoMask(() => original(device, type, baseVertex, minIndex, vertices, startIndex, primitives))); }
            catch (Exception exception) { Fail(exception); }
            return 0;
        }
        var result = original(device, type, baseVertex, minIndex, vertices, startIndex, primitives);
        var part = *DrawingPart;
        if (result < 0 || part == 0 || _failed || device != _device) return result;
        try
        {
            var player = *(int*)(part + 0xE0) == *PlayerObject;
            if (!_fog.Calibrated || (player && !_fog.FromPlayer)) Timed(() => _fog.Calibrate(part, player));
        }
        catch (Exception exception) { Fail(exception); }
        if (Selected == 0 || !DrawsSelected()) return result;
        try { Timed(() => DrawIntoMask(() => original(device, type, baseVertex, minIndex, vertices, startIndex, primitives))); }
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
        if (!_failed)
        {
            try
            {
                if (_fogOn) Timed(_fog.Draw);
                if (!_masked && Selected != 0) Timed(DrawCulledSelection);
            }
            catch (Exception exception) { Fail(exception); }
        }
        try
        {
            if (_masked && !_failed) Timed(Composite);
        }
        catch (Exception exception) { Fail(exception); }
        finally { _masked = false; _draws = 0; }
        LogTiming();
    }

    private void Timed(Action work)
    {
        var start = _clock.ElapsedTicks;
        try { work(); }
        finally { _effectTicks += _clock.ElapsedTicks - start; }
    }

    /// <summary>Logs a world frame over 50 ms with the effects' share of it, and every 30 s the effects' worst frame.</summary>
    private void LogTiming()
    {
        var now = _clock.ElapsedTicks;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        if (_effectTicks > _worstEffectTicks) _worstEffectTicks = _effectTicks;
        var frame = Ms(now - _lastWorld);
        if (_lastWorld != 0)
        {
            _windowTicks += now - _lastWorld;
            _windowFrames++;
        }
        if (_lastWorld != 0 && frame > 50 && Ms(now - _lastHitchLog) > 2000)
        {
            _lastHitchLog = now;
            _log($"World effects: a {frame:F0} ms world frame, {Ms(_effectTicks):F2} ms of it in the effects; storm strength {_fog.Strength:F2}, selection 0x{Selected:X8}.");
        }
        if (Ms(now - _lastSummary) > 30000)
        {
            _lastSummary = now;
            _log($"World effects: worst frame {Ms(_worstEffectTicks):F2} ms in the effects over the last 30 s.");
            _worstEffectTicks = 0;
        }
        _lastWorld = now;
        _effectTicks = 0;
    }

    /// <summary>
    /// Has retail draw the selected object's parts again, into the mask only: indoors, retail culls what is in rooms out of view.
    /// ponytail: a culled object's draw position is where retail last placed it; a moving one may lag until it is in view again.
    /// </summary>
    private void DrawCulledSelection()
    {
        var physicsObject = _getObject(Selected);
        // Out of the world (picked up into a pack, or gone): no cell, and its parts keep the place it was last drawn.
        if (physicsObject == IntPtr.Zero || Marshal.ReadIntPtr(physicsObject, 0x90) == IntPtr.Zero) return;
        var parts = Marshal.ReadIntPtr(physicsObject, 0x10);
        if (parts == IntPtr.Zero || Marshal.ReadInt32(parts, 0x58) == 0) return;
        _forcing = true;
        try { _drawParts(parts, IntPtr.Zero); }
        finally { _forcing = false; }
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
        try { _log($"World effects are off after an error: {exception}"); }
        catch { /* Reporting must never escape the native callback. */ }
    }

    public void Dispose()
    {
        // A hook that could not be disabled stays rooted, still calling back here.
        _smartBoxHook.Disable();
        _drawHook.Disable();
        _resetHook.Disable();
        ReleaseTextures();
        _fog.Dispose();
        if (_root.IsAllocated) _root.Free();
    }
}
