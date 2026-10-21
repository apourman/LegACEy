using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// What a chat window reports to its host: its tab list changed, a tab was dragged out of its strip, the strip's empty part was pressed to
/// move the window, or a torn-off window's close medallion was pressed.
/// </summary>
public interface IRetailChatWindowHost
{
    void TabsChanged(RetailChatWindow window);
    void TabDraggedOut(RetailChatWindow window, int index);
    void MoveRequested(RetailChatWindow window);
    void CloseRequested(RetailChatWindow window);
    /// <summary>The player stepped the text size up (1) or down (-1).</summary>
    void TextSizeStepped(int steps);
}

/// <summary>
/// The Dereth chat windows a character has: the main window with All, and each tab torn off into a window of its own. Each window keeps its
/// own tab list, and each is a LegACEy window, placed and sized in the window position store. A tab dragged out of a window opens a window
/// that follows the pointer until it is released; released on another window's strip, the tab joins that window and the new window closes.
/// A torn-off window's close medallion merges its tabs back into the main window. The windows and their tabs are kept per character, so the
/// next open restores them.
/// </summary>
public sealed class RetailChatWindows : IRetailChatWindowHost, IDisposable
{
    /// <summary>The main window's id: its window position and its tab list are kept under it.</summary>
    public const string MainId = "dereth-chat";
    public const int DesignWidth = 460;
    public const int DesignHeight = 200;
    private const string TornIdPrefix = "dereth-chat-";
    // The text size of every chat window, in percent, kept per character in the position store.
    public const string TextSizeKey = "dereth-chat-text-size";
    public const int DefaultTextSize = 120;
    public const int SmallestTextSize = 80, LargestTextSize = 200, TextSizeStep = 10;
    private int _textSize = DefaultTextSize;
    // How long a chat window is left alone before it fades, in seconds, kept per character in the position store. NeverFades keeps it awake.
    public const string FadeKey = "dereth-chat-fade";
    public const int NeverFades = 0;
    public static readonly IReadOnlyList<int> FadeChoices = new[] { NeverFades, 10, RetailChatFade.DefaultIdleAfterSeconds, 30, 60 };
    private int _fadeSeconds = RetailChatFade.DefaultIdleAfterSeconds;
    // Where a torn-off window opens from the pointer: the tab it came from is under the pointer, so the window follows it.
    private static readonly Size Grab = new(24, 9);
    private readonly IRetailChatPort _port;
    private readonly RetailChatHistory _history;
    private readonly WindowManager _windows;
    private readonly IWindowPositionStore _positions;
    private readonly FileRetailChatTabStore _store;
    private readonly string _server;
    private readonly string _character;
    private readonly Func<DateTime> _clock;
    private readonly Func<Point> _pointer;
    private readonly Action<WindowDefinition, Control, Point> _showSurface;
    private readonly Action<string> _closeSurface;
    private readonly Action<string>? _copy;
    private readonly Func<string?>? _paste;
    private readonly List<(string Id, RetailChatWindow Window)> _chats = new();
    // Windows to close at the next refresh: a close medallion is pressed inside its own window, which cannot be torn down there.
    private readonly List<RetailChatWindow> _retiring = new();
    // The window whose tab is being dragged: it follows the pointer until the release.
    private RetailChatWindow? _dragged;
    private bool _disposed;

