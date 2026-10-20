using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using LegACEy.Client.Themes;

using WindowPlacement = (System.Drawing.Point Location, System.Drawing.Size? Size);
using WindowHover = (string? WindowId, LegACEy.Client.Demo.WindowEdges Edges);

namespace LegACEy.Client.Demo;

/// <summary>How a window resizes: freely, down to a minimum size, or between a minimum and a maximum.</summary>
public sealed class WindowResizing
{
    /// <param name="maximum">The largest size; null for as big as the screen. A maximum height equal to the minimum fixes the height.</param>
    /// <param name="locked">Whether the window is locked against resizing now, such as while retail's UI is locked. Null: never locked.</param>
    /// <param name="noCorners">
    /// No corner squares: a press near two edges is the edges' press, and the corners are the content's to move from. For a strip whose
    /// content starts its own moves.
    /// </param>
    /// <param name="shape">
    /// The narrowest and widest the window may be for its height, as width over height. A window whose content scales with its height keeps
    /// its content's shape this way; a drag on one axis carries the other along when it would leave the range. Null: any shape.
    /// </param>
    public WindowResizing(Size minimum, Size? maximum = null, Func<bool>? locked = null, bool noCorners = false, (double Narrowest, double Widest)? shape = null)
    {
        if (minimum.Width <= 0 || minimum.Height <= 0) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (maximum is { } top && (top.Width < minimum.Width || top.Height < minimum.Height)) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (shape is { } range && (range.Narrowest <= 0 || range.Widest < range.Narrowest)) throw new ArgumentOutOfRangeException(nameof(shape));
        Minimum = minimum;
        Maximum = maximum;
        Locked = locked;
        NoCorners = noCorners;
        Shape = shape;
    }

    public Size Minimum { get; }
    public Size? Maximum { get; }
    public Func<bool>? Locked { get; }
    public bool NoCorners { get; }
    public (double Narrowest, double Widest)? Shape { get; }

    /// <summary>Whether the height is fixed: the window has no top or bottom edge to drag.</summary>
    public bool FixedHeight => Maximum is { } top && top.Height == Minimum.Height;

    /// <summary>Whether a press on the edges resizes the window now.</summary>
    internal bool CanResize => Locked?.Invoke() != true;

    /// <summary><paramref name="wanted"/>, within the minimum, the maximum and <paramref name="max"/>.</summary>
    public Size Fit(Size wanted, Size max) => Shaped(new(
        Clamp(wanted.Width, Minimum.Width, Limit(Maximum?.Width, max.Width)),
        Clamp(wanted.Height, Minimum.Height, Limit(Maximum?.Height, max.Height))), widthLeads: true, max);

    /// <summary>
    /// <paramref name="size"/> brought into <see cref="Shape"/>: the leading axis stays and the other follows, within the limits; when the
    /// other reaches a limit, the leading axis gives way instead.
    /// </summary>
    internal Size Shaped(Size size, bool widthLeads, Size max)
    {
        if (Shape is not { } range) return size;
        var ratio = (double)size.Width / size.Height;
        var wanted = Math.Max(range.Narrowest, Math.Min(range.Widest, ratio));
        if (wanted == ratio) return size;
        int widest = Limit(Maximum?.Width, max.Width), tallest = Limit(Maximum?.Height, max.Height);
        if (widthLeads)
        {
            var height = Clamp((int)Math.Round(size.Width / wanted), Minimum.Height, tallest);
            return new Size(Clamp(Within(size.Width, height, range), Minimum.Width, widest), height);
        }
        var width = Clamp((int)Math.Round(size.Height * wanted), Minimum.Width, widest);
        var within = Math.Max((int)Math.Ceiling(width / range.Widest), Math.Min((int)Math.Floor(width / range.Narrowest), size.Height));
        return new Size(width, Clamp(within, Minimum.Height, tallest));
    }

    // A width brought within the shape's range for a height.
    private static int Within(int width, int height, (double Narrowest, double Widest) range) =>
        Math.Max((int)Math.Ceiling(height * range.Narrowest), Math.Min((int)Math.Floor(height * range.Widest), width));

