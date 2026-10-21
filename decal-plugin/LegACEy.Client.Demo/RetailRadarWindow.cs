using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Dereth radar: a round minimap in a gold bezel, drawn over the land around the player, or indoors over the floor plan of the place
/// they are in, heading-up as retail's is, with a level ladder left of the bezel. A heading tape reads the heading in degrees above it, and the
/// coordinates sit below. Dots are in retail's radar colours; hovering names one, a click selects it, the wheel zooms, and the bezel holds
/// retail's UI lock and an options popover. It reads the game once a frame through <see cref="Refresh"/>; every other change is made from
/// what was read last.
/// </summary>
public sealed class RetailRadarWindow : Decorator, IDisposable
{
    /// <summary>
    /// How much larger than its layout the radar opens: the whole radar, bezel and controls included, at twice its layout size. Resizing the
    /// window draws it larger or smaller, from <see cref="MinimumScale"/> to <see cref="MaximumScale"/>, keeping its shape.
    /// </summary>
    public const int Scale = 2;
    private const double MinimumScale = 1, MaximumScale = 3;
    // The room left of the bezel for the level ladder. It is always there, so the window is the same size indoors and out: the ladder is
    // shown only where a plan is drawn, and the scope does not move when the player walks in or out.
    private const int LadderSpace = 22;
    // The layout's size, before the scale.
    private const int LayoutWidth = 146 + LadderSpace, LayoutHeight = 174;
    /// <summary>The window's design size in DIPs, the layout at its scale.</summary>
    public const int DesignWidth = LayoutWidth * Scale, DesignHeight = LayoutHeight * Scale;
    /// <summary>Where the scope's top-left sits in the layout, in layout units (a window DIP is this times the radar's scale).</summary>
    public static readonly Point ScopeOrigin = new(14 + LadderSpace, 28);
    // The scope's diameter: 118 DIPs across, so its radius is 59.
    private const double Diameter = 118;
    private const double Radius = Diameter / 2;
    /// <summary>
    /// The ladder's plaque in the layout: 16 units wide, its height the scope's less 14 at each end, and ending 3 units short of the bezel's box,
    /// which starts 11 units left of the scope.
    /// </summary>
    public static readonly Rect LadderBounds = new(ScopeOrigin.X - 11 - 3 - 16, ScopeOrigin.Y + 14, 16, Diameter - 28);
    private const string LockTip = "Lock UI", UnlockTip = "Unlock UI", OptionsTip = "Radar options";
    private static readonly IBrush Plaque = DerethPalette.Brush(Avalonia.Media.Color.FromArgb(0xD9, 0x18, 0x16, 0x0F));
    private static readonly IBrush GoldDark = DerethPalette.Brush(Avalonia.Media.Color.Parse("#8A6326"));
    private static readonly IBrush Groove = DerethPalette.Brush(Avalonia.Media.Color.Parse("#0A0906"));

    private readonly IRadarPort _port;
    private readonly Func<GroundTiles?> _ground;
    private readonly Func<IndoorPlanCache?> _plans;
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
    private readonly DerethLevelLadder _ladder;
    // The level the pointer is on in the ladder, or -1: the radar peeks at it while the ladder shows.
    private int _peek = -1;
    // The resize corners, shown while the pointer is over the radar and the UI is unlocked.
    private readonly DerethCornerMarks _corners = new() { IsVisible = false };
    private bool _pointerOver;
    private readonly DerethMedallion _gear;
    private readonly Control _popover;
    private readonly OptionRow _mapRow, _northRow, _ringsRow, _zoomRow;
    private RadarPlayer? _player;
    private IReadOnlyList<RadarObject> _objects = Array.Empty<RadarObject>();
    // The place the player is in and the landblock's plan it came from, kept while the player stays in it; and the place drawn this read.
    private RadarPlan? _place;
    private IndoorPlan? _placeLandblock;
    private uint _placeCell;
    private RadarPlan? _plan;
    // The scope's copy of the plan's levels, made again only when the frame's list of them changes.
    private IReadOnlyList<RadarPlanDraw>? _planDrawn;
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

    /// <summary>
    /// How many landblocks of ground the radar drew last: nine outdoors, or under a building's plan, with the map on and built; else none.
    /// </summary>
    public int TilesDrawn => _tilesDrawn;

    /// <param name="port">The radar's game.</param>
    /// <param name="ground">The ground's tiles, or null until they can be built.</param>
    /// <param name="plans">The indoor plans, or null until the DATs they are read from can be opened.</param>
    /// <param name="options">The options kept for this character.</param>
    /// <param name="saved">Told each time an option or the zoom changes, to keep it for the character.</param>
    /// <param name="selected">The selected object's id, or 0, for the green ring.</param>
    /// <param name="startMove">Starts the window's move, from a press on the frame when the UI is not locked.</param>
    public RetailRadarWindow(IRadarPort port, Func<GroundTiles?> ground, Func<IndoorPlanCache?> plans, RadarOptions options, Action<RadarOptions> saved,
        Func<uint> selected, Action startMove)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _ground = ground ?? throw new ArgumentNullException(nameof(ground));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        Options = options;
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _startMove = startMove ?? throw new ArgumentNullException(nameof(startMove));

