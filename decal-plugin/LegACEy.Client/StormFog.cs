using System;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// A look-only battle-royale storm: the ring of landblocks around the <see cref="Zone"/>'s safe landblock is filled with grey fog. From outside,
/// nested translucent walls show the fog bank, hidden by the game's depth buffer behind terrain and buildings. Inside, the view fades into
/// fog with distance: camera-facing slices, each depth-tested against retail's scene, so a pixel is fogged by every slice in front of it.
/// Game positions (z-up) become render positions (y-up, relative to the viewer's landblock) through an offset measured while retail draws a part (<see cref="Calibrate"/>).
/// </summary>
internal sealed unsafe class StormFog : IDisposable
{
    internal const float Block = 192;
    private const float Bottom = -50, Top = 450;
    // The ring's edges, as distances from the safe landblock's centre: its own edge, and the outer edge one landblock further out.
    private const float Inner = Block / 2, Outer = Block * 1.5f;
    // From outside, the fog bank is many faint walls across each edge of the ring, so where terrain cuts them there is no hard seam to jump
    // about as retail reshapes the terrain with distance: Bank metres deep, 1/e of what is behind showing through every BankThickness metres.
    private const int Layers = 24, NoiseSize = 128;
    private const float Bank = 40, BankThickness = 20;
    private const uint Grey = 0xC8CDCF;
    // Inside: fog thick enough that 1/e of the scene shows through every Thickness metres, reached FadeIn metres past the edge.
    private const float Thickness = 45, FadeIn = 30, LastSlice = 400;
    // How far the volume takes the sky to be: past it, the ring is all fog anyway.
    private const float SkyDistance = 1500;
    private static readonly (int X, int Y)[] Corners = { (-1, -1), (1, -1), (1, 1), (-1, 1) };

    private readonly IntPtr _device;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IntPtr _noise;
    // The volume's shaders (StormFog.hlsl); _shaded is null until first tried, false when they did not compile.
    private IntPtr _vertexShader, _pixelShader;
    private bool? _shaded;
    private readonly Action<string> _log;
    // The fog the haze asked for this frame (the thickest), laid over retail's world next frame (TakeFog).
    private (uint Color, float Density)? _nextFog;
    // Frames since the player's own part was measured.
    private int _sincePlayer = int.MaxValue / 2;

    /// <summary>A part has been measured, so <see cref="Offset"/>, <see cref="View"/> and <see cref="Projection"/> hold the last frame's camera.</summary>
    public bool Measured { get; private set; }

    /// <summary>Global render position (game position, y and z swapped) plus this is retail's render position.</summary>
    public Vector3 Offset { get; private set; }

    public Matrix4x4 View { get; private set; }

    public Matrix4x4 Projection { get; private set; }

    /// <summary>The camera's global game position (z-up), or null before the first measure.</summary>
    public Vector3? Eye => Measured && Matrix4x4.Invert(View, out var camera) ? Render(camera.Translation - Offset) : null;

    public StormFog(IntPtr device, Action<string> log)
    {
        _device = device;
        _log = log;
    }

    /// <summary>
    /// The player's global game position (z-up), from their part last measured; null before. How deep in the fog or weather is taken from
    /// here, not the camera, which swings in and out of the edge as a third-person view turns (seen as the fog pulsing, 19 Oct).
    /// </summary>
    public Vector3? Player { get; private set; }

    /// <summary>The safe landblock the storm rings, as landblock x and y; null means no storm is drawn.</summary>
    public (int X, int Y)? Zone { get; set; }

    /// <summary>A part was measured this frame; the player's own part, the better measure, once <see cref="FromPlayer"/>.</summary>
    public bool Calibrated { get; private set; }

    public bool FromPlayer { get; private set; }