    /// <param name="port">Retail's chat, as the client reaches it.</param>
    /// <param name="windows">The client's window manager: it places and moves every window.</param>
    /// <param name="positions">The window position store, which a torn-off window's place is saved in.</param>
    /// <param name="store">The tab lists, per window, per character.</param>
    /// <param name="pointer">The pointer's place on the screen, in pixels.</param>
    /// <param name="showSurface">Shows a window's content as a LegACEy window with the definition, at the place given.</param>
    /// <param name="closeSurface">Closes a window shown with <paramref name="showSurface"/>.</param>
    /// <param name="clock">The time, for the idle fade; null for the system's.</param>
    /// <param name="copy">Puts text on the clipboard, for text copied from a log or a box; null copies nothing.</param>
    /// <param name="paste">Reads the clipboard's text, for Ctrl+V in a box; null pastes nothing.</param>
    /// <param name="carried">The history of the chat windows these replace, such as before a UI size change; null starts empty.</param>
    public RetailChatWindows(IRetailChatPort port, WindowManager windows, IWindowPositionStore positions, FileRetailChatTabStore store,
        string server, string character, Func<Point> pointer, Action<WindowDefinition, Control, Point> showSurface,
        Action<string> closeSurface, Func<DateTime>? clock = null, Action<string>? copy = null, Func<string?>? paste = null, RetailChatHistory? carried = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _server = server ?? string.Empty;
        _character = character ?? string.Empty;
        _pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        _showSurface = showSurface ?? throw new ArgumentNullException(nameof(showSurface));
        _closeSurface = closeSurface ?? throw new ArgumentNullException(nameof(closeSurface));
        _clock = clock ?? (() => DateTime.Now);
        _copy = copy;
        _paste = paste;
        _history = new RetailChatHistory(port, carried);
    }

    /// <summary>The lines and sent text every window shares.</summary>
    public RetailChatHistory History => _history;

    /// <summary>The text selected in a window's log, or empty when no window has any.</summary>
    public string LogSelection => _chats.Select(chat => chat.Window.LogSelection).FirstOrDefault(text => text.Length > 0) ?? string.Empty;

    /// <summary>Lets go of every log's selection but the one in <paramref name="windowId"/>'s window, as a click outside it does.</summary>
    public void ClearLogSelections(string? windowId = null)
    {
        foreach (var (id, window) in _chats)
            if (id != windowId) window.ClearLogSelection();
    }

    /// <summary>The main window, or null before <see cref="Open"/>.</summary>
    public RetailChatWindow? Main => Get(MainId);

    /// <summary>Every open window, the main window first.</summary>
    /// <summary>Whether a window id is a chat window's: the main one or one torn off it.</summary>
    public static bool IsChat(string id) => id == MainId || id.StartsWith(TornIdPrefix, StringComparison.Ordinal);

    public IReadOnlyList<RetailChatWindow> Windows => _chats.Select(chat => chat.Window).ToArray();

    /// <summary>
    /// Opens the main window at <paramref name="beside"/> with its saved tabs, then each torn-off window saved for the character at its
    /// saved place. A window with no saved place opens at <paramref name="beside"/>.
    /// </summary>
    public void Open(Point beside)
    {
        _textSize = LoadTextSize(_positions, _server, _character);
        _fadeSeconds = LoadFadeSeconds(_positions, _server, _character);
        ShowWindow(MainId, _store.Load(_server, _character, MainId) ?? RetailChatTab.Defaults, tornOff: false, beside);
        foreach (var id in _store.Windows(_server, _character))
        {
            if (id == MainId || _store.Load(_server, _character, id) is not { } tabs) continue;
            ShowWindow(id, tabs, tornOff: true, beside);
        }
    }

