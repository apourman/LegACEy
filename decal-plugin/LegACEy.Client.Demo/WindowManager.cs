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

/// <summary>
/// How a window resizes: a minimum size, and per axis a snap step measured beyond fixed chrome, so a size of
/// chrome + n × step lands on whole cells. A step of 1 and a chrome of 0 snap nothing.
/// </summary>
public sealed class WindowResizing
{
    public WindowResizing(Size minimum, Size step, Size chrome)
    {
        if (minimum.Width <= 0 || minimum.Height <= 0) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (step.Width <= 0 || step.Height <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        Minimum = minimum;
        Step = step;
        Chrome = chrome;
    }

    public Size Minimum { get; }
    public Size Step { get; }
    public Size Chrome { get; }

    /// <summary>The size nearest <paramref name="wanted"/> on the step, no smaller than the minimum and no bigger than <paramref name="max"/>.</summary>
    public Size Fit(Size wanted, Size max) => new(
        Snap(wanted.Width, Minimum.Width, Step.Width, Chrome.Width, max.Width),
        Snap(wanted.Height, Minimum.Height, Step.Height, Chrome.Height, max.Height));

    /// <summary>One axis. The minimum wins over the maximum when the screen is too small for it.</summary>
    internal static int Snap(int wanted, int minimum, int step, int chrome, int max)
    {
        var snapped = chrome + Math.Round((wanted - chrome) / (double)step, MidpointRounding.AwayFromZero) * step;
        var largest = chrome + Math.Floor((max - chrome) / (double)step) * step;
        var smallest = chrome + Math.Ceiling((minimum - chrome) / (double)step) * step;
        return (int)Math.Max(smallest, Math.Min(largest, snapped));
    }
}

/// <summary>Dimensions and identity for a LegACEy window.</summary>
public sealed class WindowDefinition
{
    /// <param name="theme">The window's own theme, such as Dereth. Null uses the client's theme, which a window with its own theme does not get.</param>
    /// <param name="resizing">How the window resizes from its edges and corners. Null means it does not resize.</param>
    public WindowDefinition(string id, string title, int width, int height, int titleBarHeight = 28, IClientTheme? theme = null, WindowResizing? resizing = null)
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
    }

    public string Id { get; }
    public string Title { get; }
    public int Width { get; }
    public int Height { get; }
    public int TitleBarHeight { get; }
    public IClientTheme? Theme { get; }
    public WindowResizing? Resizing { get; }
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

