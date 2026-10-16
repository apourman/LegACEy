using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// Visual check of the Dereth radar through the in-game panel pipeline, at 1× and 2×. It writes PNGs when LEGACEY_RADAR_RENDER names a
/// folder; the ground is Arwic's when LEGACEY_PORTAL_DAT is set, and flat green otherwise. Nothing is asserted: the renders are compared
/// with the mockup by eye.
/// </summary>
public sealed class RetailRadarRenderTests
{
    // Arwic's landblock 0xC6A9, outdoors, at its middle.
    private const uint Cell = 0xC6A90001;
    private static readonly Avalonia.Media.Color Backdrop = Avalonia.Media.Color.FromRgb(0x1A, 0x1E, 0x22);

    [Fact]
    public void The_radar_renders_at_1x_and_2x_with_the_ground_dots_and_options() => RenderThread.Run(() =>
    {
        var folder = Environment.GetEnvironmentVariable("LEGACEY_RADAR_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var ground = OpenGround();

        Render(folder, "radar-1x.png", 1, ground, new RadarOptions());
        Render(folder, "radar-2x.png", 2, ground, new RadarOptions());
        Render(folder, "radar-north-up-1x.png", 1, ground, new RadarOptions(NorthUp: true));
        Render(folder, "radar-dark-1x.png", 1, null, new RadarOptions(Ground: false));
        Render(folder, "radar-options-2x.png", 2, ground, new RadarOptions(Rings: true), openOptions: true);
    });

    private static void Render(string folder, string file, int scale, GroundTiles? ground, RadarOptions options, bool openOptions = false)
    {
        var port = new FakeRadarPort { Player = new RadarPlayer(new RadarPosition(Cell, 96, 96, 0), 270) };
        port.Objects.AddRange(Scene());
        RetailRadarWindow? window = null;
        var panel = AvaloniaPanel.Create(() => window = new RetailRadarWindow(port, () => ground, options, _ => { }, () => 0, () => { }),
            RetailRadarWindow.Width * scale, RetailRadarWindow.Height * scale, scale);
        try
        {
            // The ground builds off the UI thread: draw each frame until its tiles are in, or the wait runs out.
            var until = DateTime.UtcNow.AddSeconds(15);
            do
            {
                window!.Refresh();
                panel.Tick();
                if (ground == null || window!.TilesDrawn == 9 || DateTime.UtcNow > until) break;
                Thread.Sleep(50);
            } while (true);
            if (openOptions)
            {
                var gear = panel.Content.GetVisualDescendants().OfType<Avalonia.Controls.Control>().First(control => Avalonia.Controls.ToolTip.GetTip(control) as string == "Radar options");
                var at = gear.TranslatePoint(new Point(gear.Bounds.Width / 2, gear.Bounds.Height / 2), panel.Content)!.Value;
                // The panel takes pixels; the content measures in DIPs.
                panel.PointerDown(at.X * scale, at.Y * scale);
                panel.PointerUp(at.X * scale, at.Y * scale);
                window!.Refresh();
                panel.Tick();
            }
            Save(panel, Path.Combine(folder, file));
        }
        finally
        {
            panel.Dispose();
        }
    }

    // Fake creatures, NPCs, players, fellows, portals and lifestones, around the player, one hidden from retail's radar.
    private static IEnumerable<RadarObject> Scene()
    {
        RadarObject Near(uint id, string name, RadarColor colour, float east, float north, RadarBehavior behaviour = RadarBehavior.ShowAlways) =>
            new(id, name, colour, behaviour, new RadarPosition(Cell, 96 + east, 96 + north, 0));
        yield return Near(1, "Mirra", RadarColor.BrightGreen, 12, 10);
        yield return Near(2, "Archmage", RadarColor.Yellow, -18, 22);
        yield return Near(3, "Barkeeper", RadarColor.Yellow, 25, -4);
        yield return Near(4, "Drudge Skulker", RadarColor.Gold, -40, -30);
        yield return Near(5, "Arwic Lifestone", RadarColor.Blue, 4, -28);
        yield return Near(6, "Portal", RadarColor.Purple, -30, 45);
        yield return Near(7, "Vexx", RadarColor.Red, 30, 30);
        yield return Near(8, "Thorne", RadarColor.White, -10, -8);
        yield return Near(9, "Hidden", RadarColor.Gold, 6, 6, RadarBehavior.ShowNever);
    }

    // Arwic's ground when the DATs are given, else none: the caller passes null for the dark variant.
    private static GroundTiles? OpenGround()
    {
        var portal = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (portal == null) return new GroundTiles(new FlatGround());
        return new GroundTiles(new GroundDat(new PortalDat(portal)));
    }

    // Writes the panel's frame as a PNG over the backdrop, so the transparent parts of the window show as they do over the world.
    private static void Save(AvaloniaPanel panel, string path)
    {
        var frame = panel.Frame;
        var width = frame.Width;
        var height = frame.Height;
        var backdrop = new[] { Backdrop.B, Backdrop.G, Backdrop.R };
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var locked = bitmap.Lock();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var source = (y * frame.Stride) + (x * 4);
                var alpha = frame.Pixels[source + 3];
                for (var channel = 0; channel < 3; channel++)
                {
                    // The frame is premultiplied: what is not covered shows the backdrop.
                    var value = frame.Pixels[source + channel] + backdrop[channel] * (255 - alpha) / 255;
                    Marshal.WriteByte(locked.Address, (y * locked.RowBytes) + (x * 4) + channel, (byte)Math.Min(255, value));
                }
                Marshal.WriteByte(locked.Address, (y * locked.RowBytes) + (x * 4) + 3, 255);
            }
        bitmap.Save(path);
    }

    /// <summary>Flat green terrain everywhere: every landblock builds, so the harness renders without the DATs.</summary>
    private sealed class FlatGround : IGroundSource
    {
        public GroundLandblock? ReadLandblock(int x, int y) =>
            new(x, y, new ushort[81], new float[81], Array.Empty<GroundFootprint>());

        public GameImage? TerrainTexture(uint terrainType) => new(4, 4, Enumerable.Range(0, 64).Select(i => (byte)(i % 4 == 3 ? 0xFF : 0x40)).ToArray());
    }
}
