using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class MapWindowTests
{
    private const string Yaraq = "Date: Morningthaw 20, 122 P.Y.\nTime: Foredawn";

    [Fact]
    public void The_window_shows_what_retails_map_and_house_pages_hold() => RenderThread.Run(() =>
    {
        var map = new FakeMap { View = new RetailMapView(Yaraq, "21.5S, 1.7W", new Point(125, 159), null) };
        MapWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new MapWindow(map, null)).Window, MapWindow.Width, MapWindow.Height);
        var texts = Texts(view);
        Assert.Contains("Date: Morningthaw 20, 122 P.Y.", texts);
        Assert.Contains("Time: Foredawn", texts);
        Assert.Contains("21.5S, 1.7W", texts);
        var player = view.Window.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single();
        Assert.True(player.IsVisible);
        Assert.Equal(125 - 7, Canvas.GetLeft(player));

        // Indoors retail empties the coordinates and hides the marker.
        map.View = map.View with { Coordinates = string.Empty, Player = null };
        view.Refresh();
        Assert.False(player.IsVisible);

        view.ShowTab(1);
        Assert.Contains("You do not currently own a house.", Texts(view));
        map.Lines = new[] { "Your house is at 21.5S, 1.7W." };
        view.Refresh();
        Assert.DoesNotContain("You do not currently own a house.", Texts(view));
    });

    [Fact]
    public void The_map_window_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_MAP_RENDER names the folder; LEGACEY_DAT_DIR gives it retail's map art.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_MAP_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var dats = Environment.GetEnvironmentVariable("LEGACEY_DAT_DIR");
        using var portal = dats == null ? null : new PortalDat(Path.Combine(dats, "client_portal.dat"));
        var map = new FakeMap
        {
            View = new RetailMapView(Yaraq, "21.5S, 1.7W", new Point(125, 159), new Point(168, 81)),
            Lines = new[] { "You do not currently own a house.", "You may buy another landscape house at 11/4/2026 10:08:29 AM. This restriction does not apply to apartments." }
        };
        foreach (var (name, tab) in new[] { ("map", 0), ("house", 1) })
        {
            MapWindow view = null!;
            using var panel = AvaloniaPanel.Create(() => (view = new MapWindow(map, portal)).Window, MapWindow.Width, MapWindow.Height);
            view.ShowTab(tab);
            panel.Tick();
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, name + ".png"));
        }
    });

    private static List<string?> Texts(MapWindow view) =>
        view.Window.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();

    private sealed class FakeMap : IRetailMap
    {
        public RetailMapView? View;
        public IReadOnlyList<string> Lines = new[] { "You do not currently own a house." };
        public RetailMapView? Read() => View;
        public IReadOnlyList<string> HouseLines() => Lines;
        public IReadOnlyList<MapPlace> Places { get; } = new[] { new MapPlace("Yaraq", new Rect(121, 156, 7, 6)) };
    }
}