    public FileWindowPositionStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A position file path is required.", nameof(path));
        _path = path;
    }

    public WindowPlacement? Load(string server, string character, string windowId)
    {
        lock (_sync) return Rows().TryGetValue(Key(server, character, windowId), out var placement) ? placement : (WindowPlacement?)null;
    }

    public void Save(string server, string character, string windowId, WindowPlacement placement)
    {
        lock (_sync)
        {
            var rows = Rows();
            rows[Key(server, character, windowId)] = placement;
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_path, rows.Select(pair => Format(pair.Key, pair.Value)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, WindowPlacement> Rows()
    {
        var rows = new Dictionary<string, WindowPlacement>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return rows;
        try
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
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
/// A press in a resizable window's edge band, or in its corner square, resizes it; a press in its title bar moves it.
/// </summary>
public sealed class WindowManager
{
    /// <summary>How far in from a window's outside edge a press still counts as that edge.</summary>
    public const int ResizeBand = 6;

    /// <summary>
    /// The corner square's size: a press this near both edges of a corner is that corner. It matches the frame's corner
    /// piece, so the grip is as easy to find as the art shows it.
    /// </summary>
    public const int CornerBand = 16;

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

    public WindowManager(Size screen, IWindowPositionStore positions, string server, string character)
    {
        if (screen.Width <= 0 || screen.Height <= 0) throw new ArgumentOutOfRangeException(nameof(screen));
        _screen = screen;
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _server = server ?? string.Empty;
        _character = character ?? string.Empty;
    }

    public Size Screen => _screen;
    public IReadOnlyList<ManagedWindow> ZOrder => _windows;
    public bool IsDragging => _dragging != null;

    public ManagedWindow Open(WindowDefinition definition, Point requestedLocation)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        var existing = Get(definition.Id);
        if (existing != null) return existing;
        var saved = _positions.Load(_server, _character, definition.Id);
        var preferredLocation = saved?.Location ?? requestedLocation;
        var preferredSize = definition.Resizing != null && saved?.Size is { } savedSize ? savedSize : DefaultSize(definition);
        var size = FitSize(definition, preferredSize, _screen);
        var window = new ManagedWindow(definition, Clamp(preferredLocation, size), size, preferredLocation, preferredSize);
        _windows.Insert(0, window);
        return window;
    }

    public ManagedWindow? Get(string id) => _windows.FirstOrDefault(window => window.Id == id);

    public bool Close(string id)
    {
        var window = Get(id);
        if (window == null) return false;
        _positions.Save(_server, _character, window.Id, Placement(window));
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
        return window == null ? default : (window.Id, EdgesAt(window, point));
    }

    /// <summary>Press a point. An edge or corner press resizes; a title-bar press moves. Either captures the window.</summary>
    public bool Press(Point point)
    {
        var window = HitTest(point);
        if (window == null) return false;
        BringToFront(window.Id);
        _resizeEdges = EdgesAt(window, point);
        if (_resizeEdges != WindowEdges.None)
        {
            _dragging = window;
            _pressPoint = point;
            _pressBounds = window.Bounds;
        }
        else if (point.Y - window.Location.Y < window.TitleBarHeight)
        {
            _dragging = window;
            _dragOffset = new Point(point.X - window.Location.X, point.Y - window.Location.Y);
        }
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
        _dragging.Location = Clamp(new Point(point.X - _dragOffset.X, point.Y - _dragOffset.Y), _dragging.Size);
        _dragging.PreferredLocation = _dragging.Location;
    }

    public void Release()
    {
        if (_dragging != null)
            _positions.Save(_server, _character, _dragging.Id, Placement(_dragging));
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

    /// <summary>The edges a point is on: within <see cref="ResizeBand"/> of a side, or inside a corner square of <see cref="CornerBand"/>.</summary>
    private static WindowEdges EdgesAt(ManagedWindow window, Point point)
    {
        if (window.Definition.Resizing == null) return WindowEdges.None;
        var bounds = window.Bounds;
        var left = point.X - bounds.Left;
        var right = bounds.Right - 1 - point.X;
        var top = point.Y - bounds.Top;
        var bottom = bounds.Bottom - 1 - point.Y;
        if (Math.Min(left, right) < CornerBand && Math.Min(top, bottom) < CornerBand)
            return (left < right ? WindowEdges.Left : WindowEdges.Right) | (top < bottom ? WindowEdges.Top : WindowEdges.Bottom);
        var edges = WindowEdges.None;
        if (left < ResizeBand) edges |= WindowEdges.Left;
        if (right < ResizeBand) edges |= WindowEdges.Right;
        if (top < ResizeBand) edges |= WindowEdges.Top;
        if (bottom < ResizeBand) edges |= WindowEdges.Bottom;
        return edges;
    }

    /// <summary>The resize drag: the pressed edges move with the pointer and the others stay where they were.</summary>
    private void Resize(ManagedWindow window, Point point)
    {
        var resizing = window.Definition.Resizing!;
        var (x, width) = ResizeAxis(_pressBounds.X, _pressBounds.Width, point.X - _pressPoint.X,
            (_resizeEdges & WindowEdges.Left) != 0, (_resizeEdges & WindowEdges.Right) != 0,
            resizing.Minimum.Width, resizing.Step.Width, resizing.Chrome.Width, _screen.Width);
        var (y, height) = ResizeAxis(_pressBounds.Y, _pressBounds.Height, point.Y - _pressPoint.Y,
            (_resizeEdges & WindowEdges.Top) != 0, (_resizeEdges & WindowEdges.Bottom) != 0,
            resizing.Minimum.Height, resizing.Step.Height, resizing.Chrome.Height, _screen.Height);
        window.Location = new Point(x, y);
        window.Size = new Size(width, height);
        window.PreferredLocation = window.Location;
        window.PreferredSize = window.Size;
    }

    /// <summary>One axis of a resize. A moving start edge keeps the end edge fixed; a moving end edge stops at the screen.</summary>
    private static (int Start, int Length) ResizeAxis(int start, int length, int delta, bool startMoves, bool endMoves,
        int minimum, int step, int chrome, int screen)
    {
        if (!startMoves && !endMoves) return (start, length);
        if (startMoves)
        {
            var end = start + length;
            var fitted = WindowResizing.Snap(length - delta, minimum, step, chrome, end);
            return (end - fitted, fitted);
        }
        return (start, WindowResizing.Snap(length + delta, minimum, step, chrome, screen - start));
    }

    private void EndDrag()
    {
        _dragging = null;
        _resizeEdges = WindowEdges.None;
    }

    private static Size DefaultSize(WindowDefinition definition) => new(definition.Width, definition.Height);

    private static Size FitSize(WindowDefinition definition, Size wanted, Size screen) =>
        definition.Resizing is { } resizing ? resizing.Fit(wanted, screen) : DefaultSize(definition);

    private static WindowPlacement Placement(ManagedWindow window) =>
        (window.PreferredLocation, window.Definition.Resizing == null ? null : window.PreferredSize);

    private Point Clamp(Point location, Size size, Size? screen = null)
    {
        var bounds = screen ?? _screen;
        return new Point(
            Math.Max(0, Math.Min(bounds.Width - size.Width, location.X)),
            Math.Max(0, Math.Min(bounds.Height - size.Height, location.Y)));
    }
}
