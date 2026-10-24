using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Radar pings shown in the world, in the sender's radar colour, for as long as the radar shows the ping; each sender has one, as on the
/// radar. A pillar of light stands on the spot, turned to face the camera, brightest at its foot and fading upwards, with a ring on the
/// ground that sends out a pulse. Both glow (added to the scene) and are drawn twice with the storm fog's camera: dimmer where something
/// stands in front of them, so they show through walls and terrain, and in full where nothing does. Indoors, once the landblock's plan is
/// read (<see cref="Scan"/>), the walls around the spot on its level glow through everything, a wave sweeping out from the spot over them.
/// </summary>
internal sealed class PingBeams : IDisposable
{
    // Metres. Indoors the pillar stops near a room's ceiling; outdoors it is seen from afar.
    private const float IndoorHeight = 4.5f, OutdoorHeight = 30, CoreWidth = 0.3f, GlowWidth = 1.4f, Ring = 0.9f, PulseFrom = 0.3f, PulseTo = 2.6f;
    // ponytail: outdoors a ping holds the pinger's height, not the ground's at the spot; on a slope the ring can float or sink.
    private const float Lift = 0.04f;
    private const double PulseSeconds = 1.4, FadeIn = 0.25, FadeOut = 1;
    private const float Hidden = 0.5f;
    // The scan: walls this far from the spot, drawn this high, cut into pieces this long so the wave and fall-off shade along them.
    private const float ScanRadius = 19, WallHeight = 3.6f, Piece = 1, WaveWidth = 1.2f, Trail = 3;
    // The grid's bands up the walls.
    private const int Bands = 4, RibEvery = 2;
    private const double ScanSeconds = 2.6;
    private const int TextureSize = 64;

    private readonly IntPtr _device;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly Dictionary<string, Beam> _beams = new();
    private IntPtr _column, _ring;
    private Vertex[] _scan = new Vertex[256];

    /// <param name="Walls">The scanned walls' bottom edges, in the same space as <see cref="Spot"/>; empty outdoors and until the plan is read.</param>
    private sealed record Beam(RadarPosition At, Vector3 Spot, uint Color, double Born, IReadOnlyList<(Vector3 A, Vector3 B)> Walls);

    public PingBeams(IntPtr device)
    {
        _device = device;
    }

    /// <summary>A ping arrived: the sender's beam moves to the spot, in RGB <paramref name="color"/>.</summary>
    public void Show(string sender, RadarPosition at, uint color)
    {
        lock (_gate) _beams[sender] = new Beam(at, Place(at, at.X, at.Y, at.Z), color & 0xFFFFFF, _clock.Elapsed.TotalSeconds, Array.Empty<(Vector3, Vector3)>());
    }

    /// <summary>
    /// The landblock's plan for an indoor ping has been read: the walls of the spot's level near it are kept for the scan, if the sender's
    /// beam is still on that spot. Call it off the game thread; it walks the plan.
    /// </summary>
    public void Scan(string sender, RadarPosition at, IndoorPlan landblock)
    {
        var place = landblock.Place((ushort)at.Cell);
        var level = place.LevelOf((ushort)at.Cell);
        if (level < 0) return;
        var walls = new List<(Vector3, Vector3)>();
        foreach (var cell in place.Levels[level].Cells)
            foreach (var (a, b) in cell.Walls)
            {
                var length = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                var pieces = Math.Max(1, (int)MathF.Ceiling(length / Piece));
                for (var i = 0; i < pieces; i++)
                {
                    float t0 = (float)i / pieces, t1 = (float)(i + 1) / pieces;
                    float x0 = a.X + (b.X - a.X) * t0, y0 = a.Y + (b.Y - a.Y) * t0, x1 = a.X + (b.X - a.X) * t1, y1 = a.Y + (b.Y - a.Y) * t1;
                    if (Vector2.Distance(new Vector2((x0 + x1) / 2, (y0 + y1) / 2), new Vector2(at.X, at.Y)) > ScanRadius) continue;
                    walls.Add((Place(at, x0, y0, cell.Height), Place(at, x1, y1, cell.Height)));
                }
            }
        lock (_gate)
            if (_beams.TryGetValue(sender, out var beam) && beam.At == at) _beams[sender] = beam with { Walls = walls };
    }

    public void Clear()
    {
        lock (_gate) _beams.Clear();
    }