    /// <summary>One axis. The minimum wins over the maximum when the screen is too small for it.</summary>
    internal static int Clamp(int wanted, int minimum, int max) => Math.Max(minimum, Math.Min(max, wanted));

    /// <summary>One axis's largest length: its maximum, or the screen's when there is none.</summary>
    internal static int Limit(int? maximum, int screen) => maximum is { } top ? Math.Min(top, screen) : screen;
}

/// <summary>Dimensions and identity for a LegACEy window.</summary>
public sealed class WindowDefinition
{
    /// <param name="theme">The window's own theme, such as Dereth. Null uses the client's theme, which a window with its own theme does not get.</param>
    /// <param name="resizing">How the window resizes from its edges and corners. Null means it does not resize.</param>
    /// <param name="sharesLocationWith">
    /// Another window this one stands in for, such as a compact view of it: this one opens where that one was last left, and
    /// where this one is left is where that one opens. Each keeps its own size.
    /// </param>
    /// <param name="dragsFromContent">
    /// No title bar: a press does not start a move. The window's content starts one with <see cref="WindowManager.BeginMove"/>, on the presses
    /// that should drag it, so the rest of the window (its buttons and slots) stays a click.
    /// </param>
    public WindowDefinition(string id, string title, int width, int height, int titleBarHeight = 28, IClientTheme? theme = null, WindowResizing? resizing = null,
        string? sharesLocationWith = null, bool dragsFromContent = false)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A window id is required.", nameof(id));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (titleBarHeight <= 0 || titleBarHeight > height) throw new ArgumentOutOfRangeException(nameof(titleBarHeight));
        Id = id;
        Title = title ?? string.Empty;
        Width = width;
        Height = height;
        TitleBarHeight = titleBarHeight;
        Theme = theme;
        Resizing = resizing;
        SharesLocationWith = sharesLocationWith;
        DragsFromContent = dragsFromContent;
    }

    public string Id { get; }
    public string Title { get; }
    public int Width { get; }
    public int Height { get; }
    public int TitleBarHeight { get; }
    public IClientTheme? Theme { get; }
    public WindowResizing? Resizing { get; }
    public string? SharesLocationWith { get; }
    public bool DragsFromContent { get; }
}

/// <summary>The live state of one LegACEy window. The preferred location and size are where the window was last left.</summary>
public sealed class ManagedWindow
{
    internal ManagedWindow(WindowDefinition definition, Point location, Size size, Point preferredLocation, Size preferredSize)
    {
        Definition = definition;
        Location = location;
        Size = size;
        PreferredLocation = preferredLocation;
        PreferredSize = preferredSize;
    }

    public WindowDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public int Width => Size.Width;
    public int Height => Size.Height;
    public int TitleBarHeight => Definition.TitleBarHeight;
    public Point Location { get; internal set; }
    internal Point PreferredLocation { get; set; }
    public Size Size { get; internal set; }
    internal Size PreferredSize { get; set; }
    public Rectangle Bounds => new(Location, Size);
}

/// <summary>The edges of a window a pointer is within a few pixels of. A corner is two edges.</summary>
[Flags]
public enum WindowEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

/// <summary>Persistence seam for placements keyed by server, character and window id.</summary>
public interface IWindowPositionStore
{
    WindowPlacement? Load(string server, string character, string windowId);
    void Save(string server, string character, string windowId, WindowPlacement placement);
}

/// <summary>In-memory position store for tests.</summary>
public sealed class MemoryWindowPositionStore : IWindowPositionStore
{
    private readonly Dictionary<string, WindowPlacement> _positions = new(StringComparer.Ordinal);

    public WindowPlacement? Load(string server, string character, string windowId) =>
        _positions.TryGetValue(Key(server, character, windowId), out var placement) ? placement : (WindowPlacement?)null;

