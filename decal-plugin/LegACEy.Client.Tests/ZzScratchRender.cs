using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Inventory;
using Xunit;
using Xunit.Abstractions;
namespace LegACEy.Client.Tests;
public sealed partial class InventoryWindowTests
{
    [Fact]
    public void ZzScratch() => RenderThread.Run(() =>
    {
        var log = new System.Text.StringBuilder();
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var w = host.Window;
        foreach (var scale in new[] { 1.0, 1.15, 1.25, 2.0 })
        {
            int W = (int)Math.Ceiling(w.Bounds.Width * scale), H = (int)(110 * scale);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(W, H), new Vector(96 * scale, 96 * scale));
            bitmap.Render(w);
            bitmap.Save($"/tmp/claude-1000/-home-jacob-repos-LegACEy/2f3976d2-5843-4c15-9ef3-1c5da163f0b6/scratchpad/render/align-{scale}.png");
            var px = new byte[W * H * 4];
            var h = GCHandle.Alloc(px, GCHandleType.Pinned);
            bitmap.CopyPixels(new PixelRect(0, 0, W, H), h.AddrOfPinnedObject(), px.Length, W * 4);
            h.Free();
            (int b, int g, int r) P(int x, int y) { var i = (y * W + x) * 4; return (px[i], px[i + 1], px[i + 2]); }
            log.AppendLine($"scale {scale}");
            // Title: rows between rules with light ink at the centre columns.
            var title = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Inventory");
            var rules = w.GetVisualDescendants().OfType<DerethRule>().ToArray();
            int Y(Visual v, double y) => (int)Math.Round(v.TranslatePoint(new Point(0, y), w)!.Value.Y * scale);
            bool Gold(int x, int y) { var c = P(x, y); return c.r > 120 && c.g > 80 && c.b < 110; }
            bool Ink(int x, int y) { var c = P(x, y); return c.r > 150 && c.g > 150 && c.b > 130; }
            int mid = W / 2;
            int r1top = Y(rules[0], 0), r2bot = Y(rules[1], 5);
            var goldRows = Enumerable.Range(r1top, r2bot - r1top).Where(y => Gold(mid - 60, y)).ToArray();
            log.AppendLine("gold rows at x=mid-60: " + string.Join(",", goldRows));
            var inkRows = Enumerable.Range(r1top, r2bot - r1top).Where(y => Enumerable.Range(mid - 40, 80).Any(x => Ink(x, y))).ToArray();
            log.AppendLine($"ink rows {inkRows.First()}..{inkRows.Last()}");
            foreach (var b in w.GetVisualDescendants().OfType<DerethHeaderButton>().Where(b => b.IsEffectivelyVisible))
            {
                var tl = b.TranslatePoint(new Point(0, 0), w)!.Value * scale;
                int x0 = (int)Math.Round(tl.X), y0 = (int)Math.Round(tl.Y), s = (int)Math.Round(b.Bounds.Width * scale);
                // glyph pixels: green or cream, inside the inner box (skip 2 px rim)
                bool G(int x, int y) { var c = P(x, y); return (c.g > 110 && c.r < 140) || (c.r > 180 && c.g > 170); }
                var pts = (from y in Enumerable.Range(y0 + 2, s - 4) from x in Enumerable.Range(x0 + 2, s - 4) where G(x, y) select (x, y)).ToArray();
                if (pts.Length == 0) { log.AppendLine("no glyph"); continue; }
                double gx = pts.Average(p => p.x) + 0.5 - x0, gy = pts.Average(p => p.y) + 0.5 - y0;
                log.AppendLine($" btn {b.Name ?? b.Tag?.ToString() ?? "close"} at {tl.X:F2},{tl.Y:F2} size {b.Bounds.Width * scale:F2} glyph centre off {gx - s / 2.0:F2},{gy - s / 2.0:F2} ink x {pts.Min(p => p.x) - x0}..{x0 + s - 1 - pts.Max(p => p.x)} y {pts.Min(p => p.y) - y0}..{y0 + s - 1 - pts.Max(p => p.y)}");
            }
        }
        System.IO.File.WriteAllText("/tmp/claude-1000/-home-jacob-repos-LegACEy/2f3976d2-5843-4c15-9ef3-1c5da163f0b6/scratchpad/render/align.txt", log.ToString());
    });
}