    /// <summary>
    /// Measures the game-to-render offset from a part being drawn: its game position against the device's world transform. Returns the
    /// part's measure, which another object's part can get a whole landblock wrong (seen as the water jumping for a frame, 19 Oct), so
    /// the offset is only taken from the player's part while the player has been drawn in the last second.
    /// </summary>
    public Vector3 Calibrate(int part, bool player)
    {
        var cell = *(uint*)(part + 0x7C);
        var origin = new Vector3(*(float*)(part + 0xB4), *(float*)(part + 0xB8), *(float*)(part + 0xBC));
        // Retail renders relative to a landblock corner, so the offset is whole landblocks; rounding drops a part's small measuring error.
        var offset = Snap(GetTransform(_device, TransformWorld).Translation - Render(Global(cell, origin)));
        // The camera is the same for every part of the world; the offset is taken from another part only while the player's is not drawn.
        View = GetTransform(_device, TransformView);
        Projection = GetTransform(_device, TransformProjection);
        if (player)
        {
            _sincePlayer = 0;
            Player = Global(cell, origin);
        }
        if (player || (!FromPlayer && _sincePlayer > 30)) Offset = offset;
        Measured = true;
        Calibrated = true;
        FromPlayer |= player;
        return offset;
    }

    /// <summary>
    /// Draws the storm after retail's world, then waits for the next frame's measure. With the scene's depth, as a volume of fog (one pass,
    /// no edges where it meets the ground); without it, in walls and slices.
    /// </summary>
    public void Draw(SceneDepth depth)
    {
        // A frame with nothing measured (no part drawn yet) keeps the last measure, so the fog does not blink out for a frame.
        if (!Measured) return;
        Calibrated = FromPlayer = false;
        _sincePlayer++;
        if (Zone is not { } zone) return;
        if (_noise == IntPtr.Zero) _noise = MakeNoise();
        if (depth.Texture != IntPtr.Zero && (_shaded ??= MakeShaders()) == true && Eye is { } camera)
        {
            depth.Reading(() => DrawVolume(depth.Texture, new Vector2(depth.Width, depth.Height), zone, camera));
            return;
        }
        var saved = SaveState(_device);
        try
        {
            UseFixedFunction(_device, FvfXyz | FvfDiffuse | FvfTex1);
            UseWorldCamera();
            SetTexture(_device, 0, _noise);
            SetStageState(_device, 0, StageState.ColorOp, OpModulate);
            SetStageState(_device, 0, StageState.ColorArg1, ArgTexture);
            SetStageState(_device, 0, StageState.ColorArg2, ArgDiffuse);
            SetStageState(_device, 0, StageState.AlphaOp, OpModulate);
            SetStageState(_device, 0, StageState.AlphaArg1, ArgTexture);
            SetStageState(_device, 0, StageState.AlphaArg2, ArgDiffuse);
            SetStageState(_device, 0, StageState.TexCoordIndex, 0);
            SetStageState(_device, 0, StageState.TextureTransformFlags, 0);
            SetStageState(_device, 1, StageState.ColorOp, OpDisable);
            SetStageState(_device, 1, StageState.AlphaOp, OpDisable);
            SetSamplerState(_device, 0, SamplerState.MinFilter, FilterLinear);
            SetSamplerState(_device, 0, SamplerState.MagFilter, FilterLinear);
            SetSamplerState(_device, 0, SamplerState.MipFilter, FilterNone);
            SetSamplerState(_device, 0, SamplerState.AddressU, AddressWrap);
            SetSamplerState(_device, 0, SamplerState.AddressV, AddressWrap);

            var time = (float)_clock.Elapsed.TotalSeconds;
            var eye = Eye;
            var centre = Centre(zone);
            var walls = stackalloc FogVertex[10];
            var step = Bank / Layers;
            var alpha = 1 - MathF.Exp(-step / BankThickness);
            for (var wall = 0; wall < Layers * 2; wall++)
            {
                // Half of the walls run in from the safe landblock's edge, half in from the ring's outer edge.
                var layer = wall % Layers;
                var halfWidth = wall < Layers ? Inner + layer * step : Outer - layer * step;
                // A wall fades out within 10 m of the camera, so passing through it does not flash it across the screen.
                var fade = eye is { } p ? Math.Clamp(MathF.Abs(FromCentre(p, zone) - halfWidth) / 10, 0, 1) : 1;
                var color = (uint)(alpha * fade * 255) << 24 | Grey;
                // Each wall's noise is offset and drifts its own way, so the wisps of neighbouring walls do not line up.
                var drift = (wall % 2 == 0 ? 1 : -1) * time * 0.03f + wall * 0.37f;
                for (var corner = 0; corner <= 4; corner++)
                {
                    var (sx, sy) = Corners[corner % 4];
                    float x = centre.X + sx * halfWidth, y = centre.Y + sy * halfWidth;
                    var u = corner * halfWidth * 2 / 96 + drift;
                    walls[corner * 2] = new(Render(new Vector3(x, y, Bottom)), color, u, time * 0.02f);
                    walls[corner * 2 + 1] = new(Render(new Vector3(x, y, Top)), color, u, (Top - Bottom) / 128 + time * 0.02f);
                }
                DrawPrimitiveUP(_device, TriangleStrip, 8, walls, sizeof(FogVertex));
            }

            var strength = (Player ?? eye) is { } inside ? Math.Clamp(StormDepth(inside, zone) / FadeIn, 0, 1) : 0;
            if (strength > 0) DrawHaze(Grey, Thickness, strength);
        }
        finally { RestoreState(saved); }
    }

