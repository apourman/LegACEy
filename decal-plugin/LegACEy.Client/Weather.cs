using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using LegACEy.Client.Demo;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Look-only weather on the 3×3 landblocks around <see cref="Current"/>'s landblock. Snow and rain are drops in a box that follows the camera,
/// fixed in the world as they fall, so moving through them shows parallax; a drop leaving the box comes back in on the other side. Snowflakes
/// are soft camera-facing dots, raindrops streaks along their fall; both are depth-tested against retail's scene. A haze through
/// <see cref="StormFog.DrawHaze"/> closes in the distance. A thunderstorm also darkens the screen to night and strikes lightning: a jagged
/// bolt in the distance every 5 to 10 seconds, flickering, while the whole screen flashes. None of it shows indoors.
/// With shader model 3 (<c>Weather.hlsl</c>) every drop sits in one vertex buffer and the shader moves it, so a frame costs one draw
/// whatever the weather; without, the drops are moved here, half as many.
/// </summary>
internal sealed unsafe class Weather : IDisposable
{
    private const float Reach = StormFog.Block * 1.5f, FadeIn = 30;
    private const int DotSize = 16;
    private static readonly Vector3 Box = new(40, 40, 24);

    /// <param name="Streak">Seconds of a drop's fall drawn as its streak; zero draws a round flake.</param>
    /// <param name="Night">How much of the scene the night's darkness hides, 0 to 1.</param>
    private sealed record Fall(int Drops, float Speed, Vector2 Wind, float Sway, float Size, byte Alpha, uint Color, float Streak,
        uint HazeColor, float Haze, float Night, bool Lightning);

    private static readonly Fall Snow = new(2500, 1.0f, new Vector2(0.4f, 0.2f), 0.35f, 0.035f, 0xD0, 0xFFFFFF, 0, 0xDCE2EA, 900, 0, false);
    private static readonly Fall Blizzard = new(7000, 2.6f, new Vector2(7f, 2.5f), 0.6f, 0.03f, 0xE0, 0xFFFFFF, 0, 0xDCE2EA, 55, 0, false);
    private static readonly Fall Thunderstorm = new(9000, 9f, new Vector2(1.5f, 0.6f), 0, 0.012f, 0x70, 0xB8C4D4, 0.05f, 0x1C222C, 140, 0.55f, true);
    private const int MostDrops = 9000;
    private const uint NightColor = 0x080C16, FlashColor = 0xC8D2FF, BoltColor = 0xE8EEFF;

    // The shader's clock wraps this often, so a float still places a raindrop to the millimetre; the drops reshuffle once as it does.
    private const double ClockWrap = 600;

    private readonly IntPtr _device;
    private readonly StormFog _camera;
    private readonly Action<string> _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new(1722);
    private readonly Drop[] _drops;
    private QuadVertex[] _vertices = Array.Empty<QuadVertex>();
    private readonly List<Vector3[]> _bolt = new();
    // How far off the last bolt struck, which widens it so a far bolt still reads.
    private float _boltDistance;
    private double _strikeAt = double.NegativeInfinity, _nextStrike = 2;
    private IntPtr _dot;
    // The shaders and the drops' vertex buffer; _shaded is null until they are first tried, false when they failed.
    private IntPtr _vertexShader, _pixelShader, _dropBuffer;
    private bool? _shaded;

    public Weather(IntPtr device, StormFog camera, Action<string> log)
    {
        _device = device;
        _camera = camera;
        _log = log;
        _drops = new Drop[MostDrops];
        for (var i = 0; i < _drops.Length; i++)
            _drops[i] = new Drop(
                new Vector3((float)_random.NextDouble() * Box.X, (float)_random.NextDouble() * Box.Y, (float)_random.NextDouble() * Box.Z),
                0.7f + (float)_random.NextDouble() * 0.6f, (float)_random.NextDouble() * MathF.Tau, 0.6f + (float)_random.NextDouble() * 0.8f);
    }

    /// <summary>The weather and the middle landblock, or null for clear skies.</summary>
    public (WeatherKind Kind, int X, int Y)? Current { get; set; }

    /// <summary>The player's cell id; an indoor cell (low word 0x100 and up, including dungeons) has no weather.</summary>
    public uint PlayerCell { get; set; }

