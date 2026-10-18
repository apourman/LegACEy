using System;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using LegACEy.Client.Demo;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Look-only snow on the 3×3 landblocks around <see cref="Snow"/>'s landblock: flakes in a box that follows the camera, fixed in the world as
/// they fall, so moving through them shows parallax; a flake leaving the box comes back in on the other side. Each is a soft camera-facing quad
/// depth-tested against retail's scene. A blizzard adds a white haze through <see cref="StormFog.DrawHaze"/>. None of it shows indoors.
/// </summary>
internal sealed unsafe class Snowfall : IDisposable
{
    private const float Reach = StormFog.Block * 1.5f, FadeIn = 30;
    private const int DotSize = 16;
    private static readonly Vector3 Box = new(40, 40, 24);

    private sealed record Weather(int Flakes, float Fall, Vector2 Wind, float Sway, float Size, byte Alpha, float Haze);

    private static readonly Weather Light = new(2500, 1.0f, new Vector2(0.4f, 0.2f), 0.35f, 0.035f, 0xD0, 900);
    private static readonly Weather Blizzard = new(7000, 2.6f, new Vector2(7f, 2.5f), 0.6f, 0.03f, 0xE0, 55);
    private const uint HazeColor = 0xDCE2EA;

    private readonly IntPtr _device;
    private readonly StormFog _camera;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Flake[] _flakes;
    private QuadVertex[] _vertices = Array.Empty<QuadVertex>();
    private IntPtr _dot;

    public Snowfall(IntPtr device, StormFog camera)
    {
        _device = device;
        _camera = camera;
        var random = new Random(1722);
        _flakes = new Flake[Blizzard.Flakes];
        for (var i = 0; i < _flakes.Length; i++)
            _flakes[i] = new Flake(
                new Vector3((float)random.NextDouble() * Box.X, (float)random.NextDouble() * Box.Y, (float)random.NextDouble() * Box.Z),
                0.7f + (float)random.NextDouble() * 0.6f, (float)random.NextDouble() * MathF.Tau, 0.6f + (float)random.NextDouble() * 0.8f);
    }

    /// <summary>How hard it snows and the middle landblock, or null for no snow.</summary>
    public (SnowIntensity Intensity, int X, int Y)? Snow { get; set; }

    /// <summary>The player's cell id; an indoor cell (low word 0x100 and up, including dungeons) has no snow.</summary>
    public uint PlayerCell { get; set; }

    /// <summary>Draws the snow after retail's world, with the camera the storm fog measured.</summary>
    public void Draw()
    {
        if (Snow is not { } snow || (PlayerCell & 0xFFFF) >= 0x100 || _camera.Eye is not { } eye) return;
        var weather = snow.Intensity == SnowIntensity.Blizzard ? Blizzard : Light;
        var centre = new Vector2((snow.X + 0.5f) * StormFog.Block, (snow.Y + 0.5f) * StormFog.Block);
        var strength = Math.Clamp(Depth(eye, centre) / FadeIn, 0, 1);
        // Flakes fall only inside the area, so from outside its edge shows; past a box's reach of it there is nothing to draw.
        if (Depth(eye, centre) < -Box.X) return;
        if (_dot == IntPtr.Zero) _dot = MakeDot();
        var saved = SaveState(_device);
        try
        {
            UseFixedFunction(_device, FvfXyz | FvfDiffuse);
            _camera.UseWorldCamera();
            if (strength > 0) _camera.DrawHaze(HazeColor, weather.Haze, strength);
            DrawFlakes(weather, eye, centre);
        }
        finally { RestoreState(saved); }
    }