    /// <summary>
    /// Closes the windows that were pressed shut, and applies the idle fade to every window. A window stays awake while it is dragged, moved
    /// or resized. The host calls it once a frame.
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;
        foreach (var window in _retiring.ToArray()) Retire(window);
        var held = (_windows.Moving ?? _windows.Resizing)?.Id;
        foreach (var (id, window) in _chats.ToArray()) window.Refresh(window == _dragged || id == held);
    }

    /// <summary>
    /// Called after the window manager releases a window that was moved. If that window was a tab being dragged, the tab joins the window
    /// whose strip is under the pointer, and the dragged window closes; otherwise the window stays where it was dropped.
    /// </summary>
    public void Released()
    {
        if (_dragged is not { } dragged) return;
        _dragged = null;
        if (_disposed) return;
        var pointer = _pointer();
        // The topmost window under the pointer, other than the dragged one (which follows the pointer), decides. A window that covers a
        // chat strip, or any non-chat window, keeps the tab: the merge happens only where the strip is the thing under the pointer.
        foreach (var placed in _windows.ZOrder)
        {
            var target = Get(placed.Id);
            if (target == dragged || !placed.Bounds.Contains(pointer)) continue;
            if (target == null || !target.StripContains(ItemDropRouting.ContentPoint(placed, pointer))) return;
            target.AddTabs(dragged.Tabs);
            _retiring.Add(dragged);
            return;
        }
    }

    void IRetailChatWindowHost.TabsChanged(RetailChatWindow window)
    {
        if (_disposed || IdOf(window) is not { } id) return;
        _store.Save(_server, _character, id, window.Tabs);
    }

    void IRetailChatWindowHost.TabDraggedOut(RetailChatWindow window, int index)
    {
        if (_disposed || _dragged != null || IdOf(window) is not { } sourceId) return;
        var pointer = _pointer();
        // A torn-off window's only tab is the window itself, so the window moves rather than a new one opening.
        if (window.TornOff && window.Tabs.Count == 1)
        {
            if (_windows.BeginMove(sourceId, pointer)) _dragged = window;
            return;
        }
        var id = NewTornId();
        var tab = window.Tabs[index];
        var at = new Point(pointer.X - Grab.Width, pointer.Y - Grab.Height);
        // The new window is saved at its place before it opens, so it opens where it was dropped, not where a window of that id was last.
        _positions.Save(_server, _character, id, (at, null));
        _store.Save(_server, _character, id, new[] { tab });
        RetailChatWindow torn;
        try
        {
            torn = ShowWindow(id, new[] { tab }, tornOff: true, at);
        }
        catch
        {
            _store.Remove(_server, _character, id);
            throw;
        }
        if (!_windows.BeginMove(id, pointer))
        {
            Retire(torn);
            return;
        }
        window.TakeTab(index);
        _dragged = torn;
    }

    void IRetailChatWindowHost.MoveRequested(RetailChatWindow window)
    {
        if (_disposed || IdOf(window) is not { } id) return;
        _windows.BeginMove(id, _pointer());
    }

    void IRetailChatWindowHost.CloseRequested(RetailChatWindow window)
    {
        if (_disposed || !window.TornOff || Main is not { } main || _retiring.Contains(window)) return;
        main.AddTabs(window.Tabs);
        _retiring.Add(window);
    }

    /// <summary>
    /// Closes every window and releases it. A window that merged away is retired first, so its tab row goes and it cannot reappear on
    /// restore. The other windows keep their rows: their tabs and place stay saved, so the next open restores them. Each window is released
    /// on its own, so one that fails does not stop the rest; the first failure is rethrown after every window and the history are released.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? first = null;
        foreach (var window in _retiring.ToArray())
        {
            try { Retire(window); } catch (Exception error) { first ??= error; }
        }
        _retiring.Clear();
        _dragged = null;
        foreach (var (id, window) in _chats.ToArray())
        {
            try
            {
                _chats.RemoveAll(chat => chat.Window == window);
                _closeSurface(id);
                window.Dispose();
            }
            catch (Exception error) { first ??= error; }
        }
        try { _history.Dispose(); } catch (Exception error) { first ??= error; }
        if (first != null) throw new InvalidOperationException("One or more Dereth chat windows failed to close.", first);
    }

    private RetailChatWindow ShowWindow(string id, IReadOnlyList<RetailChatTab> tabs, bool tornOff, Point location)
    {
        var window = new RetailChatWindow(_port, _history, this, tabs, tornOff, clock: _clock, copy: _copy, paste: _paste) { TextScale = _textSize / 100.0, IdleAfterSeconds = FadeDelay };
        _chats.Add((id, window));
        try
        {
            _showSurface(Definition(id), window, location);
        }
        catch
        {
            _chats.RemoveAll(chat => chat.Window == window);
            window.Dispose();
            throw;
        }
        return window;
    }

    /// <summary>Closes a window and forgets it. A torn-off window's tab list is removed, since its tabs have merged or gone.</summary>
    private void Retire(RetailChatWindow window)
    {
        if (IdOf(window) is not { } id) return;
        _chats.RemoveAll(chat => chat.Window == window);
        if (window.TornOff) _store.Remove(_server, _character, id);
        if (_dragged == window) _dragged = null;
        _closeSurface(id);
        window.Dispose();
    }

    /// <summary>The text size of every chat window, in percent.</summary>
    public int TextSize => _textSize;

    /// <summary>Told when the text size moves, from the chat's own menu or the Settings window.</summary>
    public event Action<int>? TextSizeChanged;

    public void TextSizeStepped(int steps) => SetTextSize(_textSize + steps * TextSizeStep);

    /// <summary>Sets the text size of every chat window, in percent, within the bounds, and keeps it for the character.</summary>
    public void SetTextSize(int percent)
    {
        if (_disposed) return;
        var size = ClampTextSize(percent);
        if (size == _textSize) return;
        _textSize = size;
        SaveTextSize(_positions, _server, _character, size);
        foreach (var (_, window) in _chats) window.TextScale = size / 100.0;
        TextSizeChanged?.Invoke(size);
    }

    /// <summary>A text size kept within the chat's bounds, in percent.</summary>
    public static int ClampTextSize(int percent) => Math.Max(SmallestTextSize, Math.Min(LargestTextSize, percent));

    /// <summary>The character's text size in percent, the default before one is saved, so a window opens at the size it was left.</summary>
    public static int LoadTextSize(IWindowPositionStore positions, string server, string character) =>
        positions.Load(server, character, TextSizeKey) is { Location.X: var saved and > 0 } ? ClampTextSize(saved) : DefaultTextSize;

    public static void SaveTextSize(IWindowPositionStore positions, string server, string character, int percent) =>
        positions.Save(server, character, TextSizeKey, (new Point(ClampTextSize(percent), 0), null));

    /// <summary>The idle delay every chat window uses, or null when it never fades.</summary>
    private int? FadeDelay => _fadeSeconds == NeverFades ? null : _fadeSeconds;

    /// <summary>Sets how long every chat window is left alone before it fades, in seconds (<see cref="NeverFades"/> keeps them awake), and keeps it for the character.</summary>
    public void SetFadeSeconds(int seconds)
    {
        if (_disposed) return;
        var fade = FadeOrDefault(seconds);
        if (fade == _fadeSeconds) return;
        _fadeSeconds = fade;
        SaveFadeSeconds(_positions, _server, _character, fade);
        foreach (var (_, window) in _chats) window.IdleAfterSeconds = FadeDelay;
    }

    /// <summary>A fade delay that is one of <see cref="FadeChoices"/>; any other value snaps to the default.</summary>
    public static int FadeOrDefault(int seconds) => FadeChoices.Contains(seconds) ? seconds : RetailChatFade.DefaultIdleAfterSeconds;

    /// <summary>The character's fade delay in seconds, the default before one is saved.</summary>
    public static int LoadFadeSeconds(IWindowPositionStore positions, string server, string character) =>
        positions.Load(server, character, FadeKey) is { Location.X: var saved } ? FadeOrDefault(saved) : RetailChatFade.DefaultIdleAfterSeconds;

    public static void SaveFadeSeconds(IWindowPositionStore positions, string server, string character, int seconds) =>
        positions.Save(server, character, FadeKey, (new Point(seconds, 0), null));

    private RetailChatWindow? Get(string id) => _chats.FirstOrDefault(chat => chat.Id == id).Window;

    private string? IdOf(RetailChatWindow window) => _chats.FirstOrDefault(chat => chat.Window == window).Id;

    /// <summary>The lowest torn-off id this character has neither open nor saved, so a new window never reuses another's tabs.</summary>
    private string NewTornId()
    {
        var saved = _store.Windows(_server, _character);
        for (var number = 1; ; number++)
        {
            var id = TornIdPrefix + number.ToString(CultureInfo.InvariantCulture);
            if (_chats.All(chat => chat.Id != id) && !saved.Contains(id)) return id;
        }
    }


    private static WindowDefinition Definition(string id) => new(id, "Chat", DesignWidth, DesignHeight, titleBarHeight: 19,
        theme: new DerethClientTheme(), resizing: new WindowResizing(new Size(260, 120)), dragsFromContent: true);
}
