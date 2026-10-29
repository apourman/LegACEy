using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// Retail's map page as its own update leaves it: the date and time lines, the player's coordinates (empty indoors), and the centres of
/// the player and house markers in map pixels, null while retail hides them.
/// </summary>
public sealed record RetailMapView(string DateTime, string Coordinates, Point? Player, Point? House);

/// <summary>A place retail names when the mouse rests on it: its box on the map, in map pixels.</summary>
public readonly record struct MapPlace(string Name, Rect Area);

/// <summary>What the map window reads from retail's map and house pages. The client implements it over retail's own panel; tests fake it.</summary>
public interface IRetailMap
{
    /// <summary>The map page brought up to date, or null when retail's page cannot be read.</summary>
    RetailMapView? Read();
    /// <summary>The lines retail's house page shows, in order.</summary>
    IReadOnlyList<string> HouseLines();
    /// <summary>The places retail names on its map.</summary>
    IReadOnlyList<MapPlace> Places { get; }
}

/// <summary>
/// The Dereth map window, retail's Map and House tabs laid out as retail lays them: the date and time, retail's map with the player and
/// house markers, the coordinates; and the house page's lines. Retail's place names show when the mouse rests on a place. The map grows
/// with the window. One instance serves the session; <see cref="Refresh"/> redraws.
/// </summary>
public sealed class MapWindow
{
    public const string WindowId = "map";
    public const int Width = 310, Height = 400;
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(Width, Height));
    /// <summary>Retail's map art: the parchment map of Dereth, 257 by 267, its towns drawn in.</summary>
    public const uint MapArt = 0x0600127D;
    private const double MapWidth = 257, MapHeight = 267, MarkerSize = 14;
    private static readonly string[] TabNames = { "Map", "House" };
    private static readonly IBrush PlayerBrush = DerethPalette.Brush(Color.Parse("#46E03A"));
    private static readonly IBrush LabelBrush = DerethPalette.Brush(Color.FromArgb(0xE6, 0x10, 0x0E, 0x0A));

    private readonly IRetailMap _map;
    private readonly DerethSegmented _tabs;
    private readonly ContentControl _page;
    private readonly Control[] _pages;
    private readonly TextBlock _date, _time, _coordinates, _placeName;
    private readonly Border _placeLabel;
    private readonly Control _player, _house;
    private readonly StackPanel _houseLines = new() { Spacing = 10, Margin = new Thickness(10, 8) };
    private string _drawnHouse = string.Empty;
    private int _tab;

    public MapWindow(IRetailMap map, IGameArtSource? art)
    {
        _map = map;
        _tabs = new DerethSegmented(TabNames, cellPadding: new Thickness(4, 4)) { VerticalAlignment = VerticalAlignment.Center };
        _tabs.Chosen += ShowTab;

        // The map: retail's art, a hover box on each place it names, then the house and player markers and the place's name above them.
        var canvas = new Canvas { Width = MapWidth, Height = MapHeight };
        if (GameArtImageExtension.CreateBitmap(art, MapArt) is { } bitmap)
        {
            var image = new Image { Source = bitmap, Width = MapWidth, Height = MapHeight };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            canvas.Children.Add(image);
        }
        _placeName = new TextBlock { FontFamily = DerethPalette.Body, FontSize = 11, Foreground = DerethPalette.CreamBrush };
        _placeLabel = new Border
        {
            Background = LabelBrush, BorderBrush = DerethPalette.GoldDarkBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 1), Child = _placeName, IsVisible = false, IsHitTestVisible = false
        };
        foreach (var place in map.Places) canvas.Children.Add(Hover(place));
        _house = Marker(new Polygon
        {
            Points = new Points { new(7, 1), new(13, 7), new(7, 13), new(1, 7) },
            Fill = DerethPalette.GoldWashBrush, Stroke = DerethPalette.GoldBrush, StrokeThickness = 2
        });
        _player = Marker(new Ellipse { Stroke = PlayerBrush, StrokeThickness = 2.5 });
        canvas.Children.Add(_house);
        canvas.Children.Add(_player);
        canvas.Children.Add(_placeLabel);

        _date = Line(13);
        _time = Line(13);
        _coordinates = Line(14);
        var lines = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        lines.Children.Add(_date);
        lines.Children.Add(_time);
        var mapPage = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        mapPage.Children.Add(lines);
        var scaled = new Viewbox { Stretch = Stretch.Uniform, Child = canvas, Margin = new Thickness(6, 0) };
        Grid.SetRow(scaled, 1);
        mapPage.Children.Add(scaled);
        _coordinates.Margin = new Thickness(0, 4, 0, 6);
        Grid.SetRow(_coordinates, 2);
        mapPage.Children.Add(_coordinates);

        var housePage = new ScrollViewer
        {
            Content = _houseLines, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _pages = new Control[] { mapPage, housePage };
        _page = new ContentControl { Margin = new Thickness(0, 5, 0, 0) };
        // The tabs sit in the header beside the close box, as the character and social windows' do.
        Window = new DerethWindow(string.Empty, null, _page, _tabs, stretchActions: true);
        Window.CloseRequested += (_, _) => Close?.Invoke();
        ShowTab(0);
    }

    public DerethWindow Window { get; }

    /// <summary>Runs when the player closes the window with its close box.</summary>
    public Action? Close { get; set; }

    public void ShowTab(int tab)
    {
        _tab = tab;
        _tabs.Selected = tab;
        _page.Content = _pages[tab];
        Refresh();
    }

    /// <summary>Reads retail's map or house page again and redraws it. Call it about once a second while the window is open.</summary>
    public void Refresh()
    {
        if (_tab == 0) DrawMap();
        else DrawHouse();
    }

    private void DrawMap()
    {
        var view = _map.Read();
        var lines = view?.DateTime.Split('\n') ?? Array.Empty<string>();
        _date.Text = lines.Length > 0 ? lines[0] : string.Empty;
        _time.Text = lines.Length > 1 ? lines[1] : string.Empty;
        _coordinates.Text = view?.Coordinates ?? string.Empty;
        Place(_player, view?.Player);
        Place(_house, view?.House);
    }

    private void DrawHouse()
    {
        var lines = _map.HouseLines();
        var key = string.Join("\n", lines);
        if (key == _drawnHouse) return;
        _drawnHouse = key;
        _houseLines.Children.Clear();
        foreach (var line in lines)
        {
            var text = Line(13);
            text.Text = line;
            text.TextWrapping = TextWrapping.Wrap;
            _houseLines.Children.Add(text);
        }
    }

    private static void Place(Control marker, Point? centre)
    {
        marker.IsVisible = centre != null;
        if (centre is not { } at) return;
        Canvas.SetLeft(marker, at.X - MarkerSize / 2);
        Canvas.SetTop(marker, at.Y - MarkerSize / 2);
    }

    /// <summary>A clear box over a place: while the mouse rests on it, the place's name shows above it.</summary>
    private Control Hover(MapPlace place)
    {
        var box = new Border { Width = place.Area.Width, Height = place.Area.Height, Background = Brushes.Transparent };
        Canvas.SetLeft(box, place.Area.X);
        Canvas.SetTop(box, place.Area.Y);
        box.PointerEntered += (_, _) =>
        {
            _placeName.Text = place.Name;
            _placeLabel.Measure(Size.Infinity);
            var size = _placeLabel.DesiredSize;
            // Centred above the place, kept on the map.
            Canvas.SetLeft(_placeLabel, Math.Max(0, Math.Min(place.Area.Center.X - size.Width / 2, MapWidth - size.Width)));
            Canvas.SetTop(_placeLabel, place.Area.Y >= size.Height + 2 ? place.Area.Y - size.Height - 2 : place.Area.Bottom + 2);
            _placeLabel.IsVisible = true;
        };
        box.PointerExited += (_, _) => _placeLabel.IsVisible = false;
        return box;
    }

    private static Control Marker(Shape shape)
    {
        shape.Width = shape.Height = MarkerSize;
        shape.IsVisible = false;
        shape.IsHitTestVisible = false;
        return shape;
    }

    private static TextBlock Line(double size) => new()
    {
        FontFamily = DerethPalette.Title, FontSize = size, Foreground = DerethPalette.TextBrush,
        HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center
    };
}
