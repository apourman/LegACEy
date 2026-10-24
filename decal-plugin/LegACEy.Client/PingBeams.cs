using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using LegACEy.Client.Demo;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Radar pings shown in the world: a beam of the sender's colour standing on the pinged spot, seen through walls and terrain, for as long as
/// the radar shows the ping. Each sender has one, as on the radar. Drawn on the screen like <see cref="CombatText"/>, with the camera the
/// storm fog measured.
/// </summary>
internal sealed class PingBeams
{
    // ponytail: a ping holds the pinger's height, not the ground's at the spot, so the beam reaches below it and well above.
    private const float Below = 8, Above = 40, Width = 7, Glow = 4;
    private const double PulseSeconds = 1.2, FadeOut = 1;

    private readonly IntPtr _device;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly Dictionary<string, (Vector3 At, uint Color, double Born)> _beams = new();

    public PingBeams(IntPtr device)
    {
        _device = device;
    }

    /// <summary>A ping arrived: the sender's beam moves to the spot, in RGB <paramref name="color"/>.</summary>
    public void Show(string sender, RadarPosition at, uint color)
    {
        var spot = new Vector3((at.Cell >> 24) * StormFog.Block + at.X, ((at.Cell >> 16) & 0xFF) * StormFog.Block + at.Y, at.Z);
        lock (_gate) _beams[sender] = (spot, color & 0xFFFFFF, _clock.Elapsed.TotalSeconds);
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
            foreach (var gone in new List<string>(_beams.Keys))
                if (now - _beams[gone].Born >= lasts) _beams.Remove(gone);
            if (_beams.Count == 0 || !camera.Measured) return;
            var screen = Viewport(_device);
            var viewProjection = camera.View * camera.Projection;
            var saved = SaveState(_device);
            try
            {
                UseScreenColour();
                var quad = stackalloc Vertex[4];
                foreach (var (at, color, born) in _beams.Values)
                {
                    var bottom = StormFog.Render(at - new Vector3(0, 0, Below)) + camera.Offset;
                    var top = StormFog.Render(at + new Vector3(0, 0, Above)) + camera.Offset;
                    if (CombatText.ToScreen(bottom, viewProjection, screen) is not { } foot ||
                        CombatText.ToScreen(top, viewProjection, screen) is not { } head) continue;
                    var age = now - born;
                    var fade = age < lasts - FadeOut ? 1f : (float)((lasts - age) / FadeOut);
                    var pulse = 0.75f + 0.25f * MathF.Cos((float)(age / PulseSeconds * 2 * Math.PI));
                    var alpha = fade * pulse;
                    var along = new Vector2(head.X - foot.X, head.Y - foot.Y);
                    if (along.LengthSquared() < 1) along = new Vector2(0, -1);
                    var across = Vector2.Normalize(new Vector2(-along.Y, along.X));
                    // A soft glow, then the core; each bright at the foot and gone at the top.
                    Beam(quad, foot, head, across * (Width / 2 + Glow), (uint)(alpha * 0x50) << 24 | color);
                    Beam(quad, foot, head, across * (Width / 2), (uint)(alpha * 0xE0) << 24 | color);
                }
            }
            finally { RestoreState(saved); }
        }
    }

    private unsafe void Beam(Vertex* quad, System.Drawing.PointF foot, System.Drawing.PointF head, Vector2 half, uint color)
    {
        var faded = color & 0xFFFFFF;
        quad[0] = new(foot.X - half.X, foot.Y - half.Y, color);
        quad[1] = new(foot.X + half.X, foot.Y + half.Y, color);
        quad[2] = new(head.X - half.X, head.Y - half.Y, faded);
        quad[3] = new(head.X + half.X, head.Y + half.Y, faded);
        DrawPrimitiveUP(_device, TriangleStrip, 2, quad, sizeof(Vertex));
    }

    private void UseScreenColour()
    {
        UseFixedFunction(_device, FvfXyzRhw | FvfDiffuse);
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
        SetRenderState(_device, RenderState.SrcBlend, BlendSrcAlpha);
        SetRenderState(_device, RenderState.DestBlend, BlendInvSrcAlpha);
        SetTexture(_device, 0, IntPtr.Zero);
        SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
        SetStageState(_device, 0, StageState.ColorArg1, ArgDiffuse);
        SetStageState(_device, 0, StageState.AlphaOp, OpSelectArg1);
        SetStageState(_device, 0, StageState.AlphaArg1, ArgDiffuse);
        SetStageState(_device, 1, StageState.ColorOp, OpDisable);
        SetStageState(_device, 1, StageState.AlphaOp, OpDisable);
    }

    private readonly struct Vertex(float x, float y, uint color)
    {
        public readonly float X = x, Y = y, Z = 0, W = 1;
        public readonly uint Color = color;
    }
}
