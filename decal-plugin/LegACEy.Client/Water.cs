using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegACEy.Client.GameArt;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Livelier water over retail's still water texture. With shader model 3 (<c>Water.hlsl</c>, compiled in game) each pixel gets its own wave
/// normal, fresnel, sun glint and foam at the shore; when the shaders do not compile, the fixed-function look below is drawn instead.
/// Fixed-function: The water cells of the landblocks around the player (from client_cell_1.dat, where a
/// vertex's terrain type is 16 to 20) are drawn again just above retail's ground, blended to multiply what retail drew by twice their colour:
/// mid grey leaves it as it was, so retail's lighting (day, night, shade, fog) still holds and nothing glows in the dark. Two layers of one
/// small wave texture scroll across each other for ripples and glints; open water is tinted deeper blue, the shallows greener, and water seen at a
/// low angle brighter, as it reflects more of the sky. It fades into retail's water at the shore and towards <see cref="Reach"/>, and the ripples
/// also fade with distance through the texture's mip levels. None of it shows indoors.
/// </summary>
internal sealed unsafe class Water : IDisposable
{
    private const int Radius = 2;            // landblocks loaded each way from the player's
    private const float Lift = 0.12f;        // metres above retail's ground, so it is not lost to depth fighting
    private const float Reach = 300, FadeOut = 80;
    private const int WaveSize = 128;
    private const float TileA = 40, TileB = 17; // metres per repeat of each wave layer
    private const float Cell = 24;
    // With the shaders, water triangles starting within this many metres are cut into Steps² smaller ones, for the swells to move
    // (the shader fades the swells out by SwellReach - Cell × 1.5, so the uncut triangles further off stay flat). About 1 MB of
    // vertices on open sea. ponytail: cut per frame; keep the cut landblocks if it shows in the frame time.
    private const float SwellReach = 140;
    private const int Steps = 6;
    private static readonly Vector3 Deep = new(0.62f, 0.85f, 1.00f), Shallow = new(0.80f, 1.00f, 0.92f);
    // Towards the sun, in render space (x east, y up, z north): south-east, 35° up.
    // ponytail: a fixed sun; read retail's sky if glints show where the sun is not.
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.55f, 0.7f, -0.45f));

    private readonly IntPtr _device;
    private readonly StormFog _camera;
    private readonly PortalDat _portal;
    private readonly Action<string> _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    // Landblock (x << 8 | y) to its water triangles; null for a landblock with none.
    private readonly Dictionary<int, WaterPoint[]?> _blocks = new();
    private readonly HashSet<int> _loading = new();
    private readonly ConcurrentQueue<(int Key, WaterPoint[]? Points)> _arrived = new();
    private Task<GroundDat?>? _ground;
    private int _centre = -1;
    // The landblock the last 'Water:' log line was for, so each move into a landblock is logged once its water is in.
    private int _logged = -1;
    private WaterVertex[] _vertices = Array.Empty<WaterVertex>();
    private IntPtr _waves;
    private readonly WaterPoint[] _cut = new WaterPoint[Steps * Steps * 3];
    // The shaders (vertex, tint pass, light pass); _shaded is null until they are first tried, false when they failed.
    private IntPtr _vertexShader, _tintShader, _lightShader;
    private bool? _shaded;

    public Water(IntPtr device, StormFog camera, PortalDat portal, Action<string> log)
    {
        _device = device;
        _camera = camera;
        _portal = portal;
        _log = log;
    }

    /// <summary>The player's cell id; an indoor cell (low word 0x100 and up, including dungeons) has no water drawn.</summary>
    public uint PlayerCell { get; set; }

    /// <summary>How strong the effect is (<see cref="WaterChoice.Strength"/>): 0 leaves retail's water, 1 is the look as designed, 2 twice it.</summary>
    public float Strength { get; set; } = 1;

    /// <summary>Draws the water after retail's world, with the camera the storm fog measured.</summary>
    public void Draw()
    {
        if (Strength <= 0 || PlayerCell == 0 || (PlayerCell & 0xFFFF) >= 0x100 || _camera.Eye is not { } eye) return;
        Follow((int)(PlayerCell >> 24), (int)((PlayerCell >> 16) & 0xFF));
        _shaded ??= MakeShaders();

        // Texture coordinates from the player's landblock corner, so they stay small; the scroll wraps, as the texture repeats.
        float originX = (_centre >> 8) * StormFog.Block, originY = (_centre & 0xFF) * StormFog.Block;
        var time = _clock.Elapsed.TotalSeconds;
        var scroll = new Vector4(Wrap(time * 0.010), Wrap(time * 0.006), Wrap(time * -0.017), Wrap(time * 0.013));
        var count = 0;
        foreach (var points in _blocks.Values)
        {
            if (points == null) continue;
            for (var i = 0; i < points.Length; i += 3)
            {
                // A triangle is kept or dropped whole, by its first corner: cells are small next to the reach.
                var corner = points[i].Position;
                var distance = Vector2.Distance(new Vector2(corner.X, corner.Y), new Vector2(eye.X, eye.Y));
                if (distance > Reach + Cell) continue;
                var cut = _shaded == true && distance < SwellReach;
                var triangles = cut ? Subdivide(points[i], points[i + 1], points[i + 2], _cut) : 1;
                if (_vertices.Length < count + triangles * 3) Array.Resize(ref _vertices, Math.Max(count + triangles * 3, _vertices.Length * 2));
                for (var k = 0; k < triangles * 3; k++)
                    _vertices[count++] = Vertex(cut ? _cut[k] : points[i + k], eye, originX, originY, scroll, Strength);
            }
        }
        if (_logged != _centre && _loading.Count == 0)
        {
            _logged = _centre;
            _log($"Water: landblock {_centre:X4}, {_blocks.Values.Count(points => points != null)} of {_blocks.Count} landblocks near have water, {count / 3} triangles drawn.");
        }
        if (count == 0) return;
        if (_waves == IntPtr.Zero) _waves = MakeWaves(_shaded == true);

        var saved = SaveState(_device);
        try
        {
            _camera.UseWorldCamera();
            if (_shaded == true) DrawShaded(count, eye, (float)time);
            else DrawFixed(count);
        }
        finally { RestoreState(saved); }
    }

    /// <summary>The two shader passes over the water triangles; see Water.hlsl.</summary>
    private void DrawShaded(int count, Vector3 eye, float time)
    {
        var worldViewProj = Matrix4x4.CreateTranslation(_camera.Offset) * _camera.View * _camera.Projection;
        // Its rows, as the shader's row_major matrix.
        SetVertexConstants(_device, 0, System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, Vector4>(new ReadOnlySpan<Matrix4x4>(ref worldViewProj)));
        SetPixelConstants(_device, 0, new[]
        {
            new Vector4(StormFog.Render(eye), 0), new Vector4(Sun, Math.Max(Strength, 1)), new Vector4(Deep, 1), new Vector4(Shallow, 1),
            new Vector4(time % 3600, 0, 0, 0)
        });
        SetVertexConstants(_device, 4, new[] { new Vector4(StormFog.Render(eye), 0), new Vector4(time % 3600, Strength, SwellReach - Cell * 1.5f, 0) });
        SetTexture(_device, 0, _waves);
        SetSamplerState(_device, 0, SamplerState.MinFilter, FilterLinear);
        SetSamplerState(_device, 0, SamplerState.MagFilter, FilterLinear);
        SetSamplerState(_device, 0, SamplerState.MipFilter, FilterLinear);
        SetSamplerState(_device, 0, SamplerState.AddressU, AddressWrap);
        SetSamplerState(_device, 0, SamplerState.AddressV, AddressWrap);
        fixed (WaterVertex* vertices = _vertices)
        {
            // Tint: 2 × output × retail's colour.
            UseShaders(_device, _vertexShader, _tintShader, Format);
            SetRenderState(_device, RenderState.SrcBlend, BlendDestColor);
            SetRenderState(_device, RenderState.DestBlend, BlendSrcColor);
            DrawPrimitiveUP(_device, TriangleList, count / 3, vertices, sizeof(WaterVertex));
            // Light: retail's colour × (1 + output).
            UseShaders(_device, _vertexShader, _lightShader, Format);
            SetRenderState(_device, RenderState.DestBlend, BlendOne);
            DrawPrimitiveUP(_device, TriangleList, count / 3, vertices, sizeof(WaterVertex));
        }
    }

    private const uint Format = FvfXyz | FvfDiffuse | FvfTex3;

    /// <summary>Compiles Water.hlsl; false, with the reason logged, when this machine cannot (no d3dcompiler_47, or no shader model 3).</summary>
    private bool MakeShaders()
    {
        try
        {
            using var stream = typeof(Water).Assembly.GetManifestResourceStream("Water.hlsl")!;
            var source = new System.IO.StreamReader(stream).ReadToEnd();
            _vertexShader = CompileShader(_device, source, "Vs", "vs_3_0");
            _tintShader = CompileShader(_device, source, "Tint", "ps_3_0");
            _lightShader = CompileShader(_device, source, "Light", "ps_3_0");
            _log("Water: drawn with shaders.");
            return true;
        }
        catch (Exception exception)
        {
            ReleaseShaders();
            _log($"Water: drawn without shaders, as they did not compile: {exception.Message}");
            return false;
        }
    }

    private void ReleaseShaders()
    {
        Release(_vertexShader);
        Release(_tintShader);
        Release(_lightShader);
        _vertexShader = _tintShader = _lightShader = IntPtr.Zero;
    }

    /// <summary>The fixed-function look, for machines without the shaders.</summary>
    private void DrawFixed(int count)
    {
        UseFixedFunction(_device, Format);
        // Result = 2 × water × retail's colour: mid grey leaves retail's water as it was.
        SetRenderState(_device, RenderState.SrcBlend, BlendDestColor);
        SetRenderState(_device, RenderState.DestBlend, BlendSrcColor);
        SetRenderState(_device, RenderState.TextureFactor, unchecked((int)0xFF808080));
        for (var stage = 0; stage < 2; stage++)
        {
            SetTexture(_device, stage, _waves);
            SetStageState(_device, stage, StageState.TexCoordIndex, stage);
            SetStageState(_device, stage, StageState.TextureTransformFlags, 0);
            SetSamplerState(_device, stage, SamplerState.MinFilter, FilterLinear);
            SetSamplerState(_device, stage, SamplerState.MagFilter, FilterLinear);
            SetSamplerState(_device, stage, SamplerState.MipFilter, FilterLinear);
            SetSamplerState(_device, stage, SamplerState.AddressU, AddressWrap);
            SetSamplerState(_device, stage, SamplerState.AddressV, AddressWrap);
        }
        SetTexture(_device, 2, IntPtr.Zero);
        SetTexture(_device, 3, IntPtr.Zero);
        // The two wave layers, each centred on mid grey, added.
        SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
        SetStageState(_device, 0, StageState.ColorArg1, ArgTexture);
        SetStageState(_device, 1, StageState.ColorOp, OpAddSigned);
        SetStageState(_device, 1, StageState.ColorArg1, ArgTexture);
        SetStageState(_device, 1, StageState.ColorArg2, ArgCurrent);
        // Tinted and brightened by the vertex colour (mid grey keeps the waves as they are).
        SetStageState(_device, 2, StageState.ColorOp, OpModulate2x);
        SetStageState(_device, 2, StageState.ColorArg1, ArgCurrent);
        SetStageState(_device, 2, StageState.ColorArg2, ArgDiffuse);
        // Faded to mid grey (no change) by the vertex alpha: at the shore and far off.
        SetStageState(_device, 3, StageState.ColorOp, OpBlendDiffuseAlpha);
        SetStageState(_device, 3, StageState.ColorArg1, ArgCurrent);
        SetStageState(_device, 3, StageState.ColorArg2, ArgFactor);
        for (var stage = 0; stage < 4; stage++)
        {
            SetStageState(_device, stage, StageState.AlphaOp, OpSelectArg1);
            SetStageState(_device, stage, StageState.AlphaArg1, ArgDiffuse);
        }
        SetStageState(_device, 4, StageState.ColorOp, OpDisable);
        SetStageState(_device, 4, StageState.AlphaOp, OpDisable);
        fixed (WaterVertex* vertices = _vertices)
            DrawPrimitiveUP(_device, TriangleList, count / 3, vertices, sizeof(WaterVertex));
    }

    private static WaterVertex Vertex(WaterPoint point, Vector3 eye, float originX, float originY, Vector4 scroll, float strength)
    {
        var p = point.Position;
        // Fresnel (Schlick's): water reflects more of the sky the lower the angle it is seen at.
        var facing = Math.Clamp(Vector3.Normalize(eye - p).Z, 0, 1);
        var fresnel = 0.02f + 0.98f * MathF.Pow(1 - facing, 5);
        var colour = Vector3.Lerp(Shallow, Deep, point.Open) * (0.5f + 0.35f * fresnel);
        var distance = Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(eye.X, eye.Y));
        // Below full strength the effect fades towards retail's water; above it the tint moves further from mid grey (no change).
        var alpha = point.Wet * Math.Clamp((Reach - distance) / FadeOut, 0, 1) * Math.Min(strength, 1);
        if (strength > 1) colour = new Vector3(0.5f) + (colour - new Vector3(0.5f)) * strength;
        uint Byte(float value) => (uint)Math.Clamp(value * 255, 0, 255);
        var argb = Byte(alpha) << 24 | Byte(colour.X) << 16 | Byte(colour.Y) << 8 | Byte(colour.Z);
        float x = p.X - originX, y = p.Y - originY;
        return new WaterVertex(StormFog.Render(new Vector3(p.X, p.Y, p.Z + Lift)), argb,
            x / TileA + scroll.X, y / TileA + scroll.Y, x / TileB + scroll.Z, y / TileB + scroll.W, point.Wet, point.Open);
    }

    /// <summary>Cuts a triangle into Steps² smaller ones, three corners each, into <paramref name="into"/>; returns how many.</summary>
    internal static int Subdivide(WaterPoint a, WaterPoint b, WaterPoint c, WaterPoint[] into)
    {
        WaterPoint At(int row, int column)
        {
            float u = (float)column / Steps, v = (float)row / Steps, w = 1 - u - v;
            return new(a.Position * w + b.Position * u + c.Position * v, a.Wet * w + b.Wet * u + c.Wet * v, a.Open * w + b.Open * u + c.Open * v);
        }
        var n = 0;
        for (var row = 0; row < Steps; row++)
            for (var column = 0; column < Steps - row; column++)
            {
                into[n++] = At(row, column);
                into[n++] = At(row, column + 1);
                into[n++] = At(row + 1, column);
                if (column == Steps - row - 1) continue;
                into[n++] = At(row, column + 1);
                into[n++] = At(row + 1, column + 1);
                into[n++] = At(row + 1, column);
            }
        return n / 3;
    }

    private static float Wrap(double value) => (float)(value - Math.Floor(value));

    /// <summary>Takes in the landblocks read since the last frame and, when the player has moved to another landblock, asks for the ones now near.</summary>
    private void Follow(int x, int y)
    {
        while (_arrived.TryDequeue(out var arrived))
        {
            _loading.Remove(arrived.Key);
            _blocks[arrived.Key] = arrived.Points;
        }
        var centre = x << 8 | y;
        if (centre == _centre) return;
        _centre = centre;
        foreach (var far in _blocks.Keys.Where(key => Math.Max(Math.Abs((key >> 8) - x), Math.Abs((key & 0xFF) - y)) > Radius + 1).ToArray())
            _blocks.Remove(far);
        _ground ??= Task.Run(() =>
        {
            try { return new GroundDat(_portal); }
            catch (Exception exception)
            {
                _log($"Water is left as retail draws it: the ground's DATs did not open: {exception.Message}");
                return null;
            }
        });
        for (var dx = -Radius; dx <= Radius; dx++)
            for (var dy = -Radius; dy <= Radius; dy++)
            {
                int bx = x + dx, by = y + dy, key = bx << 8 | by;
                if (bx is < 0 or > 254 || by is < 0 or > 254 || _blocks.ContainsKey(key) || !_loading.Add(key)) continue;
                _ground.ContinueWith(ground =>
                {
                    WaterPoint[]? points = null;
                    try
                    {
                        if (ground.Result?.ReadVertices(bx, by) is { } vertices && Triangles(vertices) is { Length: > 0 } found) points = found;
                    }
                    catch (Exception exception) { _log($"Landblock {bx:X2}{by:X2}'s water is left as retail draws it: {exception.Message}"); }
                    _arrived.Enqueue((key, points));
                }, TaskScheduler.Default);
            }
    }

    /// <summary>
    /// The landblock's water: the triangles, three corners each in global game metres (z up), of every cell with a water corner, split along
    /// retail's diagonal. A corner is wet when it is water; its openness is how much of the 3 × 3 vertices around it (within the landblock) are.
    /// </summary>
    internal static WaterPoint[] Triangles(GroundLandblock ground)
    {
        var wet = ground.Terrain.Select(terrain => ((terrain >> 2) & 0x1F) is >= 16 and <= 20).ToArray();
        float Open(int x, int y)
        {
            int all = 0, water = 0;
            for (var i = Math.Max(0, x - 1); i <= Math.Min(8, x + 1); i++)
                for (var j = Math.Max(0, y - 1); j <= Math.Min(8, y + 1); j++)
                {
                    all++;
                    if (wet[i * 9 + j]) water++;
                }
            return (float)water / all;
        }
        WaterPoint Corner(int x, int y) => new(new Vector3(ground.X * StormFog.Block + x * Cell, ground.Y * StormFog.Block + y * Cell, ground.Height[x * 9 + y]),
            wet[x * 9 + y] ? 1 : 0, Open(x, y));

        var points = new List<WaterPoint>();
        for (var x = 0; x < 8; x++)
            for (var y = 0; y < 8; y++)
            {
                if (!wet[x * 9 + y] && !wet[(x + 1) * 9 + y] && !wet[x * 9 + y + 1] && !wet[(x + 1) * 9 + y + 1]) continue;
                WaterPoint sw = Corner(x, y), se = Corner(x + 1, y), nw = Corner(x, y + 1), ne = Corner(x + 1, y + 1);
                points.AddRange(SplitsNorthWest(ground.X, ground.Y, x, y) ? new[] { nw, se, sw, nw, ne, se } : new[] { ne, se, sw, ne, sw, nw });
            }
        return points.ToArray();
    }

    /// <summary>Retail's diagonal for a ground cell, as ACE's LandblockMesh.GetSplitDir: true cuts from the north-west to the south-east corner.</summary>
    internal static bool SplitsNorthWest(int landblockX, int landblockY, int cellX, int cellY)
    {
        uint x = (uint)(landblockX * 8 + cellX), y = (uint)(landblockY * 8 + cellY);
        var dw = unchecked(x * y * 0x0CCAC033u - x * 0x421BE3BDu + y * 0x6C1AC587u - 0x519B8F25u);
        return (dw & 0x80000000) == 0;
    }

    /// <summary>
    /// The wave texture, about 85 KB with its mip levels, which average the waves away so far water goes calm without shimmering. For the
    /// shaders it holds the waves' slope east and north (red, green) and height (blue), each centred on mid grey; without them, a grey shading
    /// (<see cref="WavePixels"/>).
    /// </summary>
    private IntPtr MakeWaves(bool shaded)
    {
        var levels = 1;
        while (WaveSize >> levels > 0) levels++;
        float[][] channels;
        if (shaded)
        {
            var (east, north, height) = WaveField();
            channels = new[] { east, north, height };
        }
        else
        {
            var grey = WavePixels();
            channels = new[] { grey, grey, grey };
        }
        var texture = CreateTexture(_device, WaveSize, WaveSize, levels);
        for (int level = 0, size = WaveSize; level < levels; level++, size /= 2)
        {
            if (level > 0) channels = channels.Select(channel => Halve(channel, size * 2)).ToArray();
            var bytes = new byte[size * size * 4];
            for (var i = 0; i < size * size; i++)
            {
                for (var c = 0; c < 3; c++) bytes[i * 4 + 2 - c] = (byte)Math.Clamp(channels[c][i] * 255, 0, 255); // BGRA in memory
                bytes[i * 4 + 3] = 0xFF;
            }
            Upload(texture, new Rectangle(0, 0, size, size), bytes, size * 4, level);
        }
        return texture;
    }

    /// <summary>The grey shading: lit from one side by the waves' slope, with sparks on the brightest crests for glints.</summary>
    internal static float[] WavePixels()
    {
        var (east, north, _) = WaveField();
        var light = Vector2.Normalize(new Vector2(0.6f, 0.8f));
        var slope = east.Zip(north, (x, y) => (x - 0.5f) * light.X + (y - 0.5f) * light.Y).ToArray();
        var most = slope.Max(MathF.Abs);
        return slope.Select(s => s / most).Select(s => 0.5f + 0.15f * s + (s > 0.65f ? (s - 0.65f) * 0.9f : 0)).ToArray();
    }

    /// <summary>A few crossing waves that repeat across the texture: their slope east, north and their height, each 0 to 1 around 0.5.</summary>
    internal static (float[] East, float[] North, float[] Height) WaveField()
    {
        // Whole numbers of waves across the texture, so it repeats without a seam.
        (int X, int Y, float Amplitude, float Phase)[] waves = { (1, 2, 1f, 0.3f), (3, -1, 0.8f, 1.9f), (-2, 3, 0.7f, 4.1f), (4, 1, 0.5f, 2.6f), (5, -3, 0.35f, 5.2f), (-1, 6, 0.3f, 0.8f) };
        float[] east = new float[WaveSize * WaveSize], north = new float[east.Length], height = new float[east.Length];
        for (var y = 0; y < WaveSize; y++)
            for (var x = 0; x < WaveSize; x++)
            {
                float u = (float)x / WaveSize, v = (float)y / WaveSize;
                var i = y * WaveSize + x;
                foreach (var (wx, wy, amplitude, phase) in waves)
                {
                    var angle = MathF.Tau * (wx * u + wy * v) + phase;
                    east[i] += amplitude * MathF.Cos(angle) * wx;
                    north[i] += amplitude * MathF.Cos(angle) * wy;
                    height[i] += amplitude * MathF.Sin(angle);
                }
            }
        float[] Centre(float[] values)
        {
            var most = values.Max(MathF.Abs);
            return values.Select(value => 0.5f + 0.5f * value / most).ToArray();
        }
        var steepest = Math.Max(east.Max(MathF.Abs), north.Max(MathF.Abs));
        // One scale for both slopes, so their directions hold.
        return (east.Select(value => 0.5f + 0.5f * value / steepest).ToArray(), north.Select(value => 0.5f + 0.5f * value / steepest).ToArray(), Centre(height));
    }

    private static float[] Halve(float[] pixels, int size)
    {
        var half = size / 2;
        var result = new float[half * half];
        for (var y = 0; y < half; y++)
            for (var x = 0; x < half; x++)
                result[y * half + x] = (pixels[2 * y * size + 2 * x] + pixels[2 * y * size + 2 * x + 1] + pixels[(2 * y + 1) * size + 2 * x] + pixels[(2 * y + 1) * size + 2 * x + 1]) / 4;
        return result;
    }

    public void Dispose()
    {
        Release(_waves);
        _waves = IntPtr.Zero;
        ReleaseShaders();
        _ground?.ContinueWith(ground => ground.Result?.Dispose(), TaskScheduler.Default);
        _ground = null;
    }

    private readonly struct WaterVertex(Vector3 position, uint color, float u0, float v0, float u1, float v1, float wet, float open)
    {
        public readonly float X = position.X, Y = position.Y, Z = position.Z;
        public readonly uint Color = color;
        public readonly float U0 = u0, V0 = v0, U1 = u1, V1 = v1, Wet = wet, Open = open;
    }
}

/// <summary>
/// The player's /water setting, kept across sessions: on or off, and the strength in percent (0 is retail's water, 100 the look as designed,
/// 200 the most). Turning it on again keeps the strength it had.
/// </summary>
internal readonly record struct WaterChoice(bool On, int Percent)
{
    public const int Most = 200;
    public static readonly WaterChoice Default = new(true, 100);

    public float Strength => On ? Percent / 100f : 0;

    /// <summary>The setting after <c>/water argument</c>: on, off or a percent (a trailing % allowed); null for anything else. No argument keeps it.</summary>
    public WaterChoice? Apply(string argument) => argument.Trim().TrimEnd('%').ToLowerInvariant() switch
    {
        "" => this,
        "on" => this with { On = true },
        "off" => this with { On = false },
        var number when int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent <= Most => new WaterChoice(true, percent),
        _ => null
    };

    public override string ToString() => On ? $"Water: on at {Percent}%." : $"Water: off ({Percent}% when on).";
}

/// <summary>A corner of a water triangle: global game metres (z up), 1 where the corner is water (0 on the shore), and how open the water around it is.</summary>
internal readonly record struct WaterPoint(Vector3 Position, float Wet, float Open);