    /// <summary>Draws the weather after retail's world, with the camera the storm fog measured.</summary>
    public void Draw()
    {
        if (Current is not { } now || (PlayerCell & 0xFFFF) >= 0x100 || _camera.Eye is not { } eye) return;
        var fall = now.Kind switch { WeatherKind.Blizzard => Blizzard, WeatherKind.Thunderstorm => Thunderstorm, _ => Snow };
        var centre = new Vector2((now.X + 0.5f) * StormFog.Block, (now.Y + 0.5f) * StormFog.Block);
        var depth = Depth(_camera.Player ?? eye, centre);
        // Drops fall only inside the area, so from outside its edge shows; past a box's reach of it there is nothing to draw.
        if (depth < -Box.X) return;
        var strength = Math.Clamp(depth / FadeIn, 0, 1);
        if (_dot == IntPtr.Zero) _dot = MakeDot();
        _shaded ??= MakeShaders();
        var time = _clock.Elapsed.TotalSeconds;
        var flash = fall.Lightning && strength > 0 ? Lightning(time, eye) * strength : 0;
        var saved = SaveState(_device);
        try
        {
            UseFixedFunction(_device, FvfXyz | FvfDiffuse);
            _camera.UseWorldCamera();
            if (strength > 0) _camera.DrawHaze(fall.HazeColor, fall.Haze, strength);
            // Lightning lifts the night for its flash.
            if (fall.Night > 0 && strength > 0) Veil((uint)(fall.Night * strength * (1 - 0.8f * flash) * 255) << 24 | NightColor, additive: false);
            if (_shaded == true) DrawShaded(fall, eye, centre, time);
            else DrawDrops(fall, eye, centre, time);
            if (flash > 0)
            {
                DrawBolt(eye, flash);
                // A far strike lights the sky less than one overhead would.
                Veil((uint)(0.3f * flash * 255) << 24 | FlashColor, additive: true);
            }
        }
        finally { RestoreState(saved); }
    }

    private void UseDot(bool additive)
    {
        UseFixedFunction(_device, FvfXyz | FvfDiffuse | FvfTex1);
        _camera.UseWorldCamera();
        if (additive) SetRenderState(_device, RenderState.DestBlend, BlendOne);
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
    }

    /// <summary>The drops, moved by Weather.hlsl: a handful of constants and one draw.</summary>
    private void DrawShaded(Fall fall, Vector3 eye, Vector2 centre, double time)
    {
        UseDot(additive: false);
        UseShaders(_device, _vertexShader, _pixelShader, DropFormat);
        var view = _camera.View;
        var worldViewProj = Matrix4x4.CreateTranslation(_camera.Offset) * view * _camera.Projection;
        SetVertexConstants(_device, 0, System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, Vector4>(new ReadOnlySpan<Matrix4x4>(ref worldViewProj)));
        SetVertexConstants(_device, 4, new[]
        {
            new Vector4(eye, (float)(time % ClockWrap)),
            new Vector4(eye + new Vector3(0, 0, 4), 0),
            new Vector4(Box, fall.Size),
            new Vector4(fall.Wind, fall.Speed, fall.Sway),
            new Vector4(centre, Reach, fall.Streak),
            new Vector4(view.M11, view.M21, view.M31, 0),
            new Vector4(view.M12, view.M22, view.M32, 0),
            new Vector4(fall.Color >> 16 & 0xFF, fall.Color >> 8 & 0xFF, fall.Color & 0xFF, fall.Alpha) / 255,
        });
        DrawBuffer(_device, _dropBuffer, sizeof(DropVertex), TriangleList, fall.Drops * 2);
    }

    /// <summary>Compiles Weather.hlsl and writes every drop's quad into a vertex buffer; false, with the reason logged, when this machine cannot.</summary>
    private bool MakeShaders()
    {
        try
        {
            using var stream = typeof(Weather).Assembly.GetManifestResourceStream("Weather.hlsl")!;
            var source = new System.IO.StreamReader(stream).ReadToEnd();
            _vertexShader = CompileShader(_device, source, "Vs", "vs_3_0");
            _pixelShader = CompileShader(_device, source, "Ps", "ps_3_0");
            var vertices = new DropVertex[MostDrops * 6];
            ReadOnlySpan<(float X, float Y)> corners = [(0, 0), (1, 0), (0, 1), (0, 1), (1, 0), (1, 1)];
            for (var i = 0; i < vertices.Length; i++)
                vertices[i] = new DropVertex(_drops[i / 6], corners[i % 6].X, corners[i % 6].Y);
            _dropBuffer = CreateVertexBuffer<DropVertex>(_device, vertices, DropFormat);
            _log("Weather: drawn with shaders.");
            return true;
        }
        catch (Exception exception)
        {
            ReleaseShaders();
            _log($"Weather: drops moved without shaders, half as many, as the shaders did not compile: {exception.Message}");
            return false;
        }
    }

    private void ReleaseShaders()
    {
        Release(_vertexShader);
        Release(_pixelShader);
        Release(_dropBuffer);
        _vertexShader = _pixelShader = _dropBuffer = IntPtr.Zero;
    }

