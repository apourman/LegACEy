using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace LegACEy.Client.Demo;

/// <summary>Dimensions and identity for a LegACEy window.</summary>
public sealed class WindowDefinition
{
    public WindowDefinition(string id, string title, int width, int height, int titleBarHeight = 28)
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
    }

    public string Id { get; }
    public string Title { get; }
    public int Width { get; }
    public int Height { get; }
    public int TitleBarHeight { get; }
}

/// <summary>The live state of one LegACEy window.</summary>
public sealed class ManagedWindow
{
    internal ManagedWindow(WindowDefinition definition, Point location)
    {
        Definition = definition;
        Location = location;
        PreferredLocation = location;
    }

    public WindowDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public int Width => Definition.Width;
    public int Height => Definition.Height;
    public int TitleBarHeight => Definition.TitleBarHeight;
    public Point Location { get; internal set; }
    internal Point PreferredLocation { get; set; }
    public Rectangle Bounds => new(Location, new Size(Width, Height));
}

/// <summary>Persistence seam for positions keyed by server, character and window id.</summary>
public interface IWindowPositionStore
{
    Point? Load(string server, string character, string windowId);
    void Save(string server, string character, string windowId, Point location);
}

/// <summary>In-memory position store for tests and the preview app.</summary>
public sealed class MemoryWindowPositionStore : IWindowPositionStore
{
    private readonly Dictionary<string, Point> _positions = new(StringComparer.Ordinal);

    public Point? Load(string server, string character, string windowId) =>
        _positions.TryGetValue(Key(server, character, windowId), out var location) ? location : (Point?)null;

    public void Save(string server, string character, string windowId, Point location) =>
        _positions[Key(server, character, windowId)] = location;

    public Point? Get(string server, string character, string windowId) => Load(server, character, windowId);

    private static string Key(string server, string character, string windowId) =>
        server + "\u001f" + character + "\u001f" + windowId;
}

/// <summary>
/// Small, recoverable position file beside the plugin. Keys are base64 encoded so server and
/// character names can contain any punctuation. A malformed line is ignored and never prevents
/// the client from loading a window.
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

    public Point? Load(string server, string character, string windowId)
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                foreach (var line in File.ReadAllLines(_path))
                {
                    var fields = line.Split('|');
                    if (fields.Length != 5 || !int.TryParse(fields[3], out var x) || !int.TryParse(fields[4], out var y))
                        continue;
                    if (fields[0] == Encode(server) && fields[1] == Encode(character) && fields[2] == Encode(windowId))
                        return new Point(x, y);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    public void Save(string server, string character, string windowId, Point location)
    {
        lock (_sync)
        {
            var rows = new Dictionary<string, Point>(StringComparer.Ordinal);
            if (File.Exists(_path))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(_path))
                    {
                        var fields = line.Split('|');
                        if (fields.Length == 5 && int.TryParse(fields[3], out var x) && int.TryParse(fields[4], out var y))
                            rows[fields[0] + "|" + fields[1] + "|" + fields[2]] = new Point(x, y);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            rows[Encode(server) + "|" + Encode(character) + "|" + Encode(windowId)] = location;
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_path, rows.Select(pair => pair.Key + "|" + pair.Value.X + "|" + pair.Value.Y));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
}

/// <summary>
/// Pure window behavior used by the plugin adapter and the tests. The first item in ZOrder is
/// frontmost. Pointer coordinates are screen coordinates and all returned positions are clamped.
/// </summary>
public sealed class WindowManager
{
    private readonly List<ManagedWindow> _windows = new();
    private readonly IWindowPositionStore _positions;
    private readonly string _server;
    private readonly string _character;
    private Size _screen;
    private ManagedWindow? _dragging;
    private Point _dragOffset;

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
        var location = Clamp(saved ?? requestedLocation, definition.Width, definition.Height);
        var window = new ManagedWindow(definition, location) { PreferredLocation = saved ?? requestedLocation };
        _windows.Insert(0, window);
        return window;
    }

    public ManagedWindow? Get(string id) => _windows.FirstOrDefault(window => window.Id == id);

    public bool Close(string id)
    {
        var window = Get(id);
        if (window == null) return false;
        _positions.Save(_server, _character, window.Id, window.PreferredLocation);
        if (_dragging == window) _dragging = null;
        return _windows.Remove(window);
    }

    public ManagedWindow? HitTest(Point point) =>
        _windows.FirstOrDefault(window => window.Bounds.Contains(point));

    /// <summary>Press a point. A title-bar press captures the window for a drag.</summary>
    public bool Press(Point point)
    {
        var window = HitTest(point);
        if (window == null) return false;
        BringToFront(window.Id);
        if (point.Y - window.Location.Y < window.TitleBarHeight)
        {
            _dragging = window;
            _dragOffset = new Point(point.X - window.Location.X, point.Y - window.Location.Y);
        }
        return true;
    }

    public void Move(Point point)
    {
        if (_dragging == null) return;
        _dragging.Location = Clamp(new Point(point.X - _dragOffset.X, point.Y - _dragOffset.Y), _dragging.Width, _dragging.Height);
        _dragging.PreferredLocation = _dragging.Location;
    }

    public void Release()
    {
        if (_dragging != null)
            _positions.Save(_server, _character, _dragging.Id, _dragging.PreferredLocation);
        _dragging = null;
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
            window.Location = Clamp(window.PreferredLocation, window.Width, window.Height, screen);
    }

    private Point Clamp(Point location, int width, int height, Size? screen = null)
    {
        var bounds = screen ?? _screen;
        return new Point(
            Math.Max(0, Math.Min(bounds.Width - width, location.X)),
            Math.Max(0, Math.Min(bounds.Height - height, location.Y)));
    }
}