    /// <summary>The fog as a volume: each pixel fogged by the metres of ring between it and the camera, from the scene's depth (StormFog.hlsl).</summary>
    private void DrawVolume(IntPtr depth, Vector2 size, (int X, int Y) zone, Vector3 eye)
    {
        Matrix4x4.Invert(Matrix4x4.CreateTranslation(Offset) * View * Projection, out var inverse);
        var screen = Viewport(_device);
        var centre = Centre(zone);
        var time = (float)_clock.Elapsed.TotalSeconds;
        static float Wrap(float value) => value - MathF.Floor(value);
        var saved = SaveState(_device);
        try
        {
            UseShaders(_device, _vertexShader, _pixelShader, FvfXyz);
            SetPixelConstants(_device, 0, System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, Vector4>(new ReadOnlySpan<Matrix4x4>(ref inverse)));
            SetPixelConstants(_device, 4, new[]
            {
                new Vector4(Render(eye), 0), new Vector4(centre.X, centre.Y, Inner, Outer), new Vector4(Bottom, Top, 1 / Thickness, SkyDistance),
                new Vector4(((Grey >> 16) & 0xFF) / 255f, ((Grey >> 8) & 0xFF) / 255f, (Grey & 0xFF) / 255f, 1), new Vector4(screen.X, screen.Y, screen.Width, screen.Height), new Vector4(size, 0, 0),
                new Vector4(Bank, 150, Wrap(time * 0.004f), Wrap(time * 0.003f))
            });
            SetTexture(_device, 1, _noise);
            SetSamplerState(_device, 1, SamplerState.MinFilter, FilterLinear);
            SetSamplerState(_device, 1, SamplerState.MagFilter, FilterLinear);
            SetSamplerState(_device, 1, SamplerState.MipFilter, FilterNone);
            SetSamplerState(_device, 1, SamplerState.AddressU, AddressWrap);
            SetSamplerState(_device, 1, SamplerState.AddressV, AddressWrap);
            SetTexture(_device, 0, depth);
            SetSamplerState(_device, 0, SamplerState.MinFilter, FilterPoint);
            SetSamplerState(_device, 0, SamplerState.MagFilter, FilterPoint);
            SetSamplerState(_device, 0, SamplerState.MipFilter, FilterNone);
            SetSamplerState(_device, 0, SamplerState.AddressU, AddressClamp);
            SetSamplerState(_device, 0, SamplerState.AddressV, AddressClamp);
            SetRenderState(_device, RenderState.ZEnable, 0);
            SetRenderState(_device, RenderState.ZWriteEnable, 0);
            SetRenderState(_device, RenderState.StencilEnable, 0);
            SetRenderState(_device, RenderState.AlphaTestEnable, 0);
            SetRenderState(_device, RenderState.ScissorTestEnable, 0);
            SetRenderState(_device, RenderState.CullMode, CullNone);
            SetRenderState(_device, RenderState.ColorWriteEnable, 0xF);
            SetRenderState(_device, RenderState.AlphaBlendEnable, 1);
            SetRenderState(_device, RenderState.SeparateAlphaBlendEnable, 0);
            SetRenderState(_device, RenderState.BlendOp, BlendOpAdd);
            SetRenderState(_device, RenderState.SrcBlend, BlendSrcAlpha);
            SetRenderState(_device, RenderState.DestBlend, BlendInvSrcAlpha);
            // The whole screen, in clip space.
            var quad = stackalloc float[] { -1, 1, 0, 1, 1, 0, -1, -1, 0, 1, -1, 0 };
            DrawPrimitiveUP(_device, TriangleStrip, 2, quad, 12);
        }
        finally
        {
            SetTexture(_device, 0, IntPtr.Zero);
            SetTexture(_device, 1, IntPtr.Zero);
            RestoreState(saved);
        }
    }

