using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Dereth radar: a round minimap in a gold bezel, drawn over the land around the player, heading-up as retail's is. A heading tape
/// reads the heading in degrees above it, and the coordinates sit below. Dots are in retail's radar colours; hovering names one, a click
/// selects it, the wheel zooms, and the bezel holds retail's UI lock and an options popover. It reads the game once a frame through
/// <see cref="Refresh"/>; every other change is made from what was read last.
/// </summary>
public sealed class RetailRadarWindow : Decorator, IDisposable
{
    /// <summary>How much larger than its layout the radar draws: the whole radar, bezel and controls included, at twice its layout size.</summary>
    public const int Scale = 2;
    // The layout's size, before the scale.
    private const int LayoutWidth = 146, LayoutHeight = 174;
    /// <summary>The window's design size in DIPs, the layout at its scale.</summary>
    public const int DesignWidth = LayoutWidth * Scale, DesignHeight = LayoutHeight * Scale;
    /// <summary>Where the scope's top-left sits in the layout, in layout units (a window DIP is this times <see cref="Scale"/>).</summary>
    public static readonly Point ScopeOrigin = new(14, 28);
    // The scope's diameter: 118 DIPs across, so its radius is 59.
    private const double Diameter = 118;
    private const double Radius = Diameter / 2;
    private const string LockTip = "Lock UI", UnlockTip = "Unlock UI", OptionsTip = "Radar options";
    private static readonly IBrush Plaque = DerethPalette.Brush(Avalonia.Media.Color.FromArgb(0xD9, 8, 16, 22));
    private static readonly IBrush GoldDark = DerethPalette.Brush(Avalonia.Media.Color.Parse("#7A5E30"));
    private static readonly IBrush Groove = DerethPalette.Brush(Avalonia.Media.Color.Parse("#0B1117"));