    public unsafe void Draw(StormFog camera)
    {
        lock (_gate)
        {
            if (_beams.Count == 0) return;
            var now = _clock.Elapsed.TotalSeconds;
            var lasts = RadarPing.Lasts.TotalSeconds;
            foreach (var (sender, beam) in _beams)
                if (now - beam.Born >= lasts) _beams.Remove(sender);
            if (_beams.Count == 0 || !camera.Measured || !Matrix4x4.Invert(camera.View, out var eye)) return;
            if (_column == IntPtr.Zero) _column = MakeTexture((x, _) => MathF.Exp(-x * x * 7));
            if (_ring == IntPtr.Zero) _ring = MakeTexture((x, y) => { var r = MathF.Sqrt(x * x + y * y); return MathF.Exp(-MathF.Pow((r - 0.8f) * 8, 2)); });
            // The eye in the same space as the spots: retail's render space less its landblock offset.
            var from = eye.Translation - camera.Offset;
            var saved = SaveState(_device);
            try
            {
                UseFixedFunction(_device, FvfXyz | FvfDiffuse | FvfTex1);
                camera.UseWorldCamera();
                SetRenderState(_device, RenderState.DestBlend, BlendOne);
                SetStageState(_device, 0, StageState.TexCoordIndex, 0);
                SetStageState(_device, 0, StageState.TextureTransformFlags, 0);
                SetStageState(_device, 1, StageState.ColorOp, OpDisable);
                SetStageState(_device, 1, StageState.AlphaOp, OpDisable);
                SetSamplerState(_device, 0, SamplerState.MinFilter, FilterLinear);
                SetSamplerState(_device, 0, SamplerState.MagFilter, FilterLinear);
                SetSamplerState(_device, 0, SamplerState.MipFilter, FilterNone);
                SetSamplerState(_device, 0, SamplerState.AddressU, AddressClamp);
                SetSamplerState(_device, 0, SamplerState.AddressV, AddressClamp);

                // The scans first, untextured and through everything.
                SetRenderState(_device, RenderState.ZFunc, CmpAlways);
                SetTexture(_device, 0, IntPtr.Zero);
                SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
                SetStageState(_device, 0, StageState.ColorArg1, ArgDiffuse);
                SetStageState(_device, 0, StageState.AlphaOp, OpSelectArg1);
                SetStageState(_device, 0, StageState.AlphaArg1, ArgDiffuse);
                foreach (var beam in _beams.Values)
                    if (beam.Walls.Count > 0) DrawScan(beam, now - beam.Born, Fade(now - beam.Born, lasts));

                SetStageState(_device, 0, StageState.ColorOp, OpModulate);
                SetStageState(_device, 0, StageState.ColorArg1, ArgTexture);
                SetStageState(_device, 0, StageState.ColorArg2, ArgDiffuse);
                SetStageState(_device, 0, StageState.AlphaOp, OpModulate);
                SetStageState(_device, 0, StageState.AlphaArg1, ArgTexture);
                SetStageState(_device, 0, StageState.AlphaArg2, ArgDiffuse);
                var quad = stackalloc Vertex[4];
                // Behind something first, dimmer; then what is in view.
                foreach (var (test, strength) in new[] { (CmpGreater, Hidden), (CmpLessEqual, 1f) })
                {
                    SetRenderState(_device, RenderState.ZFunc, test);
                    foreach (var beam in _beams.Values)
                    {
                        var age = now - beam.Born;
                        DrawBeam(quad, beam, from, (float)(age / PulseSeconds % 1), strength * Fade(age, lasts));
                    }
                }
            }
            finally { RestoreState(saved); }
        }
    }

    private static float Fade(double age, double lasts) => (float)Math.Min(Math.Min(age / FadeIn, 1), Math.Min((lasts - age) / FadeOut, 1));

    private unsafe void DrawBeam(Vertex* quad, Beam beam, Vector3 eye, float pulse, float alpha)
    {
        var foot = beam.Spot + new Vector3(0, Lift, 0);
        var up = new Vector3(0, beam.At.Outdoors ? OutdoorHeight : IndoorHeight, 0);
        // The pillar turns about its upright axis to face the camera.
        var toEye = new Vector3(eye.X - foot.X, 0, eye.Z - foot.Z);
        var across = toEye.LengthSquared() < 1e-4f ? Vector3.UnitX : Vector3.Normalize(new Vector3(toEye.Z, 0, -toEye.X));
        SetTexture(_device, 0, _column);
        Pillar(quad, foot, up, across * GlowWidth / 2, Tint(beam.Color, alpha * 0.8f));
        Pillar(quad, foot, up, across * CoreWidth / 2, Tint(beam.Color | 0x606060, alpha));
        SetTexture(_device, 0, _ring);
        Flat(quad, foot, Ring, Tint(beam.Color | 0x303030, alpha));
        Flat(quad, foot, PulseFrom + (PulseTo - PulseFrom) * pulse, Tint(beam.Color, alpha * (1 - pulse)));
    }

