using System;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// A look-only preview of a battle-royale storm: the eight landblocks around one safe landblock are filled with grey fog. From outside,
/// nested translucent walls show the fog bank, hidden by the game's depth buffer behind terrain and buildings. Inside, the view fades into
/// fog with distance: camera-facing slices, each depth-tested against retail's scene, so a pixel is fogged by every slice in front of it.
/// Game positions (z-up) become render positions (y-up, relative to the viewer's landblock) through an offset measured while retail draws a part (<see cref="Calibrate"/>).
/// </summary>
internal sealed unsafe class StormFog : IDisposable
{
    // ponytail: preview constants; the server sends the zone once the storm is a real feature.
    private const int SafeX = 0x97, SafeY = 0x22;
    private const float Block = 192, Bottom = -50, Top = 450;
    private const int Layers = 5, NoiseSize = 128;
    private const uint Grey = 0xC8CDCF;
    // Inside: fog thick enough that 1/e of the scene shows through every Thickness metres, reached FadeIn metres past the edge.
    // Slices run from the first to the last distance, spaced wider with distance as the fog changes less there.
    private const float Thickness = 45, FadeIn = 30, FirstSlice = 3, LastSlice = 400;
    private const int Slices = 24;
    private static readonly (int X, int Y)[] Corners = { (-1, -1), (1, -1), (1, 1), (-1, 1) };

    private readonly IntPtr _device;
    private readonly Action<string> _log;
    private bool _logged;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IntPtr _noise;
    private Vector3 _offset;
    private Matrix4x4 _view, _projection;

    public StormFog(IntPtr device, Action<string> log)
    {
        _device = device;
        _log = log;
    }

    /// <summary>A part was measured this frame; the player's own part, the better measure, once <see cref="FromPlayer"/>.</summary>
    public bool Calibrated { get; private set; }

    public bool FromPlayer { get; private set; }

    /// <summary>Whether the camera was inside the storm at the last frame drawn.</summary>
    public bool CameraInStorm => _strength > 0;

    public float Strength => _strength;

    // How far into the storm the camera is, 0 at its edge to 1 at FadeIn metres in.
    private float _strength;
    /// <summary>Measures the game-to-render offset from a part being drawn: its game position against the device's world transform.</summary>
    public void Calibrate(int part, bool player)
    {
        var cell = *(uint*)(part + 0x7C);
        var origin = new Vector3(*(float*)(part + 0xB4), *(float*)(part + 0xB8), *(float*)(part + 0xBC));
        _offset = GetTransform(_device, TransformWorld).Translation - Render(Global(cell, origin));
        _view = GetTransform(_device, TransformView);
        _projection = GetTransform(_device, TransformProjection);
        Calibrated = true;
        FromPlayer |= player;
        if (player && !_logged)
        {
            _logged = true;
            _log($"Storm fog: the player's part at cell 0x{cell:X8} {origin} draws at {_offset + Render(Global(cell, origin))}; game-to-render offset {_offset}.");
        }
    }