    public void Save(string server, string character, string windowId, WindowPlacement placement) =>
        _positions[Key(server, character, windowId)] = placement;

    public WindowPlacement? Get(string server, string character, string windowId) => Load(server, character, windowId);

    private static string Key(string server, string character, string windowId) =>
        server + "\u001f" + character + "\u001f" + windowId;
}

/// <summary>
/// Small, recoverable position file beside the plugin. Each row is <c>server|character|window|x|y</c>, with
/// <c>|width|height</c> appended for a window that resizes. Keys are base64 encoded so server and character names
/// can contain any punctuation. Position-only rows from before sizes were saved load with no size, and a row with a
/// damaged size keeps its position. A malformed line is ignored and never prevents the client from loading a window.
/// </summary>
public sealed class FileWindowPositionStore : IWindowPositionStore
{
    private readonly string _path;
    private readonly object _sync = new();
    // The session's changes that a write could not put in the file: a place, or null for a place forgotten. They win over the file until a
    // write works, and a write that works clears them, since the file then holds them. Rows the file took are never kept here.
    private readonly Dictionary<string, WindowPlacement?> _session = new(StringComparer.Ordinal);

    public FileWindowPositionStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A position file path is required.", nameof(path));
        _path = path;
    }

    public WindowPlacement? Load(string server, string character, string windowId)
    {
        lock (_sync)
        {
            var key = Key(server, character, windowId);
            if (_session.TryGetValue(key, out var held)) return held;
            return Rows() is { } rows && rows.TryGetValue(key, out var placement) ? placement : (WindowPlacement?)null;
        }
    }

    public void Save(string server, string character, string windowId, WindowPlacement placement)
    {
        lock (_sync)
        {
            var key = Key(server, character, windowId);
            // A file that cannot be read is not rewritten: its other rows would be lost with it. The place still holds for this session.
            if (Rows() is { } rows)
            {
                rows[key] = placement;
                if (WriteRows(rows)) return;
            }
            _session[key] = placement;
        }
    }

    /// <summary>
    /// Forgets every window place this server and character saved, except the keys in <paramref name="keep"/>: the per-character options
    /// this store also holds. Other characters' rows stay. Returns false when the file cannot be read or written. If the file cannot be read,
    /// nothing changes. If it can be read but not written, the forget holds for this session only, until a write works.
    /// </summary>
    public bool ForgetPlaces(string server, string character, IEnumerable<string> keep)
    {
        lock (_sync)
        {
            if (Rows() is not { } rows) return false;
            var owned = Encode(server) + "|" + Encode(character) + "|";
            var kept = new HashSet<string>(keep.Select(id => Key(server, character, id)), StringComparer.Ordinal);
            var forgotten = rows.Keys.Where(key => key.StartsWith(owned, StringComparison.Ordinal) && !kept.Contains(key)).ToList();
            foreach (var key in forgotten) rows.Remove(key);
            if (WriteRows(rows)) return true;
            foreach (var key in forgotten) _session[key] = null;
            return false;
        }
    }

    /// <summary>Writes the rows, and on success clears the session's changes: the file now holds them.</summary>
    private bool WriteRows(Dictionary<string, WindowPlacement> rows)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllLines(_path, rows.Select(pair => Format(pair.Key, pair.Value)));
            _session.Clear();
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The file's rows with the session's changes applied, or null when the file exists but cannot be read. A missing file has no rows.</summary>
    private Dictionary<string, WindowPlacement>? Rows()
    {
        var rows = new Dictionary<string, WindowPlacement>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path))
            {
                foreach (var line in File.ReadAllLines(_path))
                {
                    var fields = line.Split('|');
                    if (fields.Length is not (5 or 7) || !int.TryParse(fields[3], out var x) || !int.TryParse(fields[4], out var y))
                        continue;
                    Size? size = null;
                    if (fields.Length == 7 && int.TryParse(fields[5], out var width) && int.TryParse(fields[6], out var height))
                        size = new Size(width, height);
                    rows[fields[0] + "|" + fields[1] + "|" + fields[2]] = (new Point(x, y), size);
                }
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        foreach (var pair in _session)
        {
            if (pair.Value is { } placement) rows[pair.Key] = placement;
            else rows.Remove(pair.Key);
        }
        return rows;
    }

    private static string Format(string key, WindowPlacement placement) =>
        key + "|" + placement.Location.X + "|" + placement.Location.Y +
        (placement.Size is { } size ? "|" + size.Width + "|" + size.Height : string.Empty);

    private static string Key(string server, string character, string windowId) =>
        Encode(server) + "|" + Encode(character) + "|" + Encode(windowId);

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
}

