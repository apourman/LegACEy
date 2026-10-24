using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Effects drawn into retail's 3D world: the selection outline, livelier water (<see cref="Water"/>), the storm fog preview (<see cref="StormFog"/>)
/// and radar pings (<see cref="PingBeams"/>).
/// The outline is a gold glow around the selected object, seen through walls. While retail draws a part, <c>CPhysicsPart::Draw</c> keeps the part in a
/// global; each of the device's indexed draws made for a part of the selected object is drawn again, flat white with no depth test, into
/// a mask. After retail's world (<c>SmartBox::Draw</c>), the mask is spread into a glow texture by drawing it several times offset, and the
/// glow is added to the screen in gold everywhere but the mask itself, which leaves a rim. The player's own parts go into the mask's alpha,
/// and no rim is drawn over them: the player stands in front of what they select. Only the game thread draws.
/// </summary>
internal sealed unsafe class WorldEffects : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xAD0 near 0x50E000, +0xA0 near 0x455000) for names; our objdump read of installed acclient.exe";
    private const uint SmartBoxDrawAddress = 0x00455610, GetObjectAddress = 0x0050FF60, PartDrawAddress = 0x0050E270;
    private const uint SphereTestAddress = 0x0054CE60, UpdatePartsAddress = 0x00510140;
    private const uint LandscapeDrawAddress = 0x00506D90;
    // CPhysicsPart* being drawn by CPhysicsPart::Draw, zero otherwise; part +0xE0 is its CPhysicsObj, whose +0x8 is the object id.
    private static readonly int* DrawingPart = (int*)0x008EE3D8;
    // The view retail draws through: the portal views RenderDeviceD3D::DrawMesh loops over (0x867330, per room), then the current view and
    // the clip planes its polygons are cut to, as set by the view setter (0x54DCF0). After the world they are the last room's.
    private const uint FullViewAddress = 0x0054FB60;
    private static readonly int*[] ViewGlobals =
    {
        (int*)0x00867330, (int*)0x008672EC, (int*)0x00847054, (int*)0x00847060, (int*)0x008671C8,
        (int*)0x00847050, (int*)0x00867304, (int*)0x008470D0, (int*)0x008470CC, (int*)0x00867300
    };
    private readonly int[] _savedView = new int[ViewGlobals.Length];
    // The player's CPhysicsObj, kept by CPhysicsObj::SetPlayer.
    private static readonly int* PlayerObject = (int*)0x00844D68;
    private const uint Gold = 0xFFFFC040;
    // ColorWriteEnable: the selection into the mask's colour, the player into its alpha.
    private const int SelectionChannels = 0x7, PlayerChannels = 0x8;
    private const int ArgAlphaReplicate = 0x20;
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
        new NativeUiEntry("gmRadarUI::DrawObjects reads an object's cell id", 0x004DA0E4, NativeUiCatalogue.Bytes("8B 88 90 00 00 00 85 C9 0F 84 03 02 00 00 8B 48 4C 85 C9"),
            Source, "the layout read here: CPhysicsObj +0x4C is its cell id: the player's, for the storm and weather (outdoors, a building's room, a dungeon)"),
        new NativeUiEntry("CPhysicsObj::GetObjectA", GetObjectAddress, NativeUiCatalogue.Bytes("8B 0D 64 4D 84 00 85 C9 74 0B 8B 44 24 04 50"),
            Source, "Cdecl (object id): CObjectMaint lookup, null when the object is not in the world"),
        new NativeUiEntry("CPartArray::Draw", 0x00518E00, NativeUiCatalogue.Bytes("56 57 8B F9 8B 47 58 33 F6 85 C0 76 1C 8D 49 00 8B 47 5C 8B 0C B0 85 C9 74 07 6A 00"),
            Source, "the layout read here: CPartArray part count +0x58, parts +0x5C"),
        // Past the prologue, in case a hook patches it.
        new NativeUiEntry("CPhysicsPart::Draw reads its force flag", PartDrawAddress + 0x13, NativeUiCatalogue.Bytes("55 8B 6C 24 0C 85 ED 57 75 12"),
            Source, "ThisCall (int force): unforced, it skips a part already drawn this frame; forced, RenderDeviceD3D::DrawMesh (0x5A1970) queues it through the paperdoll's BSP path, which never reaches the device draws hooked here"),
        new NativeUiEntry("CPhysicsObj::UpdatePartsInternal", UpdatePartsAddress,
            NativeUiCatalogue.Bytes("8B 81 A8 00 00 00 F6 C4 10 75 12 8B 41 10 85 C0 74 0B 83 C1 50 51 8B C8 E8"),
            Source, "ThisCall (): CPartArray::SetFrame (0x519E40) with the object's frame (+0x50) places its parts where it is now"),
        new NativeUiEntry("Render: draw through the whole screen", FullViewAddress, NativeUiCatalogue.Bytes("8A 0D 38 03 87 00 56 B8 01 00 00 00 33 F6 84 C8"),
            Source, "Cdecl (): the portal views become one full-screen view (0x8702F0), set as the current view; as before the landscape"),
        new NativeUiEntry("Render: set the current view", 0x0054DCF0, NativeUiCatalogue.Bytes(
            "8B 54 24 04 8B 44 24 08 A3 EC 72 86 00 89 15 54 70 84 00 8B 4A 04 8D 04 40 8D 04 C1 8B 08 89 0D 60 70 84 00 41 56 BE 01 00 00 00 " +
            "D3 E6 4E 89 35 C8 71 86 00 8B 48 04 8B 52 14 8D 0C 49 8D 0C CA 89 0D 50 70 84 00 8B 50 08 89 15 04 73 86 00 8B 48 0C 89 0D D0 70 84 00 " +
            "8B 50 10 89 15 CC 70 84 00 8B 40 14 A3 00 73 86 00 5E C3"),
            Source, "the globals it writes, saved and put back around the full-screen view"),
        new NativeUiEntry("The view-cone sphere test", SphereTestAddress, NativeUiCatalogue.Bytes("83 EC 1C D9 05 84 FC 81 00 8B 15 34 73 86 00"),
            Source, "Cdecl (CSphere*): 0 when a part's bounding sphere is outside the view cone; RenderDeviceD3D::DrawMesh skips the part then. Hooked only while the culled selection is drawn"),
        new NativeUiEntry("CPhysicsObj::SetLuminosity reads the part array", 0x00512374, NativeUiCatalogue.Bytes("8B 4E 10 85 C9 74 76"),
            Source, "the layout read here: CPhysicsObj +0x10 is its CPartArray")
    };

    /// <summary>
    /// With the camera in a building's room, retail draws the landscape seen through its doors and windows first (PView::DrawCells), then
    /// draws each opening, invisible, writing its depth over the landscape's, then the rooms. Drawn after the world, the storm and weather
    /// would be hidden behind those openings; drawn right after that landscape, the rooms cover them and the openings keep them. Their own
    /// list: if these do not match, only the weather seen from inside is lost.
    /// </summary>
    internal static readonly IReadOnlyList<NativeUiEntry> LandscapeEntries = new[]
    {
        new NativeUiEntry("LScape::draw", LandscapeDrawAddress, NativeUiCatalogue.Bytes("57 8B F9 8B 4F 20 85 C9 74 07 6A 00"),
            Source, "ThisCall (): draws the landscape; hooked, the storm and weather follow it when the camera is indoors"),
        new NativeUiEntry("PView::DrawCells draws the landscape before the openings and rooms", 0x005A596F, NativeUiCatalogue.Bytes("89 3D 30 73 86 00 8B 4F 78 83 C4 04"),
            Source, "followed by the LScape::draw call; the openings to outside (D3DPolyRender::DrawPortalPolyInternal) come after it"),
        new NativeUiEntry("SmartBox::RenderNormalMode reads the viewer's cell", 0x00453B59, NativeUiCatalogue.Bytes("8B 4E 0C 81 E1 FF FF 00 00 53 81 F9 00 01 00 00"),
            Source, "the layout read here: SmartBox +0xC is the camera's cell id; 0x100 and up in its low word draws through PView, from inside"),
    };

    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate void SmartBoxDrawFn(IntPtr smartBox);

    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate void LandscapeDrawFn(IntPtr landscape);

    [Function(CallingConventions.Stdcall)]
    private delegate int DrawIndexedFn(IntPtr device, int type, int baseVertex, uint minIndex, uint vertices, uint startIndex, uint primitives);

    [Function(CallingConventions.Stdcall)]
    private delegate int DrawFn(IntPtr device, int type, uint startVertex, uint primitives);

    [Function(CallingConventions.Stdcall)]
    private delegate int ResetFn(IntPtr device, IntPtr parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetObjectFn(uint objectId);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void DrawPartFn(IntPtr part, int force);

    [Function(CallingConventions.Cdecl)]
    private delegate int SphereTestFn(IntPtr sphere);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FullViewFn();

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UpdatePartsFn(IntPtr physicsObject);

    private readonly GetObjectFn _getObject = Marshal.GetDelegateForFunctionPointer<GetObjectFn>(new IntPtr(GetObjectAddress));
    private readonly DrawPartFn _drawPart = Marshal.GetDelegateForFunctionPointer<DrawPartFn>(new IntPtr(PartDrawAddress));
    private readonly UpdatePartsFn _updateParts = Marshal.GetDelegateForFunctionPointer<UpdatePartsFn>(new IntPtr(UpdatePartsAddress));
    private readonly FullViewFn _useFullView = Marshal.GetDelegateForFunctionPointer<FullViewFn>(new IntPtr(FullViewAddress));
    // While retail draws its world: parts drawn outside it (retail's 3D paperdoll in the inventory, with its own camera) are not measured or
    // outlined. While the selection, culled this frame, is drawn into the mask only.
    private bool _inWorld, _forcing;

    private readonly IntPtr _device;
    private readonly Action<string> _log;
    private readonly IHook<SmartBoxDrawFn> _smartBoxHook;
    private readonly IHook<LandscapeDrawFn>? _landscapeHook;
    // The camera is in an indoor cell this frame; the landscape (seen from inside) is being drawn; the storm and weather were drawn after it.
    private bool _viewIndoors, _inLandscape, _outsideDrawn;
    private readonly IHook<DrawIndexedFn> _drawHook;
    private readonly IHook<DrawFn> _drawPrimitiveHook;
    private readonly IHook<ResetFn> _resetHook;
    private readonly IHook<SphereTestFn> _sphereHook;
    private readonly StormFog _fog;
    private readonly Weather _weather;
    private readonly Water _water;
    private readonly CombatText _combat;
    private readonly PingBeams _pings;
    private GCHandle _root;
    private IntPtr _mask, _glow;
    private int _width, _height;
    // _masked: the mask is cleared this frame; _outlined: the selection is in it.
    private bool _masked, _outlined, _failed;
    private System.Numerics.Vector3 _lastOddMeasure;
    // The haze's fog over retail's world this frame (StormFog.TakeFog), and whether retail's own fog states have been logged once.
    private (uint Color, float Density)? _worldFog;
    private bool _fogLogged;
    private static readonly RenderState[] FogStates =
        { RenderState.FogEnable, RenderState.FogColor, RenderState.FogTableMode, RenderState.FogVertexMode, RenderState.FogDensity, RenderState.RangeFogEnable };

    private WorldEffects(IntPtr device, PortalDat portal, bool landscape, Action<string> log)
    {
        _device = device;
        _log = log;
        _fog = new StormFog(device);
        _weather = new Weather(device, _fog, portal, log);
        _water = new Water(device, _fog, portal, log);
        _combat = new CombatText(device, log);
        _pings = new PingBeams(device);
        _root = GCHandle.Alloc(this);
        _smartBoxHook = ReloadedHooks.Instance.CreateHook<SmartBoxDrawFn>(OnSmartBoxDraw, SmartBoxDrawAddress);
        if (landscape) _landscapeHook = ReloadedHooks.Instance.CreateHook<LandscapeDrawFn>(OnLandscapeDraw, LandscapeDrawAddress);
        _drawHook = ReloadedHooks.Instance.CreateHook<DrawIndexedFn>(OnDrawIndexed, Method(device, DrawIndexedPrimitiveSlot).ToInt64());
        _drawPrimitiveHook = ReloadedHooks.Instance.CreateHook<DrawFn>(OnDraw, Method(device, DrawPrimitiveSlot).ToInt64());
        _resetHook = ReloadedHooks.Instance.CreateHook<ResetFn>(OnReset, Method(device, ResetSlot).ToInt64());
        _sphereHook = ReloadedHooks.Instance.CreateHook<SphereTestFn>(OnSphereTest, SphereTestAddress);
        _sphereHook.Activate();
        _sphereHook.Disable();
        _resetHook.Activate();
        _drawHook.Activate();
        _drawPrimitiveHook.Activate();
        _smartBoxHook.Activate();
        _landscapeHook?.Activate();
        _weather.DrawsIndoors = _landscapeHook != null;
    }

    /// <summary>The effects, or null when the running client does not match their native entries.</summary>
    public static WorldEffects? Create(IntPtr device, PortalDat portal, Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
        {
            log("The selection outline, water, storm fog and weather are off: the running client does not match their native entries.");
            return null;
        }
        var landscape = NativeUiCatalogue.Validate(readMemory, log, LandscapeEntries);
        if (!landscape) log("The storm and weather are not drawn with the camera indoors: the running client does not match their native entries.");
        return new WorldEffects(device, portal, landscape, log);
    }

    /// <summary>The safe landblock the storm fog rings, as landblock x and y, or null for no storm.</summary>
    public (int X, int Y)? StormZone { get => _serverStorm; set => _fog.Zone = _serverStorm = value; }

    /// <summary>Floating combat text over the targets of the hits the server reports.</summary>
    public CombatText Combat => _combat;

    /// <summary>Radar pings as beams on the pinged spots.</summary>
    public PingBeams Pings => _pings;

    /// <summary>The weather and the landblock in the middle of it, or null for clear skies.</summary>
    public (WeatherKind Kind, int X, int Y)? Weather { get => _serverWeather; set => _weather.Current = _serverWeather = value; }

    /// <summary>Weather for testing, drawn around the player's landblock in place of the server's; None puts the server's back.</summary>
    public WeatherKind TestWeather
    {
        get => _testWeather;
        set
        {
            _testWeather = value;
            _weather.Current = _serverWeather;
        }
    }

    /// <summary>Fog for testing, alone or with the test weather: the storm's ring around the player's landblock; false puts the server's storm back.</summary>
    public bool TestFog
    {
        get => _testFogOn;
        set
        {
            _testFogOn = value;
            _testFog = null;
            _fog.Zone = _serverStorm;
        }
    }

    private (int X, int Y)? _serverStorm, _testFog;
    private (WeatherKind Kind, int X, int Y)? _serverWeather;
    private WeatherKind _testWeather;
    private bool _testFogOn;

    /// <summary>How strong the water is: 0 leaves retail's water, 1 is the look as designed.</summary>
    public float WaterStrength { get => _water.Strength; set => _water.Strength = value; }

    // Performance counters, read and reset by the runtime's 30 s log line.
    private readonly PhaseTimes _phases = new();
    private int _drawCalls;
    private int _frames;
    // ponytail: backlog 051 diagnostic, drop once a play-test shows the culled outline: frames the selection was culled, its parts
    // drawn forced, and the draws of them that reached the mask.
    private int _culledFrames, _culledParts, _culledMasked, _culledEmpty;
    // ponytail: same diagnostic: every 60th culled frame logs where the selection and its first part are and how many mask pixels the
    // pass covered (an occlusion query, read a frame or more later).
    private IntPtr _culledQuery;
    private bool _culledQueryPending;
    private string _culledSample = "";

    /// <summary>The effect timings and how many of retail's indexed draws passed through the hook per world frame, then starts again.</summary>
    public string TakePerformanceSummary()
    {
        var summary = $"frames={_frames} drawCallsPerFrame={(_frames == 0 ? 0 : _drawCalls / _frames)} {_phases.TakeSummary()} " +
            $"culledSelection={_culledFrames}/{_culledParts}/{_culledMasked}/{_culledEmpty}";
        _drawCalls = 0;
        _frames = 0;
        _culledFrames = _culledParts = _culledMasked = _culledEmpty = 0;
        return summary;
    }

    /// <summary>The object to outline, zero for none. Set each frame.</summary>
    public uint Selected { get; set; }

    private int OnDrawIndexed(IntPtr device, int type, int baseVertex, uint minIndex, uint vertices, uint startIndex, uint primitives)
    {
        _drawCalls++;
        var original = _drawHook.OriginalFunction;
        if (_forcing)
        {
            // Not on screen: the selection is drawn here only so the outline shows through what culled it.
            try { if (DrawsSelected()) MaskIndexed(original, device, type, baseVertex, minIndex, vertices, startIndex, primitives, SelectionChannels); }
            catch (Exception exception) { Fail(exception); }
            return 0;
        }
        // Retail may set its own fog between draws; the haze's goes back on before each one.
        if (Fogging && _worldFog is { } fog && device == _device && !_failed)
        {
            try { ApplyFog(fog); }
            catch (Exception exception) { Fail(exception); }
        }
        var result = original(device, type, baseVertex, minIndex, vertices, startIndex, primitives);
        // A glow's black fog colour is not left for a draw that does not pass through these hooks.
        if (Fogging && _worldFog is { } grey && device == _device && !_failed)
        {
            try { SetRenderState(_device, RenderState.FogColor, unchecked((int)(0xFF000000 | grey.Color))); }
            catch (Exception exception) { Fail(exception); }
        }
        var part = *DrawingPart;
        if (result < 0 || part == 0 || !_inWorld || _failed || device != _device) return result;
        var channels = 0;
        try
        {
            // Only objects in a world cell: the sky's objects (and anything else not in the world) are drawn with their own camera.
            var physicsObject = *(int*)(part + 0xE0);
            var player = physicsObject == *PlayerObject;
            if (physicsObject != 0 && *(int*)(physicsObject + 0x90) != 0)
            {
                var id = *(uint*)(physicsObject + 0x8);
                if (!_fog.Calibrated || (player && !_fog.FromPlayer))
                {
                    var measure = _fog.Calibrate(part, player);
                    // ponytail: diagnostic for the 19 Oct water flicker; drop once a play-test shows which parts measure wrong.
                    if (measure != _fog.Offset && measure != _lastOddMeasure)
                        _log($"World offset: {(player ? "the player" : $"object {id:X8}")} measured {measure}, kept {_fog.Offset}.");
                    _lastOddMeasure = measure == _fog.Offset ? _lastOddMeasure : measure;
                }
                if (_combat.Tracks(id)) _combat.Saw(id, GetTransform(_device, TransformWorld).Translation - _fog.Offset);
                if (Selected != 0) channels = id == Selected ? SelectionChannels : player ? PlayerChannels : 0;
            }
        }
        catch (Exception exception) { Fail(exception); }
        if (channels == 0) return result;
        try { MaskIndexed(original, device, type, baseVertex, minIndex, vertices, startIndex, primitives, channels); }
        catch (Exception exception) { Fail(exception); }
        return result;
    }

    /// <summary>The landscape seen from inside a building: the storm and weather go on straight after it, before the openings and rooms.</summary>
    private void OnLandscapeDraw(IntPtr landscape)
    {
        if (!_viewIndoors || !_inWorld || _failed || _outsideDrawn)
        {
            _landscapeHook!.OriginalFunction(landscape);
            return;
        }
        int[]? retailFog = null;
        try { if (_worldFog != null) retailFog = SaveFog(); }
        catch (Exception exception) { Fail(exception); }
        _inLandscape = true;
        try { _landscapeHook!.OriginalFunction(landscape); }
        finally
        {
            _inLandscape = false;
            RestoreFog(retailFog);
        }
        if (_failed) return;
        var start = PhaseTimes.Start();
        try { DrawOutside(PlayerCell(), shown: true); }
        catch (Exception exception) { Fail(exception); }
        _phases.Add("outside", start);
        _outsideDrawn = true;
    }

    private int[] SaveFog() => Array.ConvertAll(FogStates, state => GetRenderState(_device, state));

    private void RestoreFog(int[]? fog)
    {
        if (fog == null) return;
        try { for (var i = 0; i < FogStates.Length; i++) SetRenderState(_device, FogStates[i], fog[i]); }
        catch (Exception exception) { Fail(exception); }
    }

    /// <summary>The storm and weather; the storm's frame ends here even when nothing is shown.</summary>
    private void DrawOutside(uint cell, bool shown)
    {
        if (cell != 0)
        {
            int x = (int)(cell >> 24), y = (int)(cell >> 16) & 0xFF;
            // Test fog rings the landblock the player was in when it was turned on, and stays, so they can walk out into it. Test weather
            // follows the player: the 3×3 landblocks around theirs.
            if (_testFogOn) _fog.Zone = _testFog ??= (x, y);
            if (_testWeather != WeatherKind.None) _weather.Current = (_testWeather, x, y);
        }
        _fog.Draw(inWorld: shown && cell != 0);
        _weather.PlayerCell = cell;
        if (shown) _weather.Draw();
    }

    /// <summary>
    /// The player's cell id; zero while they are in no cell: in portal space, and before the world at login, where the storm and weather
    /// would be drawn into retail's portal warp.
    /// </summary>
    private static uint PlayerCell()
    {
        var player = *PlayerObject;
        return player != 0 && *(int*)(player + 0x90) != 0 ? *(uint*)(player + 0x4C) : 0;
    }

    /// <summary>
    /// Retail draws some of its world (terrain among it) unindexed. The haze's fog goes on before those too: they would otherwise keep the
    /// last indexed draw's fog, black after a glow, and fog whole patches of ground black (20 Oct).
    /// </summary>
    private int OnDraw(IntPtr device, int type, uint startVertex, uint primitives)
    {
        if (_forcing)
        {
            // As in OnDrawIndexed: the forced selection goes into the mask only.
            try { if (DrawsSelected()) DrawIntoMask(() => _drawPrimitiveHook.OriginalFunction(device, type, startVertex, primitives), SelectionChannels); }
            catch (Exception exception) { Fail(exception); }
            return 0;
        }
        if (Fogging && _worldFog is { } fog && device == _device && !_failed)
        {
            try { ApplyFog(fog); }
            catch (Exception exception) { Fail(exception); }
        }
        return _drawPrimitiveHook.OriginalFunction(device, type, startVertex, primitives);
    }

    private bool DrawsSelected()
    {
        var part = *DrawingPart;
        if (part == 0) return false;
        var physicsObject = *(int*)(part + 0xE0);
        return physicsObject != 0 && *(uint*)(physicsObject + 0x8) == Selected;
    }

    // Its own method so the lambda's closure is built only for a masked draw: captured in the hook, the compiler built it on every draw.
    private void MaskIndexed(DrawIndexedFn original, IntPtr device, int type, int baseVertex, uint minIndex, uint vertices, uint startIndex,
        uint primitives, int channels) =>
        DrawIntoMask(() => original(device, type, baseVertex, minIndex, vertices, startIndex, primitives), channels);

    /// <summary>Repeats one draw into the mask's channels, flat white and through anything in front, then puts the device back.</summary>
    private void DrawIntoMask(Action draw, int channels)
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
                SetRenderState(_device, RenderState.ColorWriteEnable, 0xF);
                if (!_masked) ClearTargetTo(_device, 0);
                SetViewport(_device, viewport);
                SetRenderState(_device, RenderState.ZEnable, 0);
                SetRenderState(_device, RenderState.ZWriteEnable, 0);
                SetRenderState(_device, RenderState.AlphaBlendEnable, 0);
                SetRenderState(_device, RenderState.AlphaTestEnable, 0);
                SetRenderState(_device, RenderState.FogEnable, 0);
                SetRenderState(_device, RenderState.StencilEnable, 0);
                // Indoors retail may hold drawing to the portal in view; the outline must show past it (backlog 051).
                SetRenderState(_device, RenderState.ScissorTestEnable, 0);
                SetRenderState(_device, RenderState.ClipPlaneEnable, 0);
                SetRenderState(_device, RenderState.ColorWriteEnable, channels);
                SetRenderState(_device, RenderState.TextureFactor, unchecked((int)0xFFFFFFFF));
                SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
                SetStageState(_device, 0, StageState.ColorArg1, ArgFactor);
                SetStageState(_device, 0, StageState.AlphaOp, OpSelectArg1);
                SetStageState(_device, 0, StageState.AlphaArg1, ArgFactor);
                SetStageState(_device, 1, StageState.ColorOp, OpDisable);
                SetStageState(_device, 1, StageState.AlphaOp, OpDisable);
                draw();
                if (_forcing) _culledMasked++;
                _masked = true;
                _outlined |= channels == SelectionChannels;
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

    // The haze fogs retail's world as it is drawn; with the camera indoors, only the landscape seen through the openings, not the rooms.
    private bool Fogging => _inWorld && (!_viewIndoors || _inLandscape);

    private void OnSmartBoxDraw(IntPtr smartBox)
    {
        _worldFog = _failed ? null : _fog.TakeFog();
        _viewIndoors = _landscapeHook != null && (*(uint*)(smartBox + 0xC) & 0xFFFF) >= 0x100;
        _outsideDrawn = false;
        int[]? retailFog = null;
        try
        {
            if (_worldFog is { } fog && !_viewIndoors)
            {
                retailFog = SaveFog();
                if (!_fogLogged)
                {
                    _fogLogged = true;
                    _log($"Haze fog: retail's fog states before the world are {string.Join(", ", FogStates.Select((state, i) => $"{state} 0x{retailFog[i]:X}"))}.");
                }
                ApplyFog(fog);
            }
        }
        catch (Exception exception) { Fail(exception); }
        _water.FogDensity = _worldFog?.Density ?? 0;
        _inWorld = true;
        try { _smartBoxHook.OriginalFunction(smartBox); }
        finally
        {
            _inWorld = false;
            RestoreFog(retailFog);
        }
        if (!_failed)
        {
            try
            {
                var cell = PlayerCell();
                // The water first, so the fog and weather fall over it.
                _water.PlayerCell = cell;
                var start = PhaseTimes.Start();
                _water.Draw();
                _phases.Add("water", start);
                // From indoors they were drawn after the landscape, or, with no opening to outside in view, not at all.
                if (!_outsideDrawn)
                {
                    start = PhaseTimes.Start();
                    DrawOutside(cell, shown: !_viewIndoors);
                    _phases.Add("outside", start);
                }
                start = PhaseTimes.Start();
                _combat.Draw(_fog);
                _pings.Draw(_fog);
                if (!_outlined && Selected != 0) DrawCulledSelection();
                _phases.Add("combat+pings+selection", start);
                _frames++;
            }
            catch (Exception exception) { Fail(exception); }
        }
        try
        {
            if (_outlined && !_failed) Composite();
        }
        catch (Exception exception) { Fail(exception); }
        finally { _masked = _outlined = false; }
    }

    /// <summary>
    /// Direct3D's exponential fog by each vertex's distance from the camera, so the haze thickens smoothly over retail's world, with no
    /// steps where the ground crosses a slice. ponytail: vertex fog only fogs what retail draws with fixed-function transforms; the log line
    /// of retail's own fog states says how it draws.
    /// </summary>
    private void ApplyFog((uint Color, float Density) fog)
    {
        // An additive draw (glows, like a lantern's) fogs to black, fading with distance; fogged to grey, its whole quad lit up (19 Oct).
        var additive = GetRenderState(_device, RenderState.AlphaBlendEnable) != 0 && GetRenderState(_device, RenderState.DestBlend) == BlendOne;
        SetRenderState(_device, RenderState.FogEnable, 1);
        SetRenderState(_device, RenderState.FogColor, unchecked((int)(0xFF000000 | (additive ? 0 : fog.Color))));
        SetRenderState(_device, RenderState.FogTableMode, 0);
        SetRenderState(_device, RenderState.FogVertexMode, 1); // D3DFOG_EXP
        SetRenderState(_device, RenderState.FogDensity, BitConverter.SingleToInt32Bits(fog.Density));
        SetRenderState(_device, RenderState.RangeFogEnable, 1);
    }

    /// <summary>
    /// Has retail draw the selected object's parts again, into the mask only: indoors, retail culls what is in rooms out of view. Unforced,
    /// with the view-cone test passing every part: retail skips a part whose sphere is outside every portal's cone, so a target a room or
    /// two away was not outlined, and forced, the parts went through the paperdoll's path and never reached the device (backlog 051).
    /// Through the whole screen: after the world the view left is the last room drawn's, whose doorway cut a farther target away.
    /// ponytail: the parts keep the pose they were last animated in; only their place follows the object.
    /// </summary>
    private void DrawCulledSelection()
    {
        var physicsObject = _getObject(Selected);
        // Out of the world (picked up into a pack, or gone): no cell, and its parts keep the place it was last drawn.
        if (physicsObject == IntPtr.Zero || Marshal.ReadIntPtr(physicsObject, 0x90) == IntPtr.Zero) return;
        var parts = Marshal.ReadIntPtr(physicsObject, 0x10);
        if (parts == IntPtr.Zero) return;
        // Retail places the parts only when it draws the object: never drawn, they sit at zero; out of view, where it was last seen.
        _updateParts(physicsObject);
        int count = Marshal.ReadInt32(parts, 0x58);
        var list = Marshal.ReadIntPtr(parts, 0x5C);
        _culledFrames++;
        var masked = _culledMasked;
        var sample = _culledFrames % 60 == 1;
        if (_culledQueryPending && QueryPixels(_culledQuery) is { } pixels)
        {
            _culledQueryPending = false;
            _log($"{_culledSample} covered {pixels} mask pixels.");
        }
        if (sample && !_culledQueryPending)
        {
            if (_culledQuery == IntPtr.Zero) _culledQuery = CreateOcclusionQuery(_device);
            var first = count > 0 ? Marshal.ReadIntPtr(list) : IntPtr.Zero;
            _culledSample = $"Culled selection {Selected:X8}: object in {Marshal.ReadInt32(physicsObject, 0x4C):X8} at {Origin(physicsObject + 0x84)}, " +
                (first == IntPtr.Zero ? "no first part" : $"first part in {Marshal.ReadInt32(first, 0x7C):X8} at {Origin(first + 0xB4)}") +
                $", camera {_fog.Eye} (render = game + {_fog.Offset})";
            BeginQuery(_culledQuery);
            _culledQueryPending = true;
        }
        else sample = false;
        // The last room's view is not the selection's: its polygons were cut to that room's doorway. Through the whole screen instead.
        for (var i = 0; i < ViewGlobals.Length; i++) _savedView[i] = *ViewGlobals[i];
        _useFullView();
        _forcing = true;
        _sphereHook.Enable();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var part = Marshal.ReadIntPtr(list, i * 4);
                if (part == IntPtr.Zero) continue;
                _culledParts++;
                _drawPart(part, 0);
            }
        }
        finally
        {
            _sphereHook.Disable();
            _forcing = false;
            for (var i = 0; i < ViewGlobals.Length; i++) *ViewGlobals[i] = _savedView[i];
            if (_culledMasked == masked) _culledEmpty++;
            if (sample)
            {
                EndQuery(_culledQuery);
                _culledSample += $", {_culledMasked - masked} masked draws,";
            }
        }
    }

    private static string Origin(IntPtr at) =>
        $"({BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at)):0.0}, {BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at, 4)):0.0}, {BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at, 8)):0.0})";

    // Retail's test still runs for what it records; outside the cone (0) becomes 1, partly in view.
    private int OnSphereTest(IntPtr sphere)
    {
        var inView = _sphereHook.OriginalFunction(sphere);
        return _forcing && inView == 0 ? 1 : inView;
    }

    /// <summary>Spreads the mask into the glow texture, then adds the glow to the screen in gold outside the mask.</summary>
    private void Composite()
    {
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
            for (var stage = 0; stage < 3; stage++)
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

            // The rim: glow times gold, times one minus the mask's colour and alpha, added to the screen.
            SetRenderTarget(_device, target);
            SetTexture(_device, 0, _glow);
            SetTexture(_device, 1, _mask);
            SetTexture(_device, 2, _mask);
            SetRenderState(_device, RenderState.TextureFactor, unchecked((int)Gold));
            SetStageState(_device, 1, StageState.ColorOp, OpModulate);
            SetStageState(_device, 1, StageState.ColorArg1, ArgTexture | ArgComplement);
            SetStageState(_device, 1, StageState.ColorArg2, ArgCurrent);
            SetStageState(_device, 1, StageState.AlphaOp, OpSelectArg1);
            SetStageState(_device, 1, StageState.AlphaArg1, ArgCurrent);
            SetStageState(_device, 2, StageState.ColorOp, OpModulate);
            SetStageState(_device, 2, StageState.ColorArg1, ArgTexture | ArgComplement | ArgAlphaReplicate);
            SetStageState(_device, 2, StageState.ColorArg2, ArgCurrent);
            SetStageState(_device, 2, StageState.AlphaOp, OpSelectArg1);
            SetStageState(_device, 2, StageState.AlphaArg1, ArgCurrent);
            SetStageState(_device, 3, StageState.ColorOp, OpDisable);
            SetStageState(_device, 3, StageState.AlphaOp, OpDisable);
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
        if (device == _device)
        {
            ReleaseTextures();
            Release(_culledQuery);
            _culledQuery = IntPtr.Zero;
            _culledQueryPending = false;
        }
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
        _landscapeHook?.Disable();
        _drawHook.Disable();
        _drawPrimitiveHook.Disable();
        _resetHook.Disable();
        _sphereHook.Disable();
        ReleaseTextures();
        Release(_culledQuery);
        _fog.Dispose();
        _weather.Dispose();
        _water.Dispose();
        _combat.Dispose();
        _pings.Dispose();
        if (_root.IsAllocated) _root.Free();
    }
}
