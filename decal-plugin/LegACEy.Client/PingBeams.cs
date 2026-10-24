using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using LegACEy.Client.Demo;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Radar pings shown in the world, in the sender's radar colour, for as long as the radar shows the ping; each sender has one, as on the
/// radar. A pillar of light stands on the spot, turned to face the camera, brightest at its foot and fading upwards, with a ring on the
/// ground that sends out a pulse. Both glow (added to the scene) and are drawn twice with the storm fog's camera: faint where something
/// stands in front of them, so they show through walls and terrain, and in full where nothing does.
/// </summary>
internal sealed class PingBeams : IDisposable
{
    // Metres. Indoors the pillar stops near a room's ceiling; outdoors it is seen from afar.
    private const float IndoorHeight = 4.5f, OutdoorHeight = 30, CoreWidth = 0.22f, GlowWidth = 1.1f, Ring = 0.8f, PulseFrom = 0.3f, PulseTo = 2.2f;
    // ponytail: a ping holds the pinger's height, not the ground's at the spot; outdoors on a slope the ring can float or sink.
    private const float Lift = 0.04f;
    private const double PulseSeconds = 1.4, FadeIn = 0.25, FadeOut = 1;
    private const float Hidden = 0.3f;
    private const int TextureSize = 64;

    private readonly IntPtr _device;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly Dictionary<string, Beam> _beams = new();
    private IntPtr _column, _ring;

    private sealed record Beam(Vector3 Spot, bool Outdoors, uint Color, double Born);

    public PingBeams(IntPtr device)
    {
        _device = device;
    }

    /// <summary>A ping arrived: the sender's beam moves to the spot, in RGB <paramref name="color"/>.</summary>
    public void Show(string sender, RadarPosition at, uint color)
    {
        var spot = StormFog.Render(StormFog.Global(at.Cell, new Vector3(at.X, at.Y, at.Z)));
        lock (_gate) _beams[sender] = new Beam(spot, at.Outdoors, color & 0xFFFFFF, _clock.Elapsed.TotalSeconds);
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
            if (_column == IntPtr.Zero) _column = MakeTexture((x, _) => MathF.Exp(-x * x * 9));
            if (_ring == IntPtr.Zero) _ring = MakeTexture((x, y) => { var r = MathF.Sqrt(x * x + y * y); return MathF.Exp(-MathF.Pow((r - 0.8f) * 9, 2)); });
            // The eye in the same space as the spots: retail's render space less its landblock offset.
            var from = eye.Translation - camera.Offset;
            var saved = SaveState(_device);
            try
            {
                UseFixedFunction(_device, FvfXyz | FvfDiffuse | FvfTex1);
                camera.UseWorldCamera();
                SetRenderState(_device, RenderState.DestBlend, BlendOne);
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
                SetSamplerState(_device, 0, SamplerState.AddressU, AddressClamp);
                SetSamplerState(_device, 0, SamplerState.AddressV, AddressClamp);
                var quad = stackalloc Vertex[4];
                // Behind something first, faint; then what is in view.
                foreach (var (test, strength) in new[] { (CmpGreater, Hidden), (CmpLessEqual, 1f) })
                {
                    SetRenderState(_device, RenderState.ZFunc, test);
                    foreach (var beam in _beams.Values)
                    {
                        var age = now - beam.Born;
                        var alpha = strength * (float)Math.Min(Math.Min(age / FadeIn, 1), Math.Min((lasts - age) / FadeOut, 1));
                        DrawBeam(quad, beam, from, (float)(age / PulseSeconds % 1), alpha);
                    }
                }
            }
            finally { RestoreState(saved); }
        }
    }

    private unsafe void DrawBeam(Vertex* quad, Beam beam, Vector3 eye, float pulse, float alpha)
    {
        var foot = beam.Spot + new Vector3(0, Lift, 0);
        var up = new Vector3(0, beam.Outdoors ? OutdoorHeight : IndoorHeight, 0);
        // The pillar turns about its upright axis to face the camera.
        var toEye = new Vector3(eye.X - foot.X, 0, eye.Z - foot.Z);
        var across = toEye.LengthSquared() < 1e-4f ? Vector3.UnitX : Vector3.Normalize(new Vector3(toEye.Z, 0, -toEye.X));
        SetTexture(_device, 0, _column);
        Pillar(quad, foot, up, across * GlowWidth / 2, Tint(beam.Color, alpha * 0.45f));
        Pillar(quad, foot, up, across * CoreWidth / 2, Tint(beam.Color, alpha) | 0x404040);
        SetTexture(_device, 0, _ring);
        Flat(quad, foot, Ring, Tint(beam.Color, alpha * 0.9f));
        var size = PulseFrom + (PulseTo - PulseFrom) * pulse;
        Flat(quad, foot, size, Tint(beam.Color, alpha * (1 - pulse) * 0.8f));
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

    private static uint Tint(uint color, float alpha) => (uint)(Math.Clamp(alpha, 0, 1) * 255) << 24 | color;

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