    /// <summary>Compiles StormFog.hlsl; false, with the reason logged, when it does not, and the walls and slices are drawn instead.</summary>
    private bool MakeShaders()
    {
        try
        {
            using var stream = typeof(StormFog).Assembly.GetManifestResourceStream("StormFog.hlsl")!;
            var source = new System.IO.StreamReader(stream).ReadToEnd();
            _vertexShader = CompileShader(_device, source, "Vs", "vs_3_0");
            _pixelShader = CompileShader(_device, source, "Fog", "ps_3_0");
            _log("Storm fog: drawn as a volume.");
            return true;
        }
        catch (Exception exception)
        {
            ReleaseShaders();
            _log($"Storm fog: drawn in walls and slices, as its shaders did not compile: {exception.Message}");
            return false;
        }
    }

    private void ReleaseShaders()
    {
        Release(_vertexShader);
        Release(_pixelShader);
        _vertexShader = _pixelShader = IntPtr.Zero;
    }

    /// <summary>Retail's camera and game-space world, depth-tested against its scene but not writing depth, blended by alpha.</summary>
    public void UseWorldCamera()
    {
        SetTransform(_device, TransformWorld, Matrix4x4.CreateTranslation(Offset));
        SetTransform(_device, TransformView, View);
        SetTransform(_device, TransformProjection, Projection);
        SetRenderState(_device, RenderState.ZEnable, 1);
        SetRenderState(_device, RenderState.ZFunc, CmpLessEqual);
        SetRenderState(_device, RenderState.ZWriteEnable, 0);
        SetRenderState(_device, RenderState.Lighting, 0);
        SetRenderState(_device, RenderState.FogEnable, 0);
        SetRenderState(_device, RenderState.AlphaTestEnable, 0);
        SetRenderState(_device, RenderState.StencilEnable, 0);
        SetRenderState(_device, RenderState.CullMode, CullNone);
        SetRenderState(_device, RenderState.ColorWriteEnable, 0xF);
        SetRenderState(_device, RenderState.AlphaBlendEnable, 1);
        SetRenderState(_device, RenderState.SeparateAlphaBlendEnable, 0);
        SetRenderState(_device, RenderState.BlendOp, BlendOpAdd);
        SetRenderState(_device, RenderState.SrcBlend, BlendSrcAlpha);
        SetRenderState(_device, RenderState.DestBlend, BlendInvSrcAlpha);
    }

    /// <summary>The fog to lay over retail's world this frame, as Direct3D's exponential fog: its colour and density per metre. Clears it.</summary>
    public (uint Color, float Density)? TakeFog()
    {
        var fog = _nextFog;
        _nextFog = null;
        return fog;
    }

    /// <summary>
    /// The fog inside, fogging a pixel at distance d by 1 - e^(-d × strength / thickness). Retail's world is fogged as it is drawn, next frame
    /// (<see cref="TakeFog"/>), smoothly per vertex; here only one camera-facing slice is drawn, far off and hidden where retail's scene is
    /// nearer, to fog the sky. (Many slices drew a contour line wherever the ground crossed one, 19 Oct.)
    /// Expects <see cref="UseWorldCamera"/>'s states; leaves the world and view transforms at identity.
    /// </summary>
    public void DrawHaze(uint color, float thickness, float strength)
    {
        UseFixedFunction(_device, FvfXyz | FvfDiffuse);
        SetTexture(_device, 0, IntPtr.Zero);
        SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
        SetStageState(_device, 0, StageState.ColorArg1, ArgDiffuse);
        SetStageState(_device, 0, StageState.AlphaOp, OpSelectArg1);
        SetStageState(_device, 0, StageState.AlphaArg1, ArgDiffuse);
        // In view space: the camera at the origin, looking along +z, or -z under a right-handed projection.
        SetTransform(_device, TransformWorld, Matrix4x4.Identity);
        SetTransform(_device, TransformView, Matrix4x4.Identity);
        var forward = Projection.M34 >= 0 ? 1 : -1;
        // The last slice stays inside the far plane (from the projection: far = M43 / (forward - M33)), or the sky behind would show.
        var lastSlice = MathF.Min(LastSlice, MathF.Abs(Projection.M43 / (forward - Projection.M33)) * 0.98f);
        var density = strength / thickness;
        if (_nextFog is not { } fog || fog.Density < density) _nextFog = (color, density);
        var quad = stackalloc SliceVertex[4];
        var alpha = 1 - MathF.Exp(-lastSlice * density);
        // Wider than the view, so the slice covers the screen whatever the aspect.
        float x = lastSlice / Projection.M11 * 1.2f, y = lastSlice / Projection.M22 * 1.2f, z = lastSlice * forward;
        var argb = (uint)(alpha * 255) << 24 | color;
        quad[0] = new(-x, y, z, argb);
        quad[1] = new(x, y, z, argb);
        quad[2] = new(-x, -y, z, argb);
        quad[3] = new(x, -y, z, argb);
        DrawPrimitiveUP(_device, TriangleStrip, 2, quad, sizeof(SliceVertex));
    }