    private void DrawDrops(Fall fall, Vector3 eye, Vector2 centre, double time)
    {
        UseDot(additive: false);
        // The view's right and up, in render space (the world transform only translates): a flake is a quad facing the camera.
        var view = _camera.View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var eyeAt = StormFog.Render(eye);
        if (_vertices.Length < fall.Drops * 3) _vertices = new QuadVertex[fall.Drops * 3];
        var count = 0;
        // The box's middle sits a little above the camera, which mostly looks out and down.
        var middle = eye + new Vector3(0, 0, 4);
        for (var i = 0; i < fall.Drops / 2; i++)
        {
            var drop = _drops[i];
            var velocity = new Vector3(fall.Wind * drop.Speed, -fall.Speed * drop.Speed);
            var drift = new Vector3((float)(velocity.X * time % Box.X), (float)(velocity.Y * time % Box.Y), (float)(velocity.Z * time % Box.Z));
            var position = Wrap(drop.Start + drift, middle);
            var sway = (float)Math.Sin(time * 1.3 * drop.Speed + drop.Phase) * fall.Sway;
            position += new Vector3(sway, sway * 0.6f, 0);
            if (Depth(position, centre) < 0) continue;
            // Drops almost at the lens would smear across the screen; they fade out within a metre.
            var near = Math.Clamp((Vector3.Distance(position, eye) - 0.3f) / 0.7f, 0, 1);
            if (near <= 0) continue;
            var color = (uint)(fall.Alpha * near) << 24 | fall.Color;
            var at = StormFog.Render(position);
            var size = fall.Size * drop.Scale;
            if (fall.Streak > 0)
            {
                // A streak from where the drop was a moment ago to where it is, as wide as the drop, turned to face the camera.
                var trail = StormFog.Render(velocity * fall.Streak);
                var side = Vector3.Normalize(Vector3.Cross(trail, at - eyeAt)) * size;
                Ribbon(ref count, at - trail, at, side, color);
            }
            else Ribbon(ref count, at + up * size, at - up * size, right * size, color);
        }
        Flush(count);
    }

    /// <summary>One quad from a to b, side either way of the line, the dot's soft edge across it and along it.</summary>
    private void Ribbon(ref int count, Vector3 a, Vector3 b, Vector3 side, uint color)
    {
        if (count + 6 > _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2 + 6);
        fixed (QuadVertex* v = &_vertices[count])
        {
            v[0] = new(a - side, color, 0, 0);
            v[1] = new(a + side, color, 1, 0);
            v[2] = new(b - side, color, 0, 1);
            v[3] = v[2];
            v[4] = v[1];
            v[5] = new(b + side, color, 1, 1);
        }
        count += 6;
    }

    private void Flush(int count)
    {
        if (count == 0) return;
        fixed (QuadVertex* v = _vertices)
            DrawPrimitiveUP(_device, TriangleList, count / 3, v, sizeof(QuadVertex));
    }

    /// <summary>
    /// The lightning's brightness now, 0 to 1: a strike every 5 to 10 seconds flashes, dips, flashes again and fades in a third of a second.
    /// A new strike builds its bolt somewhere around the camera.
    /// </summary>
    private float Lightning(double time, Vector3 eye)
    {
        if (time >= _nextStrike)
        {
            _strikeAt = time;
            _nextStrike = time + 5 + _random.NextDouble() * 5;
            BuildBolt(eye);
        }
        var age = time - _strikeAt;
        return age switch
        {
            < 0.07 => 1,
            < 0.12 => 0.15f,
            < 0.32 => (float)(1 - (age - 0.12) / 0.2),
            _ => 0,
        };
    }

    /// <summary>
    /// A jagged bolt in the distance, 250 to 600 m off in any direction (inside the far plane), from high in the sky to below the ground,
    /// where the terrain hides it, with a branch or two.
    /// </summary>
    private void BuildBolt(Vector3 eye)
    {
        _bolt.Clear();
        var projection = _camera.Projection;
        var forward = projection.M34 >= 0 ? 1 : -1;
        var far = MathF.Abs(projection.M43 / (forward - projection.M33));
        var angle = _random.NextDouble() * Math.PI * 2;
        var distance = (float)(250 + _random.NextDouble() * 350);
        if (far > 0) distance = MathF.Min(distance, far * 0.9f);
        _boltDistance = distance;
        var foot = eye + new Vector3((float)Math.Cos(angle) * distance, (float)Math.Sin(angle) * distance, -60);
        var main = Jagged(foot + new Vector3(0, 0, 500), foot, 18, 18);
        _bolt.Add(main);
        for (var branch = _random.Next(1, 3); branch > 0; branch--)
        {
            var from = main[_random.Next(3, 9)];
            var to = from + new Vector3((float)(_random.NextDouble() - 0.5) * 160, (float)(_random.NextDouble() - 0.5) * 160, -120 - (float)_random.NextDouble() * 80);
            _bolt.Add(Jagged(from, to, 7, 10));
        }
    }