    private readonly IRadarPort _port;
    private readonly Func<GroundTiles?> _ground;
    private readonly Action<RadarOptions> _saved;
    private readonly Func<uint> _selected;
    private readonly Action _startMove;
    // Each landblock's tile as a bitmap, converted once per tile and dropped once it leaves the nine around the player.
    private readonly Dictionary<ushort, (GameImage Source, WriteableBitmap Bitmap)> _bitmaps = new();
    // The tiles drawn last read, and the landblocks among them; both refilled each read, so the lists are kept rather than made anew. The
    // scope is given a copy only when the tiles change, and the dots only when they change, so a still radar draws no new frame.
    private readonly List<(IImage Image, Rect Metres)> _drawnTiles = new();
    private readonly HashSet<ushort> _wanted = new();
    private readonly DerethRadarScope _scope = new() { Width = Diameter, Height = Diameter, Margin = new Thickness(ScopeOrigin.X, ScopeOrigin.Y, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly DerethRadarBezel _bezel;
    private readonly DerethHeadingTape _tape = new();
    private readonly TextBlock _coordinates;
    private readonly Border _coordinatesPlaque;
    private readonly DerethMedallion _lock;
    private readonly DerethMedallion _gear;
    private readonly Control _popover;
    private readonly OptionRow _groundRow, _northRow, _ringsRow, _zoomRow;
    private RadarPlayer? _player;
    private IReadOnlyList<RadarObject> _objects = Array.Empty<RadarObject>();
    private RadarScopeFrame? _frame;
    private uint? _hovered;
    private uint? _pressed;
    private int _tilesDrawn;
    // One tile is converted to a bitmap per read; the rest wait for later reads.
    private bool _convertedThisRead;
    private bool _disposed;

    public RadarOptions Options { get; private set; }

    /// <summary>What the radar drew last: its dots, the ground and the coordinates.</summary>
    public RadarScopeFrame? Frame => _frame;

    /// <summary>The hovered dot's label, as the scope draws it; null when no dot is hovered.</summary>
    public string? HoverLabel { get; private set; }

    /// <summary>How many landblocks of ground the radar drew last: nine outdoors with the ground on and built, else none.</summary>
    public int TilesDrawn => _tilesDrawn;

    /// <param name="port">The radar's game.</param>
    /// <param name="ground">The ground's tiles, or null until they can be built.</param>
    /// <param name="options">The options kept for this character.</param>
    /// <param name="saved">Told each time an option or the zoom changes, to keep it for the character.</param>
    /// <param name="selected">The selected object's id, or 0, for the teal ring.</param>
    /// <param name="startMove">Starts the window's move, from a press on the frame when the UI is not locked.</param>
    public RetailRadarWindow(IRadarPort port, Func<GroundTiles?> ground, RadarOptions options, Action<RadarOptions> saved, Func<uint> selected, Action startMove)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _ground = ground ?? throw new ArgumentNullException(nameof(ground));
        Options = options;
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _startMove = startMove ?? throw new ArgumentNullException(nameof(startMove));

        _bezel = new DerethRadarBezel { Width = Diameter + 22, Height = Diameter + 22, Margin = new Thickness(ScopeOrigin.X - 11, ScopeOrigin.Y - 11, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        _coordinates = new TextBlock { FontFamily = DerethPalette.Title, FontSize = 10, Foreground = DerethPalette.CreamBrush };
        _coordinatesPlaque = new Border
        {
            CornerRadius = new CornerRadius(9), Background = Plaque, BorderBrush = GoldDark, BorderThickness = new Thickness(1), Padding = new Thickness(8, 1),
            Margin = new Thickness(0, ScopeOrigin.Y + Diameter + 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Child = _coordinates
        };
        var tape = new Border
        {
            Width = 106, Height = 18, CornerRadius = new CornerRadius(3), Background = Plaque, BorderBrush = GoldDark, BorderThickness = new Thickness(1),
            Margin = new Thickness(20, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Child = _tape
        };
        _lock = new DerethMedallion(DerethGlyph.Unlock, 15, 9) { Margin = new Thickness(ScopeOrigin.X + 4, ScopeOrigin.Y + 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _lock.Clicked += () => { _port.UiLocked = !_port.UiLocked; Rebuild(); };
        _gear = new DerethMedallion(DerethGlyph.Options, 15, 9) { Margin = new Thickness(ScopeOrigin.X + Diameter - 19, ScopeOrigin.Y + 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _gear.Clicked += ToggleOptions;
        ToolTip.SetTip(_lock, LockTip);
        ToolTip.SetTip(_gear, OptionsTip);

        _groundRow = new OptionRow("Show ground", () => Change(Options with { Ground = !Options.Ground }));
        _northRow = new OptionRow("North up", () => Change(Options with { NorthUp = !Options.NorthUp }));
        _ringsRow = new OptionRow("Range rings", () => Change(Options with { Rings = !Options.Rings }));
        _zoomRow = new OptionRow("Zoom", () => Change(Options with { Zoom = RadarZoom.Next(Options.Zoom) }));
        _popover = new DerethFrame(DerethFrameArt.Window)
        {
            Width = 112, Margin = new Thickness(26, ScopeOrigin.Y + 34, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Child = new StackPanel { Children = { _groundRow, _northRow, _ringsRow, _zoomRow } }
        };

        _scope.PointerMoved += (_, e) => Hover(e.GetPosition(_scope));
        _scope.PointerExited += (_, _) => { if (_hovered != null) { _hovered = null; Rebuild(); } };
        _scope.PointerPressed += (_, e) =>
        {
            CloseOptions();
            var hit = _frame == null ? null : RadarScope.Hit(_frame, e.GetPosition(_scope));
            // A press that is not on a dot is left to the frame, which moves the radar.
            if (hit == null) return;
            _pressed = hit.Id;
            e.Handled = true;
        };
        _scope.PointerReleased += (_, e) =>
        {
            if (_pressed is not { } pressed) return;
            _pressed = null;
            e.Handled = true;
            if (_frame != null && RadarScope.Hit(_frame, e.GetPosition(_scope))?.Id == pressed) _port.Select(pressed);
        };
        _scope.PointerWheelChanged += (_, e) =>
        {
            var notches = (int)Math.Round(e.Delta.Y);
            if (notches == 0) return;
            e.Handled = true;
            Change(Options with { Zoom = RadarZoom.Wheel(Options.Zoom, notches) });
        };

        var grid = new Grid
        {
            Width = LayoutWidth, Height = LayoutHeight, Background = Brushes.Transparent,
            Children = { _scope, _bezel, tape, _coordinatesPlaque, _lock, _gear, _popover }
        };
        // A press on the frame, where no dot or control takes it, closes the options and moves the radar unless the UI is locked.
        grid.PointerPressed += (_, e) =>
        {
            CloseOptions();
            if (!e.Handled && !_port.UiLocked) _startMove();
        };
        Child = new LayoutTransformControl { LayoutTransform = new ScaleTransform(Scale, Scale), Child = grid };
    }

    /// <summary>Reads the player and the objects around them, and draws the radar. The host calls it once a frame.</summary>
    public void Refresh()
    {
        _player = _port.Player;
        _objects = _player == null ? Array.Empty<RadarObject>() : _port.Objects;
        _convertedThisRead = false;
        Rebuild();
    }

    /// <summary>Frees the ground's bitmaps. The window draws nothing after this, and a later read converts none.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _drawnTiles.Clear();
        _scope.Tiles = Array.Empty<(IImage Image, Rect Metres)>();
        foreach (var (_, bitmap) in _bitmaps.Values) bitmap.Dispose();
        _bitmaps.Clear();
    }

    private void Change(RadarOptions options)
    {
        Options = options;
        _saved(options);
        Rebuild();
    }

    private void Hover(Point point)
    {
        var id = _frame == null ? null : RadarScope.Hit(_frame, point)?.Id;
        if (id == _hovered) return;
        _hovered = id;
        Rebuild();
    }

    private void ToggleOptions()
    {
        _popover.IsVisible = !_popover.IsVisible;
        _gear.IsOpen = _popover.IsVisible;
    }

    private void CloseOptions()
    {
        if (!_popover.IsVisible) return;
        _popover.IsVisible = false;
        _gear.IsOpen = false;
    }

    // Draws the radar from the last read, with the options and hover as they are now. Makes no game calls, so the events can use it.
    private void Rebuild()
    {
        var frame = RadarScope.Build(_player, _objects, Options, Radius);
        _frame = frame;
        var tiles = GroundFor(frame);
        _tilesDrawn = tiles.Count;
        if (!tiles.SequenceEqual(_scope.Tiles)) _scope.Tiles = tiles.ToArray();
        _scope.Turn = frame.Turn;
        _scope.PixelsPerMetre = frame.PixelsPerMetre;
        _scope.Rings = Options.Rings;
        _scope.Arrow = Options.NorthUp ? frame.Heading : 0;

        var selected = _selected();
        var dots = new (Point At, Color Colour, double Opacity, bool Selected, bool Hovered)[frame.Dots.Count];
        for (var i = 0; i < dots.Length; i++)
        {
            var dot = frame.Dots[i];
            dots[i] = (dot.At, RadarColors.Rgb(dot.Colour), dot.Alpha, dot.Id == selected, dot.Id == _hovered);
        }
        if (!dots.SequenceEqual(_scope.Dots)) _scope.Dots = dots;
        var hovered = _hovered is { } id ? frame.Dots.FirstOrDefault(dot => dot.Id == id) : null;
        (string Text, Point At)? label = hovered == null ? null : (RadarScope.Label(hovered), hovered.At);
        HoverLabel = label?.Text;
        _scope.Label = label;

        _bezel.Turn = frame.Turn;
        _tape.Heading = frame.Heading;
        _coordinates.Text = frame.Coordinates ?? string.Empty;
        _coordinatesPlaque.IsVisible = frame.Coordinates != null;
        // Without retail's lock natives the port's UiLocked does nothing, so the button is not shown.
        _lock.IsVisible = _port.UiLockAvailable;
        // The medallion redraws on every set, so it is set only when the lock changes.
        var locked = _port.UiLocked;
        if (_lock.IsOpen != locked)
        {
            _lock.Glyph = locked ? DerethGlyph.Lock : DerethGlyph.Unlock;
            _lock.IsOpen = locked;
            ToolTip.SetTip(_lock, locked ? UnlockTip : LockTip);
        }

        _groundRow.Show(Options.Ground);
        _northRow.Show(Options.NorthUp);
        _ringsRow.Show(Options.Rings);
        _zoomRow.Show(null, $"{Options.Zoom} m");
    }

    // The tiles of the ground that are built, each with its bitmap. Outdoors only, and only while the ground is on.
    private List<(IImage Image, Rect Metres)> GroundFor(RadarScopeFrame frame)
    {
        _drawnTiles.Clear();
        _wanted.Clear();
        var ground = frame.Tiles.Count > 0 ? _ground() : null;
        if (ground != null && _player != null)
        {
            // Follow drops the landblocks that left the nine, and starts the new ones; it is cheap while the player stays in one.
            ground.Follow((ushort)(_player.Position.Cell >> 16));
            foreach (var tile in frame.Tiles)
            {
                if (ground.Tile(tile.Landblock) is not { } image || BitmapOf(tile.Landblock, image) is not { } bitmap) continue;
                _drawnTiles.Add((bitmap, tile.Metres));
                _wanted.Add(tile.Landblock);
            }
        }
        // Each drawn tile holds one bitmap, so more bitmaps than drawn tiles means some landblock left: only then is the dictionary swept.
        if (_bitmaps.Count > _wanted.Count)
            foreach (var landblock in _bitmaps.Keys.ToArray())
            {
                if (_wanted.Contains(landblock)) continue;
                _bitmaps[landblock].Bitmap.Dispose();
                _bitmaps.Remove(landblock);
            }
        return _drawnTiles;
    }

    // A landblock's bitmap, converted once for each image the ground builds. One conversion is made a read, so a landblock crossing
    // does not convert nine tiles on the UI thread at once: the tiles not converted yet are left out until a later read.
    private WriteableBitmap? BitmapOf(ushort landblock, GameImage image)
    {
        if (_disposed) return null;
        if (_bitmaps.TryGetValue(landblock, out var known) && ReferenceEquals(known.Source, image)) return known.Bitmap;
        if (_convertedThisRead) return null;
        _convertedThisRead = true;
        var bitmap = GameArtImageExtension.CreateBitmap(image);
        if (bitmap == null) return null;
        if (known.Bitmap != null) known.Bitmap.Dispose();
        _bitmaps[landblock] = (image, bitmap);
        return bitmap;
    }

    /// <summary>One line of the options popover: a check box (or none) and its value.</summary>
    private sealed class OptionRow : Border
    {
        private readonly Border _mark = new() { Width = 5, Height = 5, CornerRadius = new CornerRadius(1), Background = DerethPalette.GoldBrush, IsVisible = false };
        private readonly TextBlock _value = new() { FontFamily = DerethPalette.Title, FontSize = 10, Foreground = DerethPalette.MutedBrush, Margin = new Thickness(6, 0, 0, 0) };

        public OptionRow(string label, Action activate)
        {
            Background = Brushes.Transparent;
            Height = 16;
            var box = new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(2), BorderBrush = GoldDark, BorderThickness = new Thickness(1), Background = Groove,
                Child = _mark, VerticalAlignment = VerticalAlignment.Center
            };
            var text = new TextBlock { Text = label, FontFamily = DerethPalette.Title, FontSize = 10, Foreground = DerethPalette.CreamBrush, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel();
            DockPanel.SetDock(box, Dock.Left);
            DockPanel.SetDock(_value, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(_value);
            row.Children.Add(text);
            Child = row;
            // The press is the row's; the release over the row is the click.
            PointerPressed += (_, e) => e.Handled = true;
            PointerReleased += (_, e) =>
            {
                e.Handled = true;
                if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) activate();
            };
        }

        /// <param name="on">Whether the option is on; null for a row with a value and no check.</param>
        /// <param name="value">The value shown at the right, or null.</param>
        public void Show(bool? on, string? value = null)
        {
            _mark.IsVisible = on == true;
            _value.Text = value ?? string.Empty;
        }
    }
}