    /// <summary>Draws the storm after retail's world, then waits for the next frame's measure.</summary>
    public void Draw()
    {
        if (!Calibrated) return;
        Calibrated = FromPlayer = false;
        if (_noise == IntPtr.Zero) _noise = MakeNoise();
        var saved = SaveState(_device);
        try
        {
            UseFixedFunction(_device, FvfXyz | FvfDiffuse | FvfTex1);
            SetTransform(_device, TransformWorld, Matrix4x4.CreateTranslation(_offset));
            SetTransform(_device, TransformView, _view);
            SetTransform(_device, TransformProjection, _projection);
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
            var cameraAt = Matrix4x4.Invert(_view, out var camera) ? Render(camera.Translation - _offset) : (Vector3?)null;
            var centre = new Vector2((SafeX + 0.5f) * Block, (SafeY + 0.5f) * Block);
            var walls = stackalloc FogVertex[10];
            for (var layer = 0; layer < Layers; layer++)
            {
                // The safe landblock's edge is the storm's wall, so the innermost layer is the thickest.
                var half = Block / 2 + layer * Block / (Layers - 1);
                // A wall fades out within 10 m of the camera, so passing through it does not flash it across the screen.
                var near = cameraAt is { } at ? Math.Clamp(MathF.Abs(FromCentre(at) - half) / 10, 0, 1) : 1;
                var color = (uint)((layer == 0 ? 0x90 : 0x40) * near) << 24 | Grey;
                var drift = (layer % 2 == 0 ? 1 : -1) * time * 0.03f;
                for (var corner = 0; corner <= 4; corner++)
                {
                    var (sx, sy) = Corners[corner % 4];
                    float x = centre.X + sx * half, y = centre.Y + sy * half;
                    var u = corner * half * 2 / 96 + drift;
                    walls[corner * 2] = new(Render(new Vector3(x, y, Bottom)), color, u, time * 0.02f);
                    walls[corner * 2 + 1] = new(Render(new Vector3(x, y, Top)), color, u, (Top - Bottom) / 128 + time * 0.02f);
                }
                DrawPrimitiveUP(_device, TriangleStrip, 8, walls, sizeof(FogVertex));
            }

            _strength = cameraAt is { } where ? Math.Clamp(StormDepth(where) / FadeIn, 0, 1) : 0;
            if (CameraInStorm) DrawSlices();
        }
        finally { RestoreState(saved); }
    }

    /// <summary>
    /// The fog inside: camera-facing grey slices from near to far, each covering the screen and hidden where retail's scene is nearer.
    /// A slice's alpha is the fog over the gap since the last one, so a pixel behind every slice to distance d is fogged 1 - e^(-d / Thickness).
    /// </summary>
    private void DrawSlices()
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
        var forward = _projection.M34 >= 0 ? 1 : -1;
        // The last slice stays inside the far plane (from the projection: far = M43 / (forward - M33)), or the sky behind would show.
        var lastSlice = MathF.Min(LastSlice, MathF.Abs(_projection.M43 / (forward - _projection.M33)) * 0.98f);
        var quad = stackalloc SliceVertex[4];
        var last = 0f;
        for (var i = 0; i < Slices; i++)
        {
            var distance = FirstSlice * MathF.Pow(lastSlice / FirstSlice, i / (Slices - 1f));
            var alpha = 1 - MathF.Exp(-(distance - last) / Thickness * _strength);
            last = distance;
            // Wider than the view, so the slice covers the screen whatever the aspect.
            float x = distance / _projection.M11 * 1.2f, y = distance / _projection.M22 * 1.2f, z = distance * forward;
            var color = (uint)(alpha * 255) << 24 | Grey;
            quad[0] = new(-x, y, z, color);
            quad[1] = new(x, y, z, color);
            quad[2] = new(-x, -y, z, color);
            quad[3] = new(x, -y, z, color);
            DrawPrimitiveUP(_device, TriangleStrip, 2, quad, sizeof(SliceVertex));
        }
    }

    /// <summary>Metres inside the storm ring to its nearer edge (the safe landblock's, or the ring's outer one); negative outside it.</summary>
    internal static float StormDepth(Vector3 position)
    {
        var fromCentre = FromCentre(position);
        return MathF.Min(fromCentre - Block / 2, Block * 1.5f - fromCentre);
    }

    /// <summary>Distance from the safe landblock's centre, measured square: the half-size of the wall square through the position.</summary>
    private static float FromCentre(Vector3 position) =>
        MathF.Max(MathF.Abs(position.X - (SafeX + 0.5f) * Block), MathF.Abs(position.Y - (SafeY + 0.5f) * Block));

    /// <summary>Game space is z-up; retail renders y-up, swapping y and z (its own inverse).</summary>
    private static Vector3 Render(Vector3 game) => new(game.X, game.Z, game.Y);

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
