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

    /// <summary>
    /// The renders to compare by eye, outdoors and indoors. They are written when LEGACEY_RADAR_RENDER names a folder. Indoors needs
    /// LEGACEY_CELL_DAT and LEGACEY_PORTAL_DAT too: the dungeon in 0x0099 on cell 0x0100 and the Arwic house on cell 0x0168 (its ground under
    /// it), both facing east, where the mockup starts them.
    /// </summary>
    [Fact]
    public void The_radar_renders_outdoors_and_indoors_at_1x_and_2x_with_the_ground_dots_and_options() => RenderThread.Run(() =>
    {
        var folder = Environment.GetEnvironmentVariable("LEGACEY_RADAR_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var ground = OpenGround();

        Render(folder, "radar-1x.png", 1, ground, new RadarOptions());
        Render(folder, "radar-2x.png", 2, ground, new RadarOptions());
        Render(folder, "radar-north-up-1x.png", 1, ground, new RadarOptions(NorthUp: true));
        Render(folder, "radar-dark-1x.png", 1, null, new RadarOptions(Map: false));
        Render(folder, "radar-options-2x.png", 2, ground, new RadarOptions(Rings: true), openOptions: true);
        Render(folder, "radar-hover-1x.png", 1, ground, new RadarOptions(), hover: true);
        RenderIndoors(folder, ground);
    });

    // The indoor renders, when the cell DAT and the portal DAT are given. The plans are read on the thread pool, as the client reads them.
    private static void RenderIndoors(string folder, GroundTiles? ground)
    {
        var cellPath = Environment.GetEnvironmentVariable("LEGACEY_CELL_DAT");
        var portalPath = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (cellPath == null || portalPath == null) return;
        using var indoors = new IndoorDat(portalPath, cellPath);
        var plans = new IndoorPlanCache(indoors.ReadPlan);

        Render(folder, "radar-indoor-0099-2x.png", 2, ground, new RadarOptions(), player: Standing(plans, 0x00990100), plans: plans);
        Render(folder, "radar-indoor-house-2x.png", 2, ground, new RadarOptions(), player: Standing(plans, 0xC6A90168), plans: plans);
        Render(folder, "radar-indoor-house-25m-2x.png", 2, ground, new RadarOptions(IndoorZoom: 25), player: Standing(plans, 0xC6A90168),
            plans: plans);
        Render(folder, "radar-indoor-0099-peek-2x.png", 2, ground, new RadarOptions(), player: Standing(plans, 0x00990100), plans: plans, peek: 4);
    }

    // A cell's centre, at its height, facing east: where the mockup starts a player in a cell.
    private static RadarPlayer Standing(IndoorPlanCache plans, uint cell)
    {
        var landblock = plans.PlanAsync((int)(cell >> 24), (int)((cell >> 16) & 0xFF)).Result;
        var shape = landblock.Cell((ushort)cell)!;
        var floor = shape.Floors[0];
        return new RadarPlayer(new RadarPosition(cell, (float)floor.Average(p => p.X), (float)floor.Average(p => p.Y), shape.Height), 90);
    }

    private static void Render(string folder, string file, int scale, GroundTiles? ground, RadarOptions options,
        bool openOptions = false, bool hover = false, RadarPlayer? player = null, IndoorPlanCache? plans = null, int peek = -1)
    {
        var port = new FakeRadarPort { Player = player ?? new RadarPlayer(new RadarPosition(Cell, 96, 96, 0), 270) };
        port.Objects.AddRange(Scene(port.Player.Position));
        RetailRadarWindow? window = null;
        var panel = AvaloniaPanel.Create(
            () => window = new RetailRadarWindow(port, () => ground, () => plans, options, _ => { }, () => 0, () => { }),
            RetailRadarWindow.DesignWidth * scale, RetailRadarWindow.DesignHeight * scale, scale);
        try
        {
            // The ground and the plan build off the UI thread: draw each frame until they are in, or the wait runs out.
            var until = DateTime.UtcNow.AddSeconds(15);
            do
            {
                window!.Refresh();
                panel.Tick();
                if (Built(window!, ground, plans) || DateTime.UtcNow > until) break;
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
            if (hover)
            {
                // The pointer over the frame's corner, with the UI unlocked: the resize corners show.
                panel.PointerMove(4 * scale, 4 * scale);
                panel.Tick();
            }
            if (peek >= 0)
            {
                // The pointer over a ladder pip, as a player hovers one: the pips share the ladder's height, the top floor first.
                var bounds = RetailRadarWindow.LadderBounds;
                var slot = bounds.Height / window!.Frame!.Ladder!.Levels;
                panel.PointerMove((bounds.X + bounds.Width / 2) * RetailRadarWindow.Scale * scale,
                    (bounds.Y + (peek + 0.5) * slot) * RetailRadarWindow.Scale * scale);
                panel.Tick();
            }
            Save(panel, Path.Combine(folder, file));
        }
        finally
        {
            panel.Dispose();
        }
    }

    // Whether the frame has what the render waits for: the ground its tiles call for, and a plan indoors.
    private static bool Built(RetailRadarWindow window, GroundTiles? ground, IndoorPlanCache? plans) =>
        (ground == null || window.TilesDrawn == window.Frame!.Tiles.Count) && (plans == null || window.Frame!.PlanLevels.Count > 0);

    // Fake creatures, NPCs, players, fellows, portals and lifestones, around the player, one hidden from retail's radar.
    private static IEnumerable<RadarObject> Scene(RadarPosition at)
    {
        RadarObject Near(uint id, string name, RadarColor colour, float east, float north, RadarBehavior behaviour = RadarBehavior.ShowAlways) =>
            new(id, name, colour, behaviour, new RadarPosition(at.Cell, at.X + east, at.Y + north, at.Z));
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