    private Vector3[] Jagged(Vector3 from, Vector3 to, int segments, float jitter)
    {
        var points = new Vector3[segments + 1];
        var offset = Vector2.Zero;
        for (var i = 0; i <= segments; i++)
        {
            if (i > 0 && i < segments)
                offset += new Vector2((float)(_random.NextDouble() - 0.5) * 2 * jitter, (float)(_random.NextDouble() - 0.5) * 2 * jitter);
            var t = i / (float)segments;
            // The wander narrows to nothing at both ends, so the bolt still meets them.
            var pull = MathF.Sin(t * MathF.PI);
            points[i] = Vector3.Lerp(from, to, t) + new Vector3(offset * pull, 0);
        }
        return points;
    }

    private void DrawBolt(Vector3 eye, float brightness)
    {
        UseDot(additive: true);
        var eyeAt = StormFog.Render(eye);
        var count = 0;
        // A wide faint glow, then the bright core, both added to the scene; widths are for a bolt 150 m off, wider further away.
        var scale = MathF.Max(1, _boltDistance / 150);
        foreach (var (width, alpha) in new[] { (7f * scale, 0.25f), (1.1f * scale, 1f) })
        {
            var color = (uint)(alpha * brightness * 255) << 24 | BoltColor;
            foreach (var line in _bolt)
                for (var i = 0; i + 1 < line.Length; i++)
                {
                    Vector3 a = StormFog.Render(line[i]), b = StormFog.Render(line[i + 1]);
                    var side = Vector3.Normalize(Vector3.Cross(b - a, a - eyeAt)) * width;
                    Ribbon(ref count, a, b, side, color);
                }
        }
        Flush(count);
    }

    /// <summary>The whole screen covered in one colour, over everything: darkening it, or added to it for a flash.</summary>
    private void Veil(uint argb, bool additive)
    {
        var screen = Viewport(_device);
        UseFixedFunction(_device, FvfXyzRhw | FvfDiffuse);
        SetTexture(_device, 0, IntPtr.Zero);
        SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
        SetStageState(_device, 0, StageState.ColorArg1, ArgDiffuse);
        SetStageState(_device, 0, StageState.AlphaOp, OpSelectArg1);
        SetStageState(_device, 0, StageState.AlphaArg1, ArgDiffuse);
        SetRenderState(_device, RenderState.ZEnable, 0);
        SetRenderState(_device, RenderState.SrcBlend, BlendSrcAlpha);
        SetRenderState(_device, RenderState.DestBlend, additive ? BlendOne : BlendInvSrcAlpha);
        float left = screen.Left - 0.5f, top = screen.Top - 0.5f, right = screen.Right - 0.5f, bottom = screen.Bottom - 0.5f;
        var quad = stackalloc ScreenVertex[4];
        quad[0] = new(left, top, argb);
        quad[1] = new(right, top, argb);
        quad[2] = new(left, bottom, argb);
        quad[3] = new(right, bottom, argb);
        DrawPrimitiveUP(_device, TriangleStrip, 2, quad, sizeof(ScreenVertex));
        SetRenderState(_device, RenderState.ZEnable, 1);
        SetRenderState(_device, RenderState.DestBlend, BlendInvSrcAlpha);
    }

    /// <summary>The copy of a point, in the box-sized repeating lattice, that falls in the box centred on the middle.</summary>
    internal static Vector3 Wrap(Vector3 position, Vector3 middle)
    {
        static float Mod(float value, float size) => value - MathF.Floor(value / size) * size;
        var from = middle - Box / 2;
        return from + new Vector3(Mod(position.X - from.X, Box.X), Mod(position.Y - from.Y, Box.Y), Mod(position.Z - from.Z, Box.Z));
    }

    /// <summary>Metres inside the weather's square to its nearest edge; negative outside it.</summary>
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
        ReleaseShaders();
    }

    private readonly record struct Drop(Vector3 Start, float Speed, float Phase, float Scale);

    private const uint DropFormat = FvfXyz | FvfTex3;

    /// <summary>One corner of a drop's quad, as Weather.hlsl reads it.</summary>
    private readonly struct DropVertex(Drop drop, float cornerX, float cornerY)
    {
        public readonly float X = drop.Start.X, Y = drop.Start.Y, Z = drop.Start.Z;
        public readonly float Speed = drop.Speed, Phase = drop.Phase;
        public readonly float Scale = drop.Scale, Unused = 0;
        public readonly float CornerX = cornerX, CornerY = cornerY;
    }

    private readonly struct QuadVertex(Vector3 position, uint color, float u, float v)
    {
        public readonly float X = position.X, Y = position.Y, Z = position.Z;
        public readonly uint Color = color;
        public readonly float U = u, V = v;
    }

    private readonly struct ScreenVertex(float x, float y, uint color)
    {
        public readonly float X = x, Y = y, Z = 0, W = 1;
        public readonly uint Color = color;
    }
}
