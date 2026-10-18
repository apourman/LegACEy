using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Floating combat text: each hit the server reports rises from above its target's head and fades, coloured by damage type; criticals pop
/// larger, damage-over-time ticks are smaller. The target's head is the highest part retail drew for it this frame (<see cref="Saw"/>);
/// a target not drawn this frame keeps the last place it was seen. Digits come from an atlas rendered once in the Dereth font.
/// </summary>
internal sealed unsafe class CombatText : IDisposable
{
    private const string Digits = "0123456789";
    private const double AtlasFont = 32;
    private const double Life = 1.3, FadeOut = 0.4;
    private const float Rise = 60, HeadRoom = 0.45f;
    private const int MostNumbers = 64;

    private readonly IntPtr _device;
    private readonly Action<string> _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();
    private readonly object _gate = new();
    private readonly List<Number> _numbers = new();
    // Each target's head, in render space less the camera's landblock offset, so it holds still as the offset moves; and the frame it was seen.
    private readonly Dictionary<uint, (Vector3 Head, int Frame)> _heads = new();
    private int _frame;
    // How many targets have numbers: read without the lock on every draw call, so the world's thousands of draws pay nothing between fights.
    private volatile int _tracked;
    private IntPtr _atlas;
    private int _atlasWidth, _atlasHeight;
    private RectangleF[] _glyphs = Array.Empty<RectangleF>();
    private bool _atlasFailed;

    public CombatText(IntPtr device, Action<string> log)
    {
        _device = device;
        _log = log;
    }

    private sealed record Number(uint Target, string Text, uint Color, float Size, bool Critical, double Born, float Drift);

    /// <summary>A hit the server reported.</summary>
    public void Add(uint target, uint amount, uint damageType, bool critical, bool periodic)
    {
        lock (_gate)
        {
            if (_numbers.Count >= MostNumbers) _numbers.RemoveAt(0);
            var size = critical ? 1.5f : periodic ? 0.75f : 1f;
            // Hits on one target spread out sideways, so a flurry does not stack into one unreadable number.
            var drift = critical ? 0 : (float)(_random.NextDouble() * 2 - 1) * 28;
            _numbers.Add(new Number(target, amount.ToString(), ColorOf(damageType), size, critical, _clock.Elapsed.TotalSeconds, drift));
            if (!_heads.ContainsKey(target)) _heads[target] = (new Vector3(float.NaN), -1);
            _tracked = _heads.Count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _numbers.Clear();
            _heads.Clear();
            _tracked = 0;
        }
    }

    /// <summary>Whether a number is showing over the object, so its parts are worth measuring.</summary>
    public bool Tracks(uint id)
    {
        if (_tracked == 0) return false;
        lock (_gate) return _heads.ContainsKey(id);
    }

    /// <summary>One of the object's parts is drawn at this render position (less the landblock offset); its head is the highest this frame.</summary>
    public void Saw(uint id, Vector3 part)
    {
        lock (_gate)
        {
            if (!_heads.TryGetValue(id, out var head)) return;
            _heads[id] = head.Frame != _frame || part.Y > head.Head.Y ? (part, _frame) : head;
        }
    }

    /// <summary>Draws the numbers over the scene with the camera the storm fog measured, then starts the next frame's measure.</summary>
    public void Draw(StormFog camera)
    {
        lock (_gate)
        {
            _frame++;
            var now = _clock.Elapsed.TotalSeconds;
            _numbers.RemoveAll(number => now - number.Born > Life);
            if (_numbers.Count == 0)
            {
                _heads.Clear();
                _tracked = 0;
                return;
            }
            if (!camera.Measured || !EnsureAtlas()) return;
            var screen = Viewport(_device);
            var viewProjection = camera.View * camera.Projection;
            var saved = SaveState(_device);
            try
            {
                UseScreenText();
                var quad = stackalloc GlyphVertex[4];
                foreach (var number in _numbers)
                {
                    if (!_heads.TryGetValue(number.Target, out var head) || float.IsNaN(head.Head.X)) continue;
                    var top = head.Head + camera.Offset + new Vector3(0, HeadRoom, 0);
                    if (ToScreen(top, viewProjection, screen) is not { } at) continue;
                    var age = now - number.Born;
                    var t = (float)(age / Life);
                    var alpha = age < Life - FadeOut ? 1f : (float)((Life - age) / FadeOut);
                    // A critical pops in large and settles.
                    var pop = number.Critical && age < 0.12 ? 1 + (float)(0.12 - age) / 0.12f * 0.6f : 1;
                    var scale = number.Size * pop * 0.6f;
                    var y = at.Y - Rise * (1 - (1 - t) * (1 - t));
                    var x = at.X + number.Drift * t;
                    var width = 0f;
                    foreach (var c in number.Text) width += _glyphs[c - '0'].Width * scale;
                    // A dark rim first, then the number in its colour.
                    var rim = (uint)(alpha * 0xC0) << 24;
                    foreach (var (dx, dy) in Rim)
                        DrawText(quad, number.Text, x - width / 2 + dx, y + dy, scale, rim);
                    DrawText(quad, number.Text, x - width / 2, y, scale, (uint)(alpha * 255) << 24 | number.Color);
                }
            }
            finally { RestoreState(saved); }
        }
    }