        _bezel = new DerethRadarBezel { Width = Diameter + 22, Height = Diameter + 22, Margin = new Thickness(ScopeOrigin.X - 11, ScopeOrigin.Y - 11, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        _coordinates = DerethPalette.CentreInk(new TextBlock { FontFamily = DerethPalette.Title, FontSize = 10, Foreground = DerethPalette.CreamBrush });
        _coordinatesPlaque = new Border
        {
            CornerRadius = new CornerRadius(9), Background = Plaque, BorderBrush = GoldDark, BorderThickness = new Thickness(1), Padding = new Thickness(8, 1),
            Margin = new Thickness(LadderSpace, ScopeOrigin.Y + Diameter + 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand), Child = _coordinates
        };
        ToolTip.SetTip(_coordinatesPlaque, "Click to paste in chat");
        // The press is the plaque's, so it does not move the radar; the release over it is the click.
        _coordinatesPlaque.PointerPressed += (_, e) => { CloseOptions(); e.Handled = true; };
        _coordinatesPlaque.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            if (new Rect(_coordinatesPlaque.Bounds.Size).Contains(e.GetPosition(_coordinatesPlaque)) && _coordinates.Text is { Length: > 0 } text)
                CoordinatesClicked?.Invoke(text);
        };
        var tape = new Border
        {
            Width = 106, Height = 18, CornerRadius = new CornerRadius(3), Background = Plaque, BorderBrush = GoldDark, BorderThickness = new Thickness(1),
            Margin = new Thickness(20 + LadderSpace, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Child = _tape
        };
        _ladder = new DerethLevelLadder
        {
            Width = LadderBounds.Width, Height = LadderBounds.Height, Margin = new Thickness(LadderBounds.X, LadderBounds.Y, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsVisible = false
        };
        _ladder.PeekChanged += level =>
        {
            if (level == _peek) return;
            _peek = level;
            Rebuild();
        };
        _lock = new DerethMedallion(DerethGlyph.Unlock, 15, 9) { Margin = new Thickness(ScopeOrigin.X + 4, ScopeOrigin.Y + 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _lock.Clicked += () => { _port.UiLocked = !_port.UiLocked; Rebuild(); };
        _gear = new DerethMedallion(DerethGlyph.Options, 15, 9) { Margin = new Thickness(ScopeOrigin.X + Diameter - 19, ScopeOrigin.Y + 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _gear.Clicked += ToggleOptions;
        ToolTip.SetTip(_lock, LockTip);
        ToolTip.SetTip(_gear, OptionsTip);

        _mapRow = new OptionRow("Show map", () => Change(Options with { Map = !Options.Map }));
        _northRow = new OptionRow("North up", () => Change(Options with { NorthUp = !Options.NorthUp }));
        _ringsRow = new OptionRow("Range rings", () => Change(Options with { Rings = !Options.Rings }));
        _zoomRow = new OptionRow("Zoom", () => Change(Zoomed((range, indoors) => RadarZoom.Next(range, indoors))));
        _popover = new DerethFrame(DerethFrameArt.Window)
        {
            Width = 112, Margin = new Thickness(26 + LadderSpace, ScopeOrigin.Y + 34, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Child = new StackPanel { Children = { _mapRow, _northRow, _ringsRow, _zoomRow } }
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
            Change(Zoomed((range, indoors) => RadarZoom.Wheel(range, notches, indoors)));
        };

        var grid = new Grid
        {
            Width = LayoutWidth, Height = LayoutHeight, Background = Brushes.Transparent,
            Children = { _scope, _bezel, tape, _coordinatesPlaque, _ladder, _lock, _gear, _popover, _corners }
        };
        grid.PointerEntered += (_, _) => { _pointerOver = true; ShowCorners(); };
        grid.PointerExited += (_, _) => { _pointerOver = false; ShowCorners(); };
        // A press on the frame, where no dot or control takes it, closes the options and moves the radar unless the UI is locked.
        grid.PointerPressed += (_, e) =>
        {
            CloseOptions();
            if (!e.Handled && !_port.UiLocked) _startMove();
        };
        Child = new HeightScaled(LayoutHeight) { Child = grid };
    }

    /// <summary>The coordinates plaque was clicked, with the coordinates it shows.</summary>
    public event Action<string>? CoordinatesClicked;

    /// <summary>Whether the resize corners show: while the pointer is over the radar and the UI is unlocked.</summary>
    public bool CornersVisible => _corners.IsVisible;

    private void ShowCorners() => _corners.IsVisible = _pointerOver && !_port.UiLocked;

    /// <summary>How the radar resizes: from its layout size to three times it, keeping its shape, unless retail's UI is locked.</summary>
    public static WindowResizing Resizing(Func<bool> uiLocked) => new(
        new System.Drawing.Size((int)(LayoutWidth * MinimumScale), (int)(LayoutHeight * MinimumScale)),
        new System.Drawing.Size((int)(LayoutWidth * MaximumScale), (int)(LayoutHeight * MaximumScale)),
        uiLocked, shape: ((double)LayoutWidth / LayoutHeight, (double)LayoutWidth / LayoutHeight));

    /// <summary>Reads the player and the objects around them, and draws the radar. The host calls it once a frame.</summary>
    public void Refresh()
    {
        _player = _port.Player;
        _objects = _player == null ? Array.Empty<RadarObject>() : _port.Objects;
        _plan = PlaceOf(_player);
        _convertedThisRead = false;
        Rebuild();
    }

    // The indoor place the player is in. It is kept while the player's cell stays in it, so its levels are made once for the place. Null
    // outdoors, and while the landblock's plan is being read: the plan is only asked for, never waited for, so the UI thread does not
    // stall on the DATs.
    private RadarPlan? PlaceOf(RadarPlayer? player)
    {
        if (player is not { Position.Outdoors: false } || _plans() is not { } plans) return null;
        var cell = player.Position.Cell;
        var landblock = plans.PlanAsync((int)(cell >> 24), (int)((cell >> 16) & 0xFF));
        if (landblock.Status != System.Threading.Tasks.TaskStatus.RanToCompletion) return null;
        var low = (ushort)(cell & 0xFFFF);
        var kept = _place != null && ReferenceEquals(_placeLandblock, landblock.Result) && (cell == _placeCell || _place.Contains(low));
        if (!kept)
        {
            _place = new RadarPlan(landblock.Result, low);
            _placeLandblock = landblock.Result;
        }
        _placeCell = cell;
        return _place;
    }

    private bool Indoors => _player is { Position.Outdoors: false };

    // The options with the zoom that is in use changed: indoors its own zoom, outdoors the outdoor one. The wheel and the zoom row use it.
    private RadarOptions Zoomed(Func<int, bool, int> zoom) =>
        Indoors ? Options with { IndoorZoom = zoom(Options.IndoorZoom, true) } : Options with { Zoom = zoom(Options.Zoom, false) };

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

    /// <summary>Takes options set from the Settings window and redraws with them. Nothing is saved here; the caller keeps them.</summary>
    public void SetOptions(RadarOptions options)
    {
        Options = options;
        Rebuild();
    }

    private void Hover(Point point)
    {
        var id = _frame == null ? null : RadarScope.Hit(_frame, point)?.Id;
        if (id == _hovered) return;
        _hovered = id;
        Rebuild();
    }

    // A dot on another level is drawn as an arrow the way its floor points: up for the level above, down for the one below.
    private static int ArrowOf(RadarFloor floor) => floor switch
    {
        RadarFloor.Above => 1,
        RadarFloor.Below => -1,
        _ => 0
    };

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
        var frame = RadarScope.Build(_player, _objects, Options, Radius, _plan, _peek);
        _frame = frame;
        var tiles = GroundFor(frame);
        _tilesDrawn = tiles.Count;
        if (!tiles.SequenceEqual(_scope.Tiles)) _scope.Tiles = tiles.ToArray();
        if (!ReferenceEquals(frame.PlanLevels, _planDrawn))
        {
            _planDrawn = frame.PlanLevels;
            _scope.PlanLevels = frame.PlanLevels.Select(draw => (draw.Level.Floors, draw.Level.Walls, draw.Solid, draw.Peek)).ToArray();
        }
        var stairs = frame.StairMarks.Select(mark => (mark.At, mark.Up)).ToArray();
        if (!stairs.SequenceEqual(_scope.StairMarks)) _scope.StairMarks = stairs;
        _scope.PlanOrigin = frame.PlanOrigin;
        _scope.PlanBuilding = _plan is { Building: true };
        _scope.Turn = frame.Turn;
        _scope.PixelsPerMetre = frame.PixelsPerMetre;
        _scope.Rings = Options.Rings;
        _scope.Arrow = Options.NorthUp ? frame.Heading : 0;

        var selected = _selected();
        var dots = new (Point At, Color Colour, double Opacity, int Arrow, bool Selected, bool Hovered)[frame.Dots.Count];
        for (var i = 0; i < dots.Length; i++)
        {
            var dot = frame.Dots[i];
            dots[i] = (dot.At, RadarColors.Rgb(dot.Colour), dot.Alpha, ArrowOf(dot.Floor), dot.Id == selected, dot.Id == _hovered);
        }
        if (!dots.SequenceEqual(_scope.Dots)) _scope.Dots = dots;
        // The ladder shows where the plan is drawn; a peek lasts only while it does.
        _ladder.IsVisible = frame.Ladder != null;
        if (frame.Ladder is { } ladder)
        {
            _ladder.Levels = ladder.Levels;
            _ladder.Current = ladder.Current;
            _ladder.Peek = ladder.Peek;
            _ladder.Occupied = ladder.Occupied;
        }
        else
        {
            _peek = -1;
        }
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
        ShowCorners();

        _mapRow.Show(Options.Map);
        _northRow.Show(Options.NorthUp);
        _ringsRow.Show(Options.Rings);
        _zoomRow.Show(null, $"{(Indoors ? Options.IndoorZoom : Options.Zoom)} m");
    }

    // The tiles of the ground that are built, each with its bitmap. Outdoors, or a building's under its plan, and only while the map is on.
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