    /// <summary>Metres inside the storm ring to its nearer edge (the safe landblock's, or the ring's outer one); negative outside it.</summary>
    internal static float StormDepth(Vector3 position, (int X, int Y) zone)
    {
        var fromCentre = FromCentre(position, zone);
        return MathF.Min(fromCentre - Inner, Outer - fromCentre);
    }

    /// <summary>The safe landblock's centre, in render space.</summary>
    private static Vector2 Centre((int X, int Y) zone) => new((zone.X + 0.5f) * Block, (zone.Y + 0.5f) * Block);

    /// <summary>Distance from the safe landblock's centre, measured square: the half-size of the wall square through the position.</summary>
    private static float FromCentre(Vector3 position, (int X, int Y) zone)
    {
        var centre = Centre(zone);
        return MathF.Max(MathF.Abs(position.X - centre.X), MathF.Abs(position.Y - centre.Y));
    }

    private static Vector3 Snap(Vector3 offset) =>
        new(MathF.Round(offset.X / Block) * Block, MathF.Round(offset.Y / Block) * Block, MathF.Round(offset.Z / Block) * Block);

    /// <summary>Game space is z-up; retail renders y-up, swapping y and z (its own inverse).</summary>
    internal static Vector3 Render(Vector3 game) => new(game.X, game.Z, game.Y);

    /// <summary>A cell's position in one space for the whole world: landblock x and y (the id's top two bytes) times 192 m, plus the origin.</summary>
    private static Vector3 Global(uint cell, Vector3 origin) => new((cell >> 24) * Block + origin.X, ((cell >> 16) & 0xFF) * Block + origin.Y, origin.Z);

    /// <summary>Tileable white value noise in the alpha channel: wisps for the fog to drift through.</summary>
    private IntPtr MakeNoise()
    {
        var random = new Random(9722);
        float[] Grid(int n) { var g = new float[n * n]; for (var i = 0; i < g.Length; i++) g[i] = (float)random.NextDouble(); return g; }
        float Sample(float[] g, int n, float x, float y)
        {
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            float At(int i, int j) => g[(j % n) * n + i % n];
            return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
        }
        var coarse = Grid(8);
        var fine = Grid(32);
        var pixels = new byte[NoiseSize * NoiseSize * 4];
        for (var y = 0; y < NoiseSize; y++)
            for (var x = 0; x < NoiseSize; x++)
            {
                var value = 0.7f * Sample(coarse, 8, x * 8f / NoiseSize, y * 8f / NoiseSize) + 0.3f * Sample(fine, 32, x * 32f / NoiseSize, y * 32f / NoiseSize);
                var i = (y * NoiseSize + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0xFF;
                pixels[i + 3] = (byte)(Math.Clamp(value * 1.4f - 0.2f, 0, 1) * 255);
            }
        var texture = CreateTexture(_device, NoiseSize, NoiseSize);
        Upload(texture, new Rectangle(0, 0, NoiseSize, NoiseSize), pixels, NoiseSize * 4);
        return texture;
    }

    public void Dispose()
    {
        Release(_noise);
        _noise = IntPtr.Zero;
        ReleaseShaders();
    }

    private readonly struct FogVertex(Vector3 position, uint color, float u, float v)
    {
        public readonly float X = position.X, Y = position.Y, Z = position.Z;
        public readonly uint Color = color;
        public readonly float U = u, V = v;
    }

    private readonly struct SliceVertex(float x, float y, float z, uint color)
    {
        public readonly float X = x, Y = y, Z = z;
        public readonly uint Color = color;
    }
}