/// <summary>
/// Pure window behavior used by the plugin adapter and the tests. The first item in ZOrder is
/// frontmost. Pointer coordinates are screen coordinates and all returned positions are clamped.
/// A press in a resizable window's side or bottom band, or in its corner square unless it has no corners, resizes it, within its
/// <see cref="WindowResizing"/>: its minimum and maximum hold on every edge, a fixed height has no top or bottom edge, and a locked window
/// does not resize. A press on any window's top edge or in its title bar moves it. While retail's UI is locked no window moves or resizes.
/// </summary>
public sealed class WindowManager
{
    /// <summary>How far in from a window's outside edge a press still counts as that edge.</summary>
    public const int ResizeBand = 6;

    /// <summary>
    /// The requested location of a window that should open centred on the screen, when it has no saved place. It is not a place
    /// on the screen: the window is centred once its size is known.
    /// </summary>
    public static readonly Point CentredLocation = new(int.MinValue, int.MinValue);

    /// <summary>
    /// The corner square's size in screen pixels, which do not scale with the window's UI scale: a press this near both edges of a corner is
    /// that corner. It matches the frame's corner piece, so the grip is as easy to find as the art shows it.
    /// </summary>
    public const int CornerBand = 16;

    /// <summary>How near, in screen pixels, a moved window's edge must come to the screen's edge or another window's to snap to it.</summary>
    public const int SnapDistance = 8;

    private readonly List<ManagedWindow> _windows = new();
    private readonly IWindowPositionStore _positions;
    private readonly string _server;
    private readonly string _character;
    private Size _screen;
    private ManagedWindow? _dragging;
    private Point _dragOffset;
    private WindowEdges _resizeEdges;
    private Point _pressPoint;
    private Rectangle _pressBounds;
    private readonly Func<bool>? _uiLocked;

    /// <param name="scale">
    /// How big windows show, against the sizes their definitions give: 0.85 shows a 400 pixel wide window 340 pixels wide. Sizes,
    /// title bars and minimums are scaled on open; saved sizes stay at design size, so a scale change keeps a window's proportions.
    /// </param>
    /// <param name="uiLocked">Whether retail's UI is locked: then no window moves or resizes, and none shows resize edges. Null: never locked.</param>
    public WindowManager(Size screen, IWindowPositionStore positions, string server, string character, double scale = 1, Func<bool>? uiLocked = null)
    {
        _uiLocked = uiLocked;
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        Scale = scale;
        if (screen.Width <= 0 || screen.Height <= 0) throw new ArgumentOutOfRangeException(nameof(screen));
        _screen = screen;
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _server = server ?? string.Empty;
        _character = character ?? string.Empty;
    }

    public Size Screen => _screen;
    public double Scale { get; }
    public IReadOnlyList<ManagedWindow> ZOrder => _windows;
    public bool IsDragging => _dragging != null;
    /// <summary>The window an edge or corner drag is resizing, or null.</summary>
    public ManagedWindow? Resizing => _resizeEdges == WindowEdges.None ? null : _dragging;
    /// <summary>The window a title-bar drag is moving, or null.</summary>
    public ManagedWindow? Moving => _resizeEdges == WindowEdges.None ? _dragging : null;
    /// <summary>Whether retail's UI is locked, so no window moves or resizes.</summary>
    public bool Locked => _uiLocked?.Invoke() == true;