    private static readonly (float, float)[] Rim = { (-1.5f, 0), (1.5f, 0), (0, -1.5f), (0, 1.5f), (-1, -1), (1, 1), (-1, 1), (1, -1) };

    private void DrawText(GlyphVertex* quad, string text, float left, float centreY, float scale, uint color)
    {
        var x = left;
        foreach (var c in text)
        {
            var glyph = _glyphs[c - '0'];
            float w = glyph.Width * scale, h = glyph.Height * scale, top = centreY - h / 2;
            float u0 = glyph.Left / _atlasWidth, u1 = glyph.Right / _atlasWidth, v0 = glyph.Top / _atlasHeight, v1 = glyph.Bottom / _atlasHeight;
            quad[0] = new(x - 0.5f, top - 0.5f, color, u0, v0);
            quad[1] = new(x + w - 0.5f, top - 0.5f, color, u1, v0);
            quad[2] = new(x - 0.5f, top + h - 0.5f, color, u0, v1);
            quad[3] = new(x + w - 0.5f, top + h - 0.5f, color, u1, v1);
            DrawPrimitiveUP(_device, TriangleStrip, 2, quad, sizeof(GlyphVertex));
            x += w;
        }
    }

    private void UseScreenText()
    {
        UseFixedFunction(_device, FvfXyzRhw | FvfDiffuse | FvfTex1);
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
        SetTexture(_device, 0, _atlas);
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
    }

    /// <summary>A render-space point on the screen, or null behind the camera.</summary>
    internal static PointF? ToScreen(Vector3 point, Matrix4x4 viewProjection, Rectangle screen)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), viewProjection);
        if (clip.W <= 0.1f) return null;
        return new PointF(screen.Left + (clip.X / clip.W + 1) / 2 * screen.Width, screen.Top + (1 - clip.Y / clip.W) / 2 * screen.Height);
    }

    /// <summary>The number's colour for its damage type flags (ACE's DamageType): white for physical, the element's colour otherwise.</summary>
    internal static uint ColorOf(uint damageType) => damageType switch
    {
        _ when (damageType & 0x10) != 0 => 0xFF8C2A, // fire
        _ when (damageType & 0x08) != 0 => 0x7FD4FF, // cold
        _ when (damageType & 0x40) != 0 => 0xC08CFF, // lightning
        _ when (damageType & 0x20) != 0 => 0x8BE04A, // acid
        _ when (damageType & 0x400) != 0 => 0xB070E0, // nether
        _ when (damageType & 0x380) != 0 => 0xE05050, // health, stamina, mana: bleeds and drains
        _ => 0xFFFFFF,
    };

    /// <summary>Renders the digits once in the Dereth title font, white with their alpha, and notes where each one sits.</summary>
    private bool EnsureAtlas()
    {
        if (_atlas != IntPtr.Zero) return true;
        if (_atlasFailed) return false;
        try
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in Digits)
                row.Children.Add(new TextBlock
                {
                    Text = c.ToString(), FontFamily = DerethPalette.Title, FontSize = AtlasFont, FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White, Margin = new Avalonia.Thickness(1, 0), VerticalAlignment = VerticalAlignment.Center,
                });
            using var panel = AvaloniaPanel.Create(() => row, (int)(AtlasFont * 10), (int)(AtlasFont * 1.4));
            panel.Tick();
            var frame = panel.Frame;
            _glyphs = new RectangleF[Digits.Length];
            for (var i = 0; i < Digits.Length; i++)
            {
                var bounds = row.Children[i].Bounds;
                _glyphs[i] = new RectangleF((float)bounds.X, 0, (float)bounds.Width + 2, frame.Height);
            }
            // White everywhere, the glyphs in the alpha: the vertex colour tints them without the premultiplied edges darkening.
            var pixels = (byte[])frame.Pixels.Clone();
            for (var i = 0; i < pixels.Length; i += 4) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0xFF;
            _atlasWidth = frame.Width;
            _atlasHeight = frame.Height;
            _atlas = CreateTexture(_device, frame.Width, frame.Height);
            Upload(_atlas, new Rectangle(0, 0, frame.Width, frame.Height), pixels, frame.Stride);
            return true;
        }
        catch (Exception exception)
        {
            // Without digits there is no combat text, but the other world effects carry on.
            _atlasFailed = true;
            _log($"Combat text is off: its digits could not be drawn: {exception.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Release(_atlas);
        _atlas = IntPtr.Zero;
    }

    private readonly struct GlyphVertex(float x, float y, uint color, float u, float v)
    {
        public readonly float X = x, Y = y, Z = 0, W = 1;
        public readonly uint Color = color;
        public readonly float U = u, V = v;
    }
}