    /// <summary>
    /// The walls near the spot as a hologram: each piece a panel glowing at its foot and fading up, under a grid of bands along it (the
    /// floor's brightest) and a rib every few metres, fading with distance from the spot and lit up as the wave sweeps past, leaving a trail.
    /// </summary>
    private unsafe void DrawScan(Beam beam, double age, float alpha)
    {
        // The wave reaches past the edge so it leaves it fully before the next starts.
        var front = (float)(age / ScanSeconds % 1) * (ScanRadius + Trail);
        var spot = new Vector2(beam.Spot.X, beam.Spot.Z);
        var up = new Vector3(0, WallHeight, 0);
        float Glow(Vector3 at)
        {
            var distance = Vector2.Distance(new Vector2(at.X, at.Z), spot);
            var near = Math.Clamp(1 - distance / ScanRadius, 0, 1);
            // A sharp front, and a trail behind it that dies away.
            var wave = distance > front ? MathF.Exp(-MathF.Pow((distance - front) / WaveWidth, 2)) : MathF.Exp(-(front - distance) / Trail);
            return alpha * near * (0.2f + 0.8f * wave);
        }
        var panels = beam.Walls.Count * 6;
        // Per piece: a line along each band (the floor's brightest, fading up) and, every RibEvery pieces, a rib up its first corner.
        var needed = panels + beam.Walls.Count * (Bands + 1) * 2;
        if (_scan.Length < needed) _scan = new Vertex[needed];
        var n = 0;
        foreach (var (a, b) in beam.Walls)
        {
            uint bottomA = Tint(beam.Color, Glow(a) * 0.6f), bottomB = Tint(beam.Color, Glow(b) * 0.6f), top = beam.Color;
            _scan[n++] = new(a, bottomA, 0, 0);
            _scan[n++] = new(b, bottomB, 0, 0);
            _scan[n++] = new(a + up, top, 0, 0);
            _scan[n++] = new(a + up, top, 0, 0);
            _scan[n++] = new(b, bottomB, 0, 0);
            _scan[n++] = new(b + up, top, 0, 0);
        }
        var lit = beam.Color | 0x404040;
        var piece = 0;
        foreach (var (a, b) in beam.Walls)
        {
            float glowA = Glow(a), glowB = Glow(b);
            for (var band = 0; band < Bands; band++)
            {
                var rise = up * band / (Bands - 1);
                var strength = band == 0 ? 2.4f : 1.1f * (1 - (float)band / Bands);
                _scan[n++] = new(a + rise, Tint(lit, glowA * strength), 0, 0);
                _scan[n++] = new(b + rise, Tint(lit, glowB * strength), 0, 0);
            }
            if (piece++ % RibEvery != 0) continue;
            _scan[n++] = new(a, Tint(lit, glowA * 1.2f), 0, 0);
            _scan[n++] = new(a + up, beam.Color, 0, 0);
        }
        fixed (Vertex* vertices = _scan)
        {
            DrawPrimitiveUP(_device, TriangleList, panels / 3, vertices, sizeof(Vertex));
            DrawPrimitiveUP(_device, LineList, (n - panels) / 2, vertices + panels, sizeof(Vertex));
        }
    }

    private unsafe void Pillar(Vertex* quad, Vector3 foot, Vector3 up, Vector3 half, uint color)
    {
        var top = color & 0xFFFFFF;
        quad[0] = new(foot - half, color, 0, 0.5f);
        quad[1] = new(foot + half, color, 1, 0.5f);
        quad[2] = new(foot + up - half, top, 0, 0.5f);
        quad[3] = new(foot + up + half, top, 1, 0.5f);
        DrawPrimitiveUP(_device, TriangleStrip, 2, quad, sizeof(Vertex));
    }

    /// <summary>A square on the ground, <paramref name="radius"/> from its centre to each side, for the ring texture.</summary>
    private unsafe void Flat(Vertex* quad, Vector3 centre, float radius, uint color)
    {
        quad[0] = new(centre + new Vector3(-radius, 0, -radius), color, 0, 0);
        quad[1] = new(centre + new Vector3(radius, 0, -radius), color, 1, 0);
        quad[2] = new(centre + new Vector3(-radius, 0, radius), color, 0, 1);
        quad[3] = new(centre + new Vector3(radius, 0, radius), color, 1, 1);
        DrawPrimitiveUP(_device, TriangleStrip, 2, quad, sizeof(Vertex));
    }

    /// <summary>A point in the ping's landblock, in landblock metres, in the space the beams are drawn in.</summary>
    private static Vector3 Place(RadarPosition at, float x, float y, float z) => StormFog.Render(StormFog.Global(at.Cell, new Vector3(x, y, z)));

    private static uint Tint(uint color, float alpha) => (uint)(Math.Clamp(alpha, 0, 1) * 255) << 24 | (color & 0xFFFFFF);

    /// <summary>A white texture whose alpha is <paramref name="alpha"/> of x and y, each from -1 to 1 across it.</summary>
    private IntPtr MakeTexture(Func<float, float, float> alpha)
    {
        var pixels = new byte[TextureSize * TextureSize * 4];
        for (var y = 0; y < TextureSize; y++)
            for (var x = 0; x < TextureSize; x++)
            {
                var i = (y * TextureSize + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0xFF;
                pixels[i + 3] = (byte)(Math.Clamp(alpha((x + 0.5f) / TextureSize * 2 - 1, (y + 0.5f) / TextureSize * 2 - 1), 0, 1) * 255);
            }
        var texture = CreateTexture(_device, TextureSize, TextureSize);
        Upload(texture, new Rectangle(0, 0, TextureSize, TextureSize), pixels, TextureSize * 4);
        return texture;
    }

    public void Dispose()
    {
        Release(_column);
        Release(_ring);
        _column = _ring = IntPtr.Zero;
    }

    private readonly struct Vertex(Vector3 position, uint color, float u, float v)
    {
        public readonly Vector3 Position = position;
        public readonly uint Color = color;
        public readonly float U = u, V = v;
    }
}