    public ManagedWindow Open(WindowDefinition definition, Point requestedLocation)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        var existing = Get(definition.Id);
        if (existing != null) return existing;
        definition = Scaled(definition);
        var saved = _positions.Load(_server, _character, definition.Id);
        var shared = definition.SharesLocationWith is { } other ? _positions.Load(_server, _character, other)?.Location : null;
        var preferredSize = definition.Resizing != null && saved?.Size is { } savedSize ? Scaled(savedSize) : DefaultSize(definition);
        var size = FitSize(definition, preferredSize, _screen);
        var requested = requestedLocation == CentredLocation
            ? new Point((_screen.Width - size.Width) / 2, (_screen.Height - size.Height) / 2)
            : requestedLocation;
        var preferredLocation = shared ?? saved?.Location ?? requested;
        var window = new ManagedWindow(definition, Clamp(preferredLocation, size), size, preferredLocation, preferredSize);
        _windows.Insert(0, window);
        return window;
    }

    public ManagedWindow? Get(string id) => _windows.FirstOrDefault(window => window.Id == id);

    public bool Close(string id)
    {
        var window = Get(id);
        if (window == null) return false;
        Save(window);
        if (_dragging == window) EndDrag();
        return _windows.Remove(window);
    }

    public ManagedWindow? HitTest(Point point) =>
        _windows.FirstOrDefault(window => window.Bounds.Contains(point));

    /// <summary>
    /// The window under the pointer and the resize edges it is on. During a resize the dragged window and its edges stay
    /// put, wherever the pointer goes. A title bar, body or move drag gives no edges.
    /// </summary>
    public WindowHover HoverAt(Point point)
    {
        if (_dragging != null)
            return _resizeEdges == WindowEdges.None ? default : (_dragging.Id, _resizeEdges);
        var window = HitTest(point);
        return window == null ? default : (window.Id, Locked ? WindowEdges.None : EdgesAt(window, point));
    }

    /// <summary>
    /// The window whose top edge is under the pointer, where a press moves it; null elsewhere, over a corner or during a drag.
    /// </summary>
    public string? MoveEdgeAt(Point point)
    {
        if (_dragging != null || Locked) return null;
        var window = HitTest(point);
        return window != null && OnMoveEdge(window, point) ? window.Id : null;
    }

    private static bool OnMoveEdge(ManagedWindow window, Point point) =>
        point.Y - window.Location.Y < ResizeBand && EdgesAt(window, point) == WindowEdges.None;

    /// <summary>Press a point. An edge or corner press resizes; a top-edge or title-bar press moves. Either captures the window.</summary>
    public bool Press(Point point)
    {
        var window = HitTest(point);
        if (window == null) return false;
        BringToFront(window.Id);
        // A locked UI keeps the press on the window, so it does not reach the game, but starts no drag.
        if (Locked) return true;
        _resizeEdges = EdgesAt(window, point);
        if (_resizeEdges != WindowEdges.None)
        {
            _dragging = window;
            _pressPoint = point;
            _pressBounds = window.Bounds;
        }
        else if (OnMoveEdge(window, point) || !window.Definition.DragsFromContent && point.Y - window.Location.Y < window.TitleBarHeight)
        {
            _dragging = window;
            _dragOffset = new Point(point.X - window.Location.X, point.Y - window.Location.Y);
        }
        return true;
    }

    /// <summary>Starts a move of a window from its content, at the pointer's point: its content calls this on a press that should drag it.</summary>
    public bool BeginMove(string id, Point point)
    {
        var window = Get(id);
        if (window == null || _dragging != null || Locked) return false;
        _resizeEdges = WindowEdges.None;
        _dragging = window;
        _dragOffset = new Point(point.X - window.Location.X, point.Y - window.Location.Y);
        return true;
    }

    public void Move(Point point)
    {
        if (_dragging == null) return;
        if (_resizeEdges != WindowEdges.None)
        {
            Resize(_dragging, point);
            return;
        }
        _dragging.Location = Clamp(Snap(_dragging, new Point(point.X - _dragOffset.X, point.Y - _dragOffset.Y)), _dragging.Size);
        _dragging.PreferredLocation = _dragging.Location;
    }

    public void Release()
    {
        if (_dragging != null) Save(_dragging);
        EndDrag();
    }

    public bool BringToFront(string id)
    {
        var window = Get(id);
        if (window == null) return false;
        _windows.Remove(window);
        _windows.Insert(0, window);
        return true;
    }

    public void ResizeScreen(Size screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0) throw new ArgumentOutOfRangeException(nameof(screen));
        Release();
        _screen = screen;
        foreach (var window in _windows)
        {
            window.Size = FitSize(window.Definition, window.PreferredSize, screen);
            window.Location = Clamp(window.PreferredLocation, window.Size, screen);
        }
    }

    /// <summary>
    /// The edges a point is on: within <see cref="ResizeBand"/> of a side or the bottom, or inside a corner square of
    /// <see cref="CornerBand"/> pixels, unless the window has no corners. The top edge between the corners moves the window instead.
    /// </summary>
    private static WindowEdges EdgesAt(ManagedWindow window, Point point)
    {
        var resizing = window.Definition.Resizing;
        if (resizing == null || !resizing.CanResize) return WindowEdges.None;
        var bounds = window.Bounds;
        var left = point.X - bounds.Left;
        var right = bounds.Right - 1 - point.X;
        var top = point.Y - bounds.Top;
        var bottom = bounds.Bottom - 1 - point.Y;
        // A window with a fixed height has no top or bottom edge, so its corners are its side bands. A window with no corners has its sides
        // and bottom as its edges; what lies between them is its content's to drag.
        if (!resizing.FixedHeight && !resizing.NoCorners && Math.Min(left, right) < CornerBand && Math.Min(top, bottom) < CornerBand)
            return (left < right ? WindowEdges.Left : WindowEdges.Right) | (top < bottom ? WindowEdges.Top : WindowEdges.Bottom);
        var edges = WindowEdges.None;
        if (left < ResizeBand) edges |= WindowEdges.Left;
        if (right < ResizeBand) edges |= WindowEdges.Right;
        if (!resizing.FixedHeight && bottom < ResizeBand) edges |= WindowEdges.Bottom;
        return edges;
    }

    /// <summary>The resize drag: the pressed edges move with the pointer and the others stay where they were.</summary>
    private void Resize(ManagedWindow window, Point point)
    {
        var resizing = window.Definition.Resizing!;
        var (x, width) = ResizeAxis(_pressBounds.X, _pressBounds.Width, point.X - _pressPoint.X,
            (_resizeEdges & WindowEdges.Left) != 0, (_resizeEdges & WindowEdges.Right) != 0,
            resizing.Minimum.Width, resizing.Maximum?.Width ?? int.MaxValue, _screen.Width);
        var (y, height) = ResizeAxis(_pressBounds.Y, _pressBounds.Height, point.Y - _pressPoint.Y,
            (_resizeEdges & WindowEdges.Top) != 0, (_resizeEdges & WindowEdges.Bottom) != 0,
            resizing.Minimum.Height, resizing.Maximum?.Height ?? int.MaxValue, _screen.Height);
        // A window that keeps its shape carries the other axis along; a pressed start edge keeps its opposite edge where it was.
        var size = resizing.Shaped(new Size(width, height), (_resizeEdges & (WindowEdges.Left | WindowEdges.Right)) != 0, _screen);
        if ((_resizeEdges & WindowEdges.Left) != 0) x = _pressBounds.Right - size.Width;
        if ((_resizeEdges & WindowEdges.Top) != 0) y = _pressBounds.Bottom - size.Height;
        window.Size = size;
        window.Location = Clamp(new Point(x, y), size);
        window.PreferredLocation = window.Location;
        window.PreferredSize = window.Size;
    }

    /// <summary>One axis of a resize. A moving start edge keeps the end edge fixed; a moving end edge stops at the screen.</summary>
    private static (int Start, int Length) ResizeAxis(int start, int length, int delta, bool startMoves, bool endMoves,
        int minimum, int maximum, int screen)
    {
        if (!startMoves && !endMoves) return (start, length);
        if (startMoves)
        {
            var end = start + length;
            var fitted = WindowResizing.Clamp(length - delta, minimum, Math.Min(end, maximum));
            return (end - fitted, fitted);
        }
        return (start, WindowResizing.Clamp(length + delta, minimum, Math.Min(screen - start, maximum)));
    }

    /// <summary>
    /// A moved window's place snapped, on each axis, to the nearest edge within <see cref="SnapDistance"/>: flush with the screen's edge,
    /// against a nearby window's edge, or lined up with it.
    /// </summary>
    private Point Snap(ManagedWindow window, Point location)
    {
        var size = window.Size;
        var near = Rectangle.Inflate(new Rectangle(location, size), SnapDistance, SnapDistance);
        var others = _windows.Where(other => other != window && other.Bounds.IntersectsWith(near)).Select(other => other.Bounds).ToList();
        return new Point(
            Nearest(location.X, others.SelectMany(other => new[] { other.Left, other.Right, other.Left - size.Width, other.Right - size.Width })
                .Append(0).Append(_screen.Width - size.Width)),
            Nearest(location.Y, others.SelectMany(other => new[] { other.Top, other.Bottom, other.Top - size.Height, other.Bottom - size.Height })
                .Append(0).Append(_screen.Height - size.Height)));
    }

    private static int Nearest(int value, IEnumerable<int> edges) =>
        edges.Where(edge => Math.Abs(edge - value) <= SnapDistance).OrderBy(edge => Math.Abs(edge - value)).DefaultIfEmpty(value).First();

    private void EndDrag()
    {
        _dragging = null;
        _resizeEdges = WindowEdges.None;
    }

    private static Size DefaultSize(WindowDefinition definition) => new(definition.Width, definition.Height);

    private static Size FitSize(WindowDefinition definition, Size wanted, Size screen) =>
        definition.Resizing is { } resizing ? resizing.Fit(wanted, screen) : DefaultSize(definition);

    /// <summary>Saves where a window was left, and moves the window it shares a location with there too, keeping that one's size.</summary>
    private void Save(ManagedWindow window)
    {
        _positions.Save(_server, _character, window.Id, Placement(window));
        if (window.Definition.SharesLocationWith is { } other)
            _positions.Save(_server, _character, other, (window.PreferredLocation, _positions.Load(_server, _character, other)?.Size));
    }

    private WindowPlacement Placement(ManagedWindow window) =>
        (window.PreferredLocation, window.Definition.Resizing == null ? null : Scaled(window.PreferredSize, 1 / Scale));

    /// <summary>The definition at this manager's scale: what its window measures on screen.</summary>
    private WindowDefinition Scaled(WindowDefinition definition) => Scale == 1 ? definition : new WindowDefinition(
        definition.Id, definition.Title, Scaled(definition.Width), Scaled(definition.Height), Scaled(definition.TitleBarHeight), definition.Theme,
        definition.Resizing is { } resizing ? new WindowResizing(Scaled(resizing.Minimum), resizing.Maximum is { } top ? Scaled(top) : null, resizing.Locked, resizing.NoCorners, resizing.Shape) : null,
        definition.SharesLocationWith, definition.DragsFromContent);

    private Size Scaled(Size size, double? scale = null) => new(Scaled(size.Width, scale), Scaled(size.Height, scale));
    private int Scaled(int length, double? scale = null) => Math.Max(1, (int)Math.Round(length * (scale ?? Scale)));

    private Point Clamp(Point location, Size size, Size? screen = null)
    {
        var bounds = screen ?? _screen;
        return new Point(
            Math.Max(0, Math.Min(bounds.Width - size.Width, location.X)),
            Math.Max(0, Math.Min(bounds.Height - size.Height, location.Y)));
    }
}