    private void DrawFlakes(Weather weather, Vector3 eye, Vector2 centre)
    {
        UseFixedFunction(_device, FvfXyz | FvfDiffuse | FvfTex1);
        _camera.UseWorldCamera();
        SetTexture(_device, 0, _dot);
        SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
        SetStageState(_device, 0, StageState.ColorArg1, ArgDiffuse);
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
        SetSamplerState(_device, 0, SamplerState.AddressU, AddressClamp);
        SetSamplerState(_device, 0, SamplerState.AddressV, AddressClamp);

        // The view's right and up, in render space (the world transform only translates): each flake is a quad facing the camera.
        var view = _camera.View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var time = _clock.Elapsed.TotalSeconds;
        if (_vertices.Length < weather.Flakes * 6) _vertices = new QuadVertex[weather.Flakes * 6];
        var count = 0;
        // The box's middle sits a little above the camera, which mostly looks out and down.
        var middle = eye + new Vector3(0, 0, 4);
        for (var i = 0; i < weather.Flakes; i++)
        {
            var flake = _flakes[i];
            var drift = new Vector3(
                (float)(weather.Wind.X * flake.Speed * time % Box.X), (float)(weather.Wind.Y * flake.Speed * time % Box.Y),
                (float)(-weather.Fall * flake.Speed * time % Box.Z));
            var position = Wrap(flake.Start + drift, middle);
            var sway = (float)Math.Sin(time * 1.3 * flake.Speed + flake.Phase) * weather.Sway;
            position += new Vector3(sway, sway * 0.6f, 0);
            if (Depth(position, centre) < 0) continue;
            // Flakes almost at the lens would smear across the screen; they fade out within a metre.
            var near = Math.Clamp((Vector3.Distance(position, eye) - 0.3f) / 0.7f, 0, 1);
            if (near <= 0) continue;
            var color = (uint)(weather.Alpha * near) << 24 | 0xFFFFFF;
            var at = StormFog.Render(position);
            var size = weather.Size * flake.Scale;
            Vector3 r = right * size, u = up * size;
            fixed (QuadVertex* v = &_vertices[count])
            {
                v[0] = new(at - r + u, color, 0, 0);
                v[1] = new(at + r + u, color, 1, 0);
                v[2] = new(at - r - u, color, 0, 1);
                v[3] = v[2];
                v[4] = v[1];
                v[5] = new(at + r - u, color, 1, 1);
            }
            count += 6;
        }
        if (count == 0) return;
        fixed (QuadVertex* v = _vertices)
            DrawPrimitiveUP(_device, TriangleList, count / 3, v, sizeof(QuadVertex));
    }

    /// <summary>The copy of a point, in the box-sized repeating lattice, that falls in the box centred on the middle.</summary>
    internal static Vector3 Wrap(Vector3 position, Vector3 middle)
    {
        static float Mod(float value, float size) => value - MathF.Floor(value / size) * size;
        var from = middle - Box / 2;
        return from + new Vector3(Mod(position.X - from.X, Box.X), Mod(position.Y - from.Y, Box.Y), Mod(position.Z - from.Z, Box.Z));
    }

    /// <summary>Metres inside the snow's square to its nearest edge; negative outside it.</summary>
    internal static float Depth(Vector3 position, Vector2 centre) =>
        Reach - MathF.Max(MathF.Abs(position.X - centre.X), MathF.Abs(position.Y - centre.Y));

    /// <summary>A soft white dot: alpha falls from the middle to nothing at the edge.</summary>
    private IntPtr MakeDot()
    {
        var pixels = new byte[DotSize * DotSize * 4];
        for (var y = 0; y < DotSize; y++)
            for (var x = 0; x < DotSize; x++)
            {
                float dx = (x + 0.5f) / DotSize * 2 - 1, dy = (y + 0.5f) / DotSize * 2 - 1;
                var fall = Math.Clamp(1 - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                var i = (y * DotSize + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0xFF;
                pixels[i + 3] = (byte)(fall * fall * 255);
            }
        var texture = CreateTexture(_device, DotSize, DotSize);
        Upload(texture, new Rectangle(0, 0, DotSize, DotSize), pixels, DotSize * 4);
        return texture;
    }

    public void Dispose()
    {
        Release(_dot);
        _dot = IntPtr.Zero;
    }

    private readonly record struct Flake(Vector3 Start, float Speed, float Phase, float Scale);

    private readonly struct QuadVertex(Vector3 position, uint color, float u, float v)
    {
        public readonly float X = position.X, Y = position.Y, Z = position.Z;
        public readonly uint Color = color;
        public readonly float U = u, V = v;
    }
}
