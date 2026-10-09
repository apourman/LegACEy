using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Media;
using IOPath = System.IO.Path;
using Decal.Adapter;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.InputRouter;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Microsoft.DirectX.Direct3D;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Decal-facing runtime that wires game callbacks to the netstandard client UI framework.</summary>
internal sealed class ClientUiRuntime : IClientUiHost, ILegACEyPluginHost
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(IntPtr cursor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    // WM_SETCURSOR, the hit-test code for the client area, and the standard size cursors' resource ids (MAKEINTRESOURCE).
    private const int WmSetCursor = 0x0020;
    private const int HtClient = 1;
    private const int IdcSizeWE = 32644;
    private const int IdcSizeNS = 32645;
    private const int IdcSizeNWSE = 32642;
    private const int IdcSizeNESW = 32643;
    private const int IdcSizeAll = 32646;
    // The retail cursors in the portal DAT, all 32×32 with the hotspot in the middle.
    private const uint DatCursorNS = 0x06005E66;
    private const uint DatCursorWE = 0x06006128;
    private const uint DatCursorNWSE = 0x06006126;
    private const uint DatCursorNESW = 0x06006127;
    private const uint DatCursorMove = 0x06006119;

    private const string MenuSlot = "LegACEy";
    private const string MenuWindowId = "plugin-menu";
    private const uint MenuIcon = 0x06004D20;
    private const string TestFailureFile = "fail-post-ui-draw";
    private static readonly string PluginDirectory = IOPath.GetDirectoryName(typeof(ClientUiRuntime).Assembly.Location)!;
    // Every client appends to the same log beside the DLL; the process id tells their lines apart.
    private static readonly int ProcessId = Process.GetCurrentProcess().Id;

    private Device? _device;
    private static readonly System.Drawing.Color ResizeOutline = System.Drawing.Color.FromArgb(0xC9, 0xA4, 0x5C);
    private PortalDat? _portal;
    private IndicatorBar? _bar;
    private readonly Dictionary<ModelView, ModelRenderer> _modelRenderers = new();
    private readonly HashSet<ModelView> _failedModelViews = new();
    private readonly HashSet<ModelView> _drawnModelViews = new();
    private ScreenSurface? _barSurface;
    private RetailSurfaceRenderer? _barRenderer;
    private WindowManager? _windows;
    // The window whose frame shows a hovered corner, and the corner it shows.
    private string? _cornerWindowId;
    private DerethCorner _cornerApplied;
    private readonly Dictionary<uint, IntPtr> _datCursors = new();
    private readonly Dictionary<int, IntPtr> _systemCursors = new();
    private PostUiDrawHook? _postUiDrawHook;
    private bool _windowsEnabled;
    private bool _firstPostUiWindow = true;
    private DateTime _lastMeasurementLog = DateTime.UtcNow;
    private DateTime _lastTestFailureCheck;
    private TimeSpan _maxUiFrame;
    private TimeSpan _maxPostUiDraw;
    private ScreenSurface? _hovered;
    private readonly InputRouterService _inputRouter = new();
    private Point _pointer;
    private Size? _dragOffset;
    private RetailTakeoverLifecycle? _barTakeover;
    private readonly GameStatePort _gameState = new(new GameStateSnapshot("unknown-character", "unknown-server", 0, 0, 0, 0, 0, 0));
    private readonly GameStatePoller _gameStatePoller;
    private ClientUiFramework? _clientUi;
    private GameChannelTransport? _channelTransport;
    private ServerChannelClient? _serverChannel;
    private RetailItemDrag? _retailDrag;
    private readonly DecalInventoryPort _inventory = new();
    private uint _retailDragItem;
    private string _retailDragName = string.Empty;
    private int _loggedDrops;
    private ScreenSurface? _dragIconSurface;
    // our copy of the retail drag icon, drawn while the item is over a LegACEy window (the client draws its own below them)
    private IDisposable? _retailDragIcon;
    // a LegACEy item being dragged out of a window: the inventory cell under it shows the client's drop indicator
    private bool _itemDragActive;
    private readonly Dictionary<string, ScreenSurface> _featureSurfaces = new(StringComparer.Ordinal);
    // Plugin windows the player closed: kept, texture and all, so reopening skips building the content again.
    // ponytail: kept until logoff, still subscribed and running (the paperdoll rebuilds on equip changes); add a cap,
    // or tell plugins when they are shown and hidden, once more plugins arrive
    private readonly Dictionary<string, ScreenSurface> _hiddenSurfaces = new(StringComparer.Ordinal);
    // Error handlers of windows opened by plugins; a failure in one turns off its plugin, not the client.
    private readonly Dictionary<string, Action<Exception>> _windowFailures = new(StringComparer.Ordinal);
    private PluginRegistry? _plugins;
    private bool _inGame;
    private bool _failed;

    public ClientUiRuntime() => _gameStatePoller = new GameStatePoller(_gameState, ReadGameState,
        error => Log($"Character stats temporarily unavailable; keeping UI active and retrying: {error.Message}"));

    public void Startup()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginDirectory;
        NativeUi.Initialize(Log);
        try
        {
            _channelTransport = new GameChannelTransport(NativeUi.ReadMemory, () => _inGame, Log);
            _serverChannel = new ServerChannelClient(_channelTransport);
        }
        catch (Exception exception) { Log($"LegACEy server channel disabled: {exception.Message}"); }
        try { _retailDrag = new RetailItemDrag(NativeUi.ReadMemory, Log); }
        catch (Exception exception) { Log($"Item drag and drop disabled: {exception.Message}"); }
        LoadPlugins();
        CoreManager.Current.FilterInitComplete += OnFilterInitComplete;
    }

    /// <summary>Starts the plugins in Plugins/ beside the client. Each plugin's failures stay with that plugin.</summary>
    private void LoadPlugins()
    {
        _plugins = new PluginRegistry(this, Log);
        try
        {
            foreach (var plugin in PluginLoader.Load(IOPath.Combine(PluginDirectory, "Plugins"), Log))
                _plugins.Add(plugin);
        }
        catch (Exception exception) { Log($"LegACEy plugins unavailable: {exception}"); }
    }

    public void Shutdown()
    {
        CoreManager.Current.FilterInitComplete -= OnFilterInitComplete;
        CoreManager.Current.CharacterFilter.LoginComplete -= OnLoginComplete;
        CoreManager.Current.CharacterFilter.Logoff -= OnLogoff;
        CoreManager.Current.RenderFrame -= OnRenderFrame;
        CoreManager.Current.WindowMessage -= OnWindowMessage;
        CoreManager.Current.EchoFilter.ServerDispatch -= OnServerDispatch;
        _inventory.Detach();
        _postUiDrawHook?.Dispose();
        _postUiDrawHook = null;
        RestoreNativeBar();
        TearDown();
        _serverChannel?.Dispose();
        _serverChannel = null;
        AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromPluginDirectory;
    }

    /// <summary>
    /// The client process has no binding redirects, and Avalonia references older builds of
    /// System.Buffers, System.Numerics.Vectors and others than the ones shipped beside the plugin.
    /// Serve the shipped copy when it is the same or newer, and log every redirect.
    /// </summary>
    private static Assembly? ResolveFromPluginDirectory(object? sender, ResolveEventArgs args)
    {
        var requested = new AssemblyName(args.Name);
        var path = IOPath.Combine(PluginDirectory, requested.Name + ".dll");
        if (!File.Exists(path))
            return null;

        var shipped = AssemblyName.GetAssemblyName(path);
        if (requested.Version != null && shipped.Version < requested.Version)
            return null;

        Log($"Assembly redirect: {args.Name} -> {shipped.Version}, requested by {args.RequestingAssembly?.GetName().Name ?? "unknown"}");
        return Assembly.LoadFrom(path);
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(IOPath.Combine(PluginDirectory, "legacey-avalonia.log"), $"{DateTime.Now:O} [pid {ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break assembly resolution or the client callback that triggered it.
        }
    }

    private void OnFilterInitComplete(object? sender, EventArgs e)
    {
        CoreManager.Current.FilterInitComplete -= OnFilterInitComplete;
        CoreManager.Current.CharacterFilter.LoginComplete += OnLoginComplete;
        CoreManager.Current.CharacterFilter.Logoff += OnLogoff;
        CoreManager.Current.RenderFrame += OnRenderFrame;
        CoreManager.Current.WindowMessage += OnWindowMessage;
        CoreManager.Current.EchoFilter.ServerDispatch += OnServerDispatch;
        _inventory.Attach();
    }

    /// <summary>LegACEy channel replies and pushes arrive on the game thread with every other server message.</summary>
    private void OnServerDispatch(object? sender, NetworkMessageEventArgs e)
    {
        if (_failed || _serverChannel == null || _channelTransport == null)
            return;
        byte[]? payload;
        try { payload = _channelTransport.ReadEvent(e); }
        catch (Exception exception)
        {
            Log($"Could not read a server channel event: {exception}");
            return;
        }
        if (payload != null)
            Guard(() => _serverChannel.Receive(payload));
    }

    /// <summary>Enable character-specific windows once Decal has finished loading character identity.</summary>
    private void OnLoginComplete(object? sender, EventArgs e)
    {
        if (_failed || !NativeUi.Ready)
            return;

        Guard(() =>
        {
            if (_barSurface == null)
                CreateUi();
            if (_windows == null)
                CreateWindowManager();
            _clientUi ??= new ClientUiFramework(this, _gameState, CurrentTheme(), _serverChannel);
            _inGame = true;
            _inventory.MarkStale();
            PublishGameState();
            RequestServerActions();
        });
    }

    /// <summary>
    /// Asks the server which actions it has. Until the answer arrives, and when there is none, every plugin that needs
    /// a server action stays hidden.
    /// </summary>
    private void RequestServerActions()
    {
        _plugins?.SetServerActions(null);
        _serverChannel?.Request(ChannelHello.Action, Array.Empty<byte>(), reply => _plugins?.SetServerActions(ServerActionsFrom(reply)));
    }

    private static IEnumerable<string> ServerActionsFrom(ChannelReply reply)
    {
        if (!reply.Ok) return Array.Empty<string>();
        try { return ChannelHello.Read(reply.Body).Actions; }
        catch (Exception exception)
        {
            Log($"Could not read the server's action list; plugins that need the server stay hidden: {exception.Message}");
            return Array.Empty<string>();
        }
    }

    private void OnLogoff(object? sender, EventArgs e)
    {
        Guard(() => ApplyReset(_inputRouter.Route(new NativeInputMessage(InputRouterService.WmLogoff, IntPtr.Zero, IntPtr.Zero), GetInputSurfaces())));
        Guard(() => _hovered?.Panel.PointerLeave());
        _inGame = false;
        _inventory.Clear();
        try { _clientUi?.EndSession(); }
        catch (Exception exception) { Log($"Could not clean up client UI at logoff: {exception}"); }
        _clientUi = null;
        try { _plugins?.EndSession(); }
        catch (Exception exception) { Log($"Could not close plugin windows at logoff: {exception}"); }
        // Windows released their requests above; anything still outstanding fails as disconnected.
        Guard(() => _serverChannel?.Reset());
        // Keep replacing the retail indicators bar through logout. RenderFrame hides any native
        // re-show until the element disappears; only unload or failure gives it back.
        foreach (var window in _windows?.ZOrder.ToArray() ?? Array.Empty<ManagedWindow>())
            _windows!.Close(window.Id);
        _windows = null;
        _hovered = null;
        _dragOffset = null;
        _bar?.SetOpen(MenuSlot, false);
    }

    private void CreateUi()
    {
        _device = GameDevice.Open();
        var acclient = Process.GetCurrentProcess().MainModule!.FileName;
        _portal = new PortalDat(IOPath.Combine(IOPath.GetDirectoryName(acclient)!, "client_portal.dat"));
        GameArtImageExtension.CurrentSource = _portal;

        var slots = new[]
        {
            new IndicatorSlot("Move", 0x060074C9, BeginBarDrag, actOnPress: true),
            new IndicatorSlot("Link status", 0x06007498, () => NativeUi.ToggleRootElement(NativeUi.LinkStatus)),
            new IndicatorSlot("Positive effects", 0x0600749C, () => NativeUi.ToggleRootElement(NativeUi.PositiveEffects)),
            new IndicatorSlot("Negative effects", 0x0600749F, () => NativeUi.ToggleRootElement(NativeUi.NegativeEffects)),
            new IndicatorSlot("Vitae", 0x060074A1, () => NativeUi.ToggleRootElement(NativeUi.Vitae)),
            new IndicatorSlot("Character info", 0x060074A2, () => NativeUi.ToggleRootElement(NativeUi.CharacterInfo)),
            new IndicatorSlot("Mini-game", 0x060074A6, () => NativeUi.ToggleRootElement(NativeUi.MiniGame)),
            new IndicatorSlot(MenuSlot, MenuIcon, ToggleMenuWindow, "L"),
            new IndicatorSlot("Log out", 0x060074B1, NativeUi.RequestLogOut)
        };
        var size = IndicatorBar.MeasureFor(slots.Length);
        var barPanel = ObservePanel(AvaloniaPanel.Create(() => _bar = new IndicatorBar(slots, _portal.ReadImage), size.Width, size.Height));
        _barSurface = new ScreenSurface(_device, barPanel);
        _barRenderer = new RetailSurfaceRenderer(_barSurface.Prepare, _barSurface.DrawNow);
        _barTakeover = new RetailTakeoverLifecycle(new NativeBarPort(NativeUi.Indicators), new SurfaceTakeoverPort(_barSurface));

        _barSurface.Panel.ApplyTheme(CurrentTheme());
        EnsurePostUiDrawHook();
        Log("Indicator bar replacement ready.");
        WarmUpDerethTheme();
    }

    /// <summary>Lays out a throwaway Dereth window at login, so the first real one opens without a long frame.</summary>
    private static void WarmUpDerethTheme()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var panel = AvaloniaPanel.Create(DerethWarmUp.Sample, DerethWarmUp.Width, DerethWarmUp.Height);
            panel.ApplyTheme(new DerethClientTheme());
            panel.Tick();
        }
        catch (Exception exception)
        {
            Log($"Dereth warm-up failed: {exception.Message}");
            return;
        }
        Log($"Dereth warm-up took {stopwatch.ElapsedMilliseconds} ms.");
    }

    private void CreateWindowManager()
    {
        if (_device == null) return;
        _windows = new WindowManager(new Size(_device.Viewport.Width, _device.Viewport.Height), new FileWindowPositionStore(IOPath.Combine(PluginDirectory, "window-positions.txt")), SessionServer(), SessionCharacter());
        _windowsEnabled = EnsurePostUiDrawHook();
    }

    private bool EnsurePostUiDrawHook()
    {
        // Entry rendering needs this before LoginComplete creates character windows.
        _postUiDrawHook ??= new PostUiDrawHook(DrawAfterRetailUi, Disable, Log);
        try
        {
            if (_postUiDrawHook.Install()) return true;
        }
        catch (Exception exception)
        {
            _postUiDrawHook.Dispose();
            _postUiDrawHook = null;
            DisableWindows(exception);
            return false;
        }
        Log("Post-UI drawing unavailable: checked retail EndScene hook could not be installed. Retail replacements use pre-UI drawing.");
        return false;
    }

    private IClientTheme CurrentTheme() => _portal != null ? new AcClientTheme(_portal) : new SimpleClientTheme();

    /// <summary>Opens or closes the LegACEy menu window.</summary>
    private void ToggleMenuWindow()
    {
        if (!_windowsEnabled || _windows == null || _clientUi == null)
            return;
        if (_postUiDrawHook?.HasRun != true)
        {
            Log("LegACEy windows unavailable: the installed retail EndScene hook has not run. Keeping the indicator bar interactive.");
            return;
        }
        if (_windows.Get(MenuWindowId) != null)
        {
            _clientUi.CloseWindow(MenuWindowId);
            _bar?.SetOpen(MenuSlot, false);
            return;
        }
        var chrome = new ThemeWindowChrome(_portal!, MenuSlot, new PluginMenuPanel(_plugins!, _portal));
        chrome.CloseRequested += (_, _) =>
        {
            _clientUi?.CloseWindow(MenuWindowId);
            _bar?.SetOpen(MenuSlot, false);
        };
        _clientUi.OpenWindow(new WindowDefinition(MenuWindowId, MenuWindowId, PluginMenuPanel.WindowWidth, PluginMenuPanel.WindowHeight), chrome, new Point(260, 120));
        _bar?.SetOpen(MenuSlot, true);
    }

    /// <summary>The handle was pressed: the bar follows the pointer until the button goes up.</summary>
    private void BeginBarDrag()
    {
        if (_barTakeover?.CanDrag != true)
            return;
        var location = _barSurface!.Location;
        _dragOffset = new Size(_pointer.X - location.X, _pointer.Y - location.Y);
    }

    private void DragBar(Point pointer)
    {
        if (_barTakeover?.CanDrag != true)
        {
            _dragOffset = null;
            return;
        }
        var screen = _device!.Viewport;
        var size = _barSurface!.Bounds.Size;
        _barSurface.Location = new Point(
            Math.Max(0, Math.Min(screen.Width - size.Width, pointer.X - _dragOffset!.Value.Width)),
            Math.Max(0, Math.Min(screen.Height - size.Height, pointer.Y - _dragOffset.Value.Height)));
    }

    /// <summary>
    /// Move the hidden retail bar to where ours was dropped, so the client keeps the position
    /// (and saves it) as if the player had dragged the retail bar.
    /// </summary>
    private void EndBarDrag()
    {
        _dragOffset = null;
        if (_barTakeover?.MoveTo(_barSurface!.Location) == true)
            Log($"Moved the retail indicators bar to {_barSurface.Location}.");
    }

    /// <summary>
    /// Replace the native bar on its first visible frame, including world entry and logout.
    /// </summary>
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        var timer = Stopwatch.StartNew();
        try { OnRenderFrameCore(sender, e); }
        finally
        {
            if (timer.Elapsed > _maxUiFrame) _maxUiFrame = timer.Elapsed;
            RecordPerformanceMeasurement();
        }
    }

    private void OnRenderFrameCore(object? sender, EventArgs e)
    {
        if (_barTakeover != null && _failed)
            RestoreNativeBar();
        if (_failed || !NativeUi.Ready)
            return;

        Guard(() =>
        {
            PublishGameState();
            _inventory.Flush(_inGame);
            _serverChannel?.Tick();
            if (_barSurface == null)
            {
                var element = NativeUi.GetElement(NativeUi.Indicators);
                if (element == IntPtr.Zero || !NativeUi.IsVisible(element))
                    return;
                CreateUi();
            }
            TakeOverNativeBar();
            _barRenderer!.RenderFrame(_inGame, _postUiDrawHook?.IsInstalled == true);
        });

        if (_failed || !_inGame || !_windowsEnabled)
            return;

        Guard(() =>
        {
            if (_windows != null)
            {
                var viewport = new Size(_device!.Viewport.Width, _device.Viewport.Height);
                if (_windows.Screen != viewport)
                {
                    _windows.ResizeScreen(viewport);
                    SyncWindowLocations();
                }
            }

            UpdateRetailDrag();
            if (_itemDragActive)
                _retailDrag?.UpdateDropIndicator();
            PrepareFeatureWindows();
            if (_dragIconSurface != null)
            {
                _dragIconSurface.Location = new Point(_pointer.X - 16, _pointer.Y - 16);
                _dragIconSurface.Prepare();
            }
        });
    }

    /// <summary>Logs the longest frame and post-UI draw since the last sample, then starts counting again.</summary>
    private void RecordPerformanceMeasurement()
    {
        var now = DateTime.UtcNow;
        if (now - _lastMeasurementLog < TimeSpan.FromSeconds(30)) return;
        _lastMeasurementLog = now;
        var uiFrame = _maxUiFrame;
        var postUiDraw = _maxPostUiDraw;
        _maxUiFrame = TimeSpan.Zero;
        _maxPostUiDraw = TimeSpan.Zero;
        try
        {
            var surfaces = new List<ScreenSurface>();
            if (_barSurface != null) surfaces.Add(_barSurface);
            surfaces.AddRange(_featureSurfaces.Values);
            using var process = Process.GetCurrentProcess();
            var maxTick = surfaces.Count == 0 ? 0 : surfaces.Max(surface => surface.LastTickMilliseconds);
            var maxUpload = surfaces.Count == 0 ? 0 : surfaces.Max(surface => surface.LastUploadMilliseconds);
            var dirtyRectangles = surfaces.Sum(surface => surface.LastDirtyRectangleCount);
            Log($"UI performance sample: maxUiFrame={uiFrame.TotalMilliseconds:F2}ms maxPostUiDraw={postUiDraw.TotalMilliseconds:F2}ms surfaces={surfaces.Count} maxPanelTick={maxTick:F2}ms maxTextureUpload={maxUpload:F2}ms dirtyRects={dirtyRectangles} privateBytes={process.PrivateMemorySize64} virtualBytes={process.VirtualMemorySize64}.");
        }
        catch (Exception exception)
        {
            Log($"Could not collect a UI performance sample: {exception.Message}");
        }
    }

    /// <summary>
    /// Ticks each LegACEy window. A failure, including a panel over its tick budget, goes to the window's error handler:
    /// a plugin's window turns off that plugin, a client window disables the client UI.
    /// </summary>
    private void PrepareFeatureWindows()
    {
        foreach (var pair in _featureSurfaces.ToArray())
        {
            // An earlier failure in this loop may have closed the window.
            if (!_featureSurfaces.ContainsKey(pair.Key)) continue;
            try { pair.Value.Prepare(); }
            catch (Exception exception) { _windowFailures[pair.Key](exception); }
        }
    }

    /// <summary>Called by IDirect3DDevice9.EndScene after retail and Decal UI drawing.</summary>
    private void DrawAfterRetailUi()
    {
        var timer = Stopwatch.StartNew();
        try { DrawAfterRetailUiCore(); }
        finally { if (timer.Elapsed > _maxPostUiDraw) _maxPostUiDraw = timer.Elapsed; }
    }

    private void DrawAfterRetailUiCore()
    {
        if (_failed) return;
        ThrowIfTestFailureRequested();
        Guard(() => _barRenderer?.DrawAfterRetailUi());
        if (_failed || !_windowsEnabled || _windows == null)
            return;
        _drawnModelViews.Clear();
        foreach (var window in _windows.ZOrder.Reverse())
        {
            var surface = SurfaceById(window.Id);
            surface?.DrawNow();
            DrawModelViews(surface);
            if (_firstPostUiWindow)
            {
                _firstPostUiWindow = false;
                Log("LegACEy post-UI window draw reached through retail RenderDeviceD3D.EndScene.");
            }
        }
        ReleaseModelRenderers(_drawnModelViews);
        if (_windows.Resizing is { } resizing && _device != null)
            Guard(() => ScreenSurface.DrawOutline(_device, resizing.Bounds, ResizeOutline));
        // An item dragged out of a LegACEy window draws above everything.
        Guard(() => _dragIconSurface?.DrawNow());
    }

    /// <summary>
    /// A tester creates a file named fail-post-ui-draw beside the DLL to make the next post-UI draw fail in game,
    /// exercising the real EndScene failure path. Checked at most once a second, not every frame.
    /// </summary>
    private void ThrowIfTestFailureRequested()
    {
        var now = DateTime.UtcNow;
        if (now - _lastTestFailureCheck < TimeSpan.FromSeconds(1)) return;
        _lastTestFailureCheck = now;
        var marker = IOPath.Combine(PluginDirectory, TestFailureFile);
        if (!File.Exists(marker)) return;
        File.Delete(marker);
        Log($"Test failure requested by {TestFailureFile}; throwing from the post-UI draw.");
        throw new InvalidOperationException($"Test failure requested by {TestFailureFile}.");
    }

    /// <summary>
    /// Every 3D view in a window goes on top of that window's frame, before any window above it. A failure turns off
    /// only that view, until its window closes.
    /// </summary>
    private void DrawModelViews(ScreenSurface? surface)
    {
        if (surface is not { Visible: true } || _device == null || surface.Panel.Content is not Avalonia.Visual window) return;
        // ponytail: walks the window's visual tree every frame; cache the views per window if a big window makes it show up in the frame budget
        foreach (var view in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<ModelView>())
        {
            _drawnModelViews.Add(view);
            if (view.Model is not { } model || !view.IsEffectivelyVisible || _failedModelViews.Contains(view)) continue;
            try
            {
                var origin = Avalonia.VisualExtensions.TranslatePoint(view, default, window);
                if (origin == null) continue;
                var bounds = view.Bounds;
                var area = new Rectangle(surface.Location.X + (int)origin.Value.X, surface.Location.Y + (int)origin.Value.Y, (int)bounds.Width, (int)bounds.Height);
                if (!_modelRenderers.TryGetValue(view, out var renderer))
                    _modelRenderers[view] = renderer = new ModelRenderer(_device);
                renderer.Draw(model, area, view.Yaw, view.Zoom);
            }
            catch (Exception exception)
            {
                _failedModelViews.Add(view);
                Log($"A 3D view failed to draw; it stays off until its window closes: {exception}");
            }
        }
    }

    /// <summary>Frees the GPU resources of 3D views that are no longer in an open window (all of them for an empty set).</summary>
    private void ReleaseModelRenderers(ISet<ModelView> keep)
    {
        foreach (var view in _modelRenderers.Keys.Where(view => !keep.Contains(view)).ToList())
        {
            _modelRenderers[view].Dispose();
            _modelRenderers.Remove(view);
        }
        _failedModelViews.IntersectWith(keep);
    }

    /// <summary>Tells LegACEy drop targets about a drag in the retail UI, and where the pointer is over them.</summary>
    private void UpdateRetailDrag()
    {
        var item = _retailDrag?.CurrentItem() ?? 0;
        if (item != _retailDragItem)
        {
            _retailDragItem = item;
            _retailDragName = item == 0 ? string.Empty : ObjectName(item);
        }
        var top = item == 0 ? null : TopSurfaceAt(_pointer);
        if (top != null && _retailDragIcon == null)
            _retailDragIcon = ShowDragIcon(ObjectIcon(item), 1);
        else if (top == null && _retailDragIcon != null)
        {
            _retailDragIcon.Dispose();
            _retailDragIcon = null;
        }
        foreach (var pair in _featureSurfaces.ToArray())
        {
            // An earlier failure in this loop may have closed the window.
            if (!_featureSurfaces.ContainsKey(pair.Key) || pair.Value.Panel.Content is not IRetailItemDropTarget target) continue;
            var surface = pair.Value;
            try
            {
                target.RetailDragOver(item, _retailDragName, top == pair.Key
                    ? new Avalonia.Point(_pointer.X - surface.Location.X, _pointer.Y - surface.Location.Y)
                    : null);
            }
            catch (Exception exception) { _windowFailures[pair.Key](exception); }
        }
    }

    /// <summary>
    /// A retail drag released over a LegACEy window: end the drag without delivering it, so the item never drops
    /// onto whatever retail element or world is under the window, then offer it to the window. The button-up still
    /// reaches the client, which now sees an ordinary release with no drag in progress.
    /// </summary>
    private void HandleRetailDrop(Point point)
    {
        var item = _retailDrag?.CurrentItem() ?? 0;
        if (item == 0) return;
        var id = TopSurfaceAt(point);
        if (id == null) return;
        _retailDrag!.Cancel();
        var surface = SurfaceById(id);
        var accepted = false;
        if (surface?.Panel.Content is IRetailItemDropTarget target)
        {
            try
            {
                accepted = target.RetailDrop(item, ObjectName(item), new Avalonia.Point(point.X - surface.Location.X, point.Y - surface.Location.Y));
            }
            catch (Exception exception) { _windowFailures[id](exception); }
        }
        if (_loggedDrops++ < 5)
            Log($"Retail item 0x{item:X8} dropped on LegACEy window '{id}'; retail drag cancelled; accepted: {accepted}.");
        _retailDragItem = 0;
        UpdateRetailDrag();
    }

    private string? TopSurfaceAt(Point point)
    {
        InputSurface? top = null;
        foreach (var surface in GetInputSurfaces())
            if (point.X >= surface.X && point.Y >= surface.Y && point.X < surface.X + surface.Width && point.Y < surface.Y + surface.Height &&
                (top == null || surface.ZOrder > top.ZOrder))
                top = surface;
        return top?.Id;
    }

    private static string ObjectName(uint id)
    {
        try { return CoreManager.Current.WorldFilter[unchecked((int)id)]?.Name ?? string.Empty; }
        catch (COMException) { return string.Empty; }
    }

    /// <summary>The object's icon as the retail UI draws it: underlay, the icon with its UI-effect outline, then the secondary overlay.</summary>
    private GameImage? ObjectIcon(uint id)
    {
        try
        {
            var item = CoreManager.Current.WorldFilter[unchecked((int)id)];
            if (item == null || _portal == null) return null;
            // Decal reports portal texture ids without their 0x06 prefix.
            static uint Texture(int value) => value == 0 ? 0 : (value & 0xFF000000) == 0 ? unchecked((uint)value) | 0x06000000 : unchecked((uint)value);
            // Decal names the UI-effects value IconOutline (checked in Decal.Adapter.dll). Decal has no secondary overlay key, so none is drawn.
            return ItemIcon.Draw(_portal, Texture(item.Values(Decal.Adapter.Wrappers.LongValueKey.IconUnderlay)), Texture(item.Icon),
                Texture(item.Values(Decal.Adapter.Wrappers.LongValueKey.IconOverlay)), 0,
                unchecked((uint)item.Values(Decal.Adapter.Wrappers.LongValueKey.IconOutline)));
        }
        catch (COMException) { return null; }
    }

    /// <summary>Drag services for LegACEy windows: the floating icon and what lies under the pointer in the retail UI.</summary>
    private sealed class ItemDragHost : IItemDragHost
    {
        private readonly ClientUiRuntime _owner;
        public ItemDragHost(ClientUiRuntime owner) => _owner = owner;

        public IDisposable ShowDragIcon(GameImage? image, int count)
        {
            var icon = _owner.ShowDragIcon(image, count);
            _owner._itemDragActive = true;
            return new ItemDrag(_owner, icon);
        }

        private sealed class ItemDrag : IDisposable
        {
            private ClientUiRuntime? _owner;
            private readonly IDisposable _icon;
            public ItemDrag(ClientUiRuntime owner, IDisposable icon) { _owner = owner; _icon = icon; }
            public void Dispose()
            {
                if (_owner == null) return;
                _owner._itemDragActive = false;
                _owner.Guard(() => _owner._retailDrag?.ClearDropIndicator());
                _icon.Dispose();
                _owner = null;
            }
        }

        public ItemDropTarget DropTargetAtPointer()
        {
            var drag = _owner._retailDrag;
            if (drag == null) return ItemDropTarget.Inventory;
            var over = drag.IsPointerOverInventory(out var exists, out var open, out var element);
            // Without the panel element, any drop outside LegACEy windows withdraws to the pack.
            var target = !exists ? ItemDropTarget.Inventory : !open ? ItemDropTarget.InventoryClosed : over ? ItemDropTarget.Inventory : ItemDropTarget.Elsewhere;
            if (_owner._loggedDrops++ < 5)
                Log($"Item dropped at {_owner._pointer}: retail element under pointer 0x{element:X8}, inventory open: {open}, target: {target}.");
            return target;
        }
    }

    /// <summary>The "×count" badge for a drag icon's corner: the count over a dark shadow, as the Vault draws its stack counts.</summary>
    private static Avalonia.Controls.Control CountBadge(int count)
    {
        static Avalonia.Controls.TextBlock Line(string text, Avalonia.Media.IBrush brush, Avalonia.Thickness margin) => new()
        {
            Text = text, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontFamily = DerethPalette.Body, Foreground = brush, Margin = margin
        };
        var badge = new Avalonia.Controls.Panel
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Avalonia.Thickness(0, 0, 3, 1)
        };
        badge.Children.Add(Line($"×{count}", Avalonia.Media.Brushes.Black, new Avalonia.Thickness(1, 1, 0, 0)));
        badge.Children.Add(Line($"×{count}", DerethPalette.TextBrush, new Avalonia.Thickness(0, 0, 1, 1)));
        return badge;
    }

    private IDisposable ShowDragIcon(GameImage? image, int count)
    {
        HideDragIcon();
        if (_device == null || _portal == null) return new DragIcon(this);
        var bitmap = GameArtImageExtension.CreateBitmap(image);
        var layers = new Grid { Width = 32, Height = 32 };
        if (bitmap != null)
            layers.Children.Add(new Avalonia.Controls.Image { Source = bitmap, Width = 32, Height = 32, Stretch = Stretch.None });
        // a selection's drag carries its count in the icon's corner
        if (count > 1)
            layers.Children.Add(CountBadge(count));
        RenderOptions.SetBitmapInterpolationMode(layers, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        layers.DetachedFromVisualTree += (_, _) => bitmap?.Dispose();
        var panel = ObservePanel(AvaloniaPanel.Create(() => layers, 32, 32));
        _dragIconSurface = new ScreenSurface(_device, panel) { Visible = true, Location = new Point(_pointer.X - 16, _pointer.Y - 16) };
        return new DragIcon(this);
    }

    private void HideDragIcon()
    {
        _dragIconSurface?.Dispose();
        _dragIconSurface = null;
    }

    private sealed class DragIcon : IDisposable
    {
        private ClientUiRuntime? _owner;
        public DragIcon(ClientUiRuntime owner) => _owner = owner;
        public void Dispose()
        {
            _owner?.HideDragIcon();
            _owner = null;
        }
    }

    /// <summary>
    /// Hide the retail indicators bar whenever the client shows it, and put ours where it was.
    /// Checking every frame also catches the client showing it again, for example after a resize.
    /// </summary>
    private void TakeOverNativeBar()
    {
        _barTakeover ??= new RetailTakeoverLifecycle(new NativeBarPort(NativeUi.Indicators), new SurfaceTakeoverPort(_barSurface!));
        var viewport = _device!.Viewport;
        if (_barTakeover?.Tick(_dragOffset != null, new Size(viewport.Width, viewport.Height)) == true)
            _dragOffset = null;
    }

    IDisposable IClientUiHost.OpenWindow(WindowDefinition definition, Control content, Point requestedLocation)
    {
        if (!CanOpenWindows)
            throw new InvalidOperationException("LegACEy windows are unavailable until the post-UI renderer is ready.");
        OpenWindowCore(definition, content, requestedLocation, Disable);
        return new FeatureWindow(this, definition.Id);
    }

    private bool CanOpenWindows => _windowsEnabled && _windows != null && _device != null && _postUiDrawHook?.HasRun == true;

    private void OpenWindowCore(WindowDefinition definition, Control content, Point requestedLocation, Action<Exception> failed)
    {
        if (_featureSurfaces.ContainsKey(definition.Id))
            throw new InvalidOperationException($"A feature window named '{definition.Id}' is already registered.");
        var panel = ObservePanel(AvaloniaPanel.Create(() => content, definition.Width, definition.Height), failed);
        var surface = new ScreenSurface(_device!, panel);
        ShowSurface(definition, surface, requestedLocation, failed);
    }

    private void ShowSurface(WindowDefinition definition, ScreenSurface surface, Point requestedLocation, Action<Exception> failed)
    {
        var opened = false;
        try
        {
            var window = _windows!.Open(definition, requestedLocation);
            opened = true;
            FitSurface(surface, window);
            surface.Visible = true;
            surface.Panel.ApplyTheme(definition.Theme ?? _clientUi?.Theme ?? CurrentTheme());
            _featureSurfaces.Add(definition.Id, surface);
            _windowFailures[definition.Id] = failed;
        }
        catch
        {
            try { surface.Dispose(); }
            finally { if (opened) _windows!.Close(definition.Id); }
            throw;
        }
    }

    bool ILegACEyPluginHost.OpenWindow(WindowDefinition definition, Point location, Func<Action, Control> createContent, Action<Exception> failed, bool ownChrome)
    {
        if (!CanOpenWindows) return false;
        if (_hiddenSurfaces.TryGetValue(definition.Id, out var hidden))
        {
            _hiddenSurfaces.Remove(definition.Id);
            // Hiding runs inside the window's own click, so its hover and focus are cleared here instead. A focused
            // field left behind would take keys again after the next click in the window.
            hidden.Panel.PointerLeave();
            hidden.Panel.ClearFocus();
            ShowSurface(definition, hidden, location, failed);
            return true;
        }
        Action close = () => HideFeatureWindow(definition.Id);
        var content = createContent(close);
        try
        {
            if (ownChrome)
                OpenWindowCore(definition, content, location, failed);
            else
            {
                var chrome = new ThemeWindowChrome(_portal!, definition.Title, content);
                chrome.CloseRequested += (_, _) => close();
                OpenWindowCore(definition, chrome, location, failed);
            }
        }
        catch
        {
            // The window never opened, so nothing else releases the content the plugin built for it.
            (content as IDisposable)?.Dispose();
            throw;
        }
        return true;
    }

    IServerChannel ILegACEyPluginHost.ServerChannel => (IServerChannel?)_serverChannel ?? UnavailableServerChannel.Instance;
    string ILegACEyPluginHost.PortalPath => _portal?.Path ?? string.Empty;
    IGameArtSource ILegACEyPluginHost.Art => (IGameArtSource?)_portal ?? throw new InvalidOperationException("Game art is unavailable until the client UI is ready.");
    IItemDragHost ILegACEyPluginHost.ItemDrag => new ItemDragHost(this);
    IInventoryPort ILegACEyPluginHost.Inventory => _inventory;
    bool ILegACEyPluginHost.IsWindowOpen(string id) => _featureSurfaces.ContainsKey(id);
    void ILegACEyPluginHost.HideWindow(string id) => HideFeatureWindow(id);
    void ILegACEyPluginHost.CloseWindow(string id) => ReleaseFeatureWindow(id);

    private void HideFeatureWindow(string id)
    {
        if (!_featureSurfaces.TryGetValue(id, out var surface)) return;
        RemoveFeatureWindow(id, surface);
        _hiddenSurfaces[id] = surface;
    }

    private void ReleaseFeatureWindow(string id)
    {
        if (_hiddenSurfaces.TryGetValue(id, out var hidden))
        {
            _hiddenSurfaces.Remove(id);
            hidden.Dispose();
        }
        if (!_featureSurfaces.TryGetValue(id, out var surface)) return;
        RemoveFeatureWindow(id, surface);
        surface.Dispose();
    }

    private void RemoveFeatureWindow(string id, ScreenSurface surface)
    {
        if (_hovered == surface) _hovered = null;
        // A hidden or closed window must not reopen with its corner lit.
        if (_cornerWindowId == id)
        {
            SetWindowCorner(id, DerethCorner.None);
            _cornerWindowId = null;
            _cornerApplied = DerethCorner.None;
        }
        surface.Visible = false;
        _featureSurfaces.Remove(id);
        _windowFailures.Remove(id);
        _windows?.Close(id);
    }

    private void RestoreNativeBar()
    {
        try
        {
            _barTakeover?.Dispose();
        }
        catch (Exception exception)
        {
            Log($"Could not restore the retail indicators bar: {exception}");
            return;
        }
        _barTakeover = null;
    }

    /// <summary>Translate Decal's raw messages into router decisions and Avalonia.Headless input.</summary>
    private void OnWindowMessage(object? sender, WindowMessageEventArgs e)
    {
        if (_failed || !_inGame || _barSurface == null)
            return;

        var lParam = e.LParam;
        if (e.Msg == WmSetCursor)
        {
            // Over a resize edge, or while moving a window, our cursor replaces the game's; the eaten message keeps the game from resetting it.
            if ((lParam & 0xffff) == HtClient)
                Guard(() =>
                {
                    var cursor = WindowCursorAt(_pointer);
                    if (cursor == IntPtr.Zero) return;
                    SetCursor(cursor);
                    e.Eat = true;
                });
            return;
        }
        if (e.Msg == InputRouterService.WmMouseMove)
            _pointer = new Point((short)(lParam & 0xffff), (short)((lParam >> 16) & 0xffff));
        if (e.Msg == InputRouterService.WmLButtonUp && _inputRouter.CapturedSurfaceId == null)
            Guard(() => HandleRetailDrop(new Point((short)(lParam & 0xffff), (short)((lParam >> 16) & 0xffff))));
        if (e.Msg == InputRouterService.WmMouseWheel)
        {
            var point = new NativePoint
            {
                X = unchecked((short)(lParam & 0xffff)),
                Y = unchecked((short)((lParam >> 16) & 0xffff))
            };
            var window = GetForegroundWindow();
            if (window != IntPtr.Zero && ScreenToClient(window, ref point))
                lParam = (point.X & 0xffff) | (point.Y << 16);
        }

        var route = _inputRouter.Route(
            new NativeInputMessage(e.Msg, new IntPtr(e.WParam), new IntPtr(lParam)),
            GetInputSurfaces());
        GuardInput(route, () =>
        {
            var target = route.SurfaceId == null ? null : SurfaceById(route.SurfaceId);
            switch (route.Action)
            {
                case InputAction.PointerMove:
                    var point = new Point((short)(lParam & 0xffff), (short)((lParam >> 16) & 0xffff));
                    _pointer = point;
                    if (target != _hovered)
                    {
                        _hovered?.Panel.PointerLeave();
                        _hovered = target;
                    }
                    if (_windows?.IsDragging == true)
                    {
                        _windows.Move(point);
                        SyncWindowLocations();
                    }
                    else if (_dragOffset != null)
                        DragBar(point);
                    else
                        target?.Panel.PointerMove(route.X, route.Y);
                    break;
                case InputAction.PointerDown:
                    // The window that had focus loses it to this one.
                    SurfaceById(route.ClearFocusSurfaceId)?.Panel.ClearFocus();
                    _pointer = new Point(route.X + target!.Location.X, route.Y + target.Location.Y);
                    if (route.SurfaceId != null && _windows?.Get(route.SurfaceId) != null)
                    {
                        _windows.Press(_pointer);
                        SyncWindowLocations();
                    }
                    target.Panel.PointerDown(route.X, route.Y, ToKeyModifiers(route.Modifiers));
                    e.Eat = route.Eat;
                    break;
                case InputAction.PointerUp:
                    target!.Panel.PointerUp(route.X, route.Y);
                    if (_windows?.IsDragging == true)
                    {
                        _windows.Release();
                        SyncWindowLocations();
                    }
                    if (_dragOffset != null)
                        EndBarDrag();
                    e.Eat = route.Eat;
                    break;
                case InputAction.MouseWheel:
                    target!.Panel.MouseWheel(route.X, route.Y, 0, route.WheelDelta / 120.0, ToKeyModifiers(route.Modifiers));
                    e.Eat = route.Eat;
                    break;
                case InputAction.KeyDown:
                    target!.Panel.KeyDown(Win32KeyMap.ToAvaloniaKey(route.KeyCode), ToKeyModifiers(route.Modifiers));
                    e.Eat = route.Eat;
                    break;
                case InputAction.KeyUp:
                    target!.Panel.KeyUp(Win32KeyMap.ToAvaloniaKey(route.KeyCode), ToKeyModifiers(route.Modifiers));
                    e.Eat = route.Eat;
                    break;
                case InputAction.TextInput:
                    target!.Panel.TextInput(((char)route.KeyCode).ToString());
                    e.Eat = route.Eat;
                    break;
                case InputAction.ClearFocus:
                    SurfaceById(route.ClearFocusSurfaceId)?.Panel.ClearFocus();
                    break;
                case InputAction.Reset:
                    ApplyReset(route);
                    break;
                case InputAction.None when e.Msg == InputRouterService.WmMouseMove:
                    _hovered?.Panel.PointerLeave();
                    _hovered = null;
                    break;
            }
            if (e.Msg == InputRouterService.WmMouseMove)
                UpdateWindowCorner(route.SurfaceId, _pointer);
            if (route.Eat)
                e.Eat = true;
        });
    }

    /// <summary>
    /// Brightens the corner under the pointer on its window's frame. The resize cursor itself is set on WM_SETCURSOR, which
    /// the game sends before each mouse move, so the game's own handler cannot put its cursor back over an edge.
    /// </summary>
    private void UpdateWindowCorner(string? surfaceId, Point point)
    {
        if (_windows == null) return;
        var hover = surfaceId == "bar" ? default : _windows.HoverAt(point);
        var corner = CornerOf(hover.Edges);
        if (hover.WindowId == _cornerWindowId && corner == _cornerApplied) return;
        SetWindowCorner(_cornerWindowId, DerethCorner.None);
        SetWindowCorner(hover.WindowId, corner);
        _cornerWindowId = hover.WindowId;
        _cornerApplied = corner;
    }

    private void SetWindowCorner(string? windowId, DerethCorner corner)
    {
        var surface = SurfaceById(windowId);
        if (surface == null) return;
        foreach (var frame in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(surface.Panel.Content).OfType<DerethFrame>())
            frame.HoveredCorner = corner;
    }

    private static DerethCorner CornerOf(WindowEdges edges)
    {
        if (edges == (WindowEdges.Left | WindowEdges.Top)) return DerethCorner.TopLeft;
        if (edges == (WindowEdges.Right | WindowEdges.Top)) return DerethCorner.TopRight;
        if (edges == (WindowEdges.Left | WindowEdges.Bottom)) return DerethCorner.BottomLeft;
        if (edges == (WindowEdges.Right | WindowEdges.Bottom)) return DerethCorner.BottomRight;
        return DerethCorner.None;
    }

    /// <summary>
    /// The cursor for a point: the move cursor while a window is being moved, the resize cursor over a window edge or corner,
    /// or zero where the game's own cursor stands.
    /// </summary>
    private IntPtr WindowCursorAt(Point point)
    {
        if (_windows == null) return IntPtr.Zero;
        if (_windows.Moving != null) return RetailCursor(DatCursorMove, IdcSizeAll);
        var hover = _windows.HoverAt(point);
        // The pointer goes to a capture, else to the topmost surface under it, as the router decides. Only the window that owns
        // it gets the resize cursor, so the retail bar and any other surface keep the game's cursor.
        var owner = _inputRouter.CapturedSurfaceId ?? InputRouterService.SurfaceAt(point.X, point.Y, GetInputSurfaces());
        if (hover.WindowId == null || owner != hover.WindowId) return IntPtr.Zero;
        var horizontal = hover.Edges & (WindowEdges.Left | WindowEdges.Right);
        var vertical = hover.Edges & (WindowEdges.Top | WindowEdges.Bottom);
        if (horizontal == WindowEdges.None && vertical == WindowEdges.None) return IntPtr.Zero;
        if (vertical == WindowEdges.None) return RetailCursor(DatCursorWE, IdcSizeWE);
        if (horizontal == WindowEdges.None) return RetailCursor(DatCursorNS, IdcSizeNS);
        return hover.Edges == (WindowEdges.Left | WindowEdges.Top) || hover.Edges == (WindowEdges.Right | WindowEdges.Bottom)
            ? RetailCursor(DatCursorNWSE, IdcSizeNWSE)
            : RetailCursor(DatCursorNESW, IdcSizeNESW);
    }

    /// <summary>A retail cursor from the DAT, made once; the standard Windows cursor if the DAT image can't be read.</summary>
    private IntPtr RetailCursor(uint imageId, int fallback)
    {
        if (!_datCursors.TryGetValue(imageId, out var cursor))
        {
            try { cursor = _portal?.ReadImage(imageId) is { } image ? CreateCursor(image) : IntPtr.Zero; }
            catch (Exception exception)
            {
                Log($"Cursor 0x{imageId:X8} could not be made from the DAT: {exception.Message}");
                cursor = IntPtr.Zero;
            }
            _datCursors.Add(imageId, cursor);
        }
        if (cursor != IntPtr.Zero) return cursor;
        if (!_systemCursors.TryGetValue(fallback, out cursor))
            _systemCursors.Add(fallback, cursor = LoadCursor(IntPtr.Zero, (IntPtr)fallback));
        return cursor;
    }

    /// <summary>An alpha cursor from a premultiplied BGRA image, its hotspot in the middle.</summary>
    private static IntPtr CreateCursor(GameImage image)
    {
        // The colour bitmap carries the alpha; the 1-bit mask is all zero, so it adds nothing.
        var color = CreateBitmap(image.Width, image.Height, 1, 32, image.Pixels);
        var mask = CreateBitmap(image.Width, image.Height, 1, 1, new byte[(image.Width + 15) / 16 * 2 * image.Height]);
        try
        {
            var info = new IconInfo { IsIcon = false, HotspotX = image.Width / 2, HotspotY = image.Height / 2, Mask = mask, Color = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    private void GuardInput(InputRoute route, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Disable(exception);
        }
    }

    private AvaloniaPanel ObservePanel(AvaloniaPanel panel, Action<Exception>? failed = null)
    {
        panel.Error += failed ?? new Action<Exception>(Disable);
        if (panel.LastError is { } initialError)
        {
            panel.Dispose();
            throw initialError;
        }
        return panel;
    }

    private InputSurface[] GetInputSurfaces()
    {
        var surfaces = new System.Collections.Generic.List<InputSurface>(4);
        if (_barSurface is { Visible: true })
            surfaces.Add(new InputSurface("bar", _barSurface.Location.X, _barSurface.Location.Y, _barSurface.Bounds.Width, _barSurface.Bounds.Height, 0, _barSurface.Panel.WantsKeyboard));
        if (_windows != null)
        {
            var z = _windows.ZOrder.Count;
            foreach (var window in _windows.ZOrder)
            {
                var surface = SurfaceById(window.Id);
                if (surface is not { Visible: true }) continue;
                surfaces.Add(new InputSurface(window.Id, window.Location.X, window.Location.Y, window.Width, window.Height, z--, surface.Panel.WantsKeyboard));
            }
        }
        return surfaces.ToArray();
    }

    private ScreenSurface? SurfaceById(string? id)
    {
        if (id == "bar") return _barSurface;
        if (id != null && _featureSurfaces.TryGetValue(id, out var surface)) return surface;
        return null;
    }

    private void ApplyReset(InputRoute route)
    {
        SurfaceById(route.ReleaseCaptureSurfaceId)?.Panel.PointerUp(-1, -1);
        SurfaceById(route.ClearFocusSurfaceId)?.Panel.ClearFocus();
        _hovered?.Panel.PointerLeave();
        _hovered = null;
        _dragOffset = null;
        _windows?.Release();
    }

    private void SyncWindowLocations()
    {
        if (_windows == null) return;
        // A window being resized keeps its surface until release: relaying it out on every mouse move stalls the game,
        // so an outline shows the new bounds meanwhile.
        foreach (var window in _windows.ZOrder)
            if (window != _windows.Resizing)
                FitSurface(SurfaceById(window.Id)!, window);
    }

    /// <summary>Puts a window's surface where the window manager has it, at the window's size. A new size re-lays out the panel.</summary>
    private static void FitSurface(ScreenSurface surface, ManagedWindow window)
    {
        surface.Location = window.Location;
        if (surface.Panel.Frame.Width != window.Width || surface.Panel.Frame.Height != window.Height)
            surface.Panel.Resize(window.Width, window.Height);
    }

    private static KeyModifiers ToKeyModifiers(InputModifiers modifiers)
    {
        var result = KeyModifiers.None;
        if ((modifiers & InputModifiers.Shift) != 0) result |= KeyModifiers.Shift;
        if ((modifiers & InputModifiers.Control) != 0) result |= KeyModifiers.Control;
        if ((modifiers & InputModifiers.Alt) != 0) result |= KeyModifiers.Alt;
        if ((modifiers & InputModifiers.Meta) != 0) result |= KeyModifiers.Meta;
        return result;
    }

    /// <summary>Run UI work from a game callback; any exception disables the replacement instead of escaping.</summary>
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Disable(exception);
        }
    }

    /// <summary>Stop drawing, give the player the retail bar back, and log why.</summary>
    private void Disable(Exception exception)
    {
        // Every panel drains the shared Avalonia dispatcher, so a plugin's failure can surface on another panel.
        if (_plugins?.TryFailOwner(exception) == true) return;
        _failed = true;
        Log($"Indicator bar replacement disabled: {exception}");
        RestoreNativeBar();
        TearDown();
    }

    /// <summary>Disable only the post-UI windows when EndScene fails; retail surfaces continue.</summary>
    private void DisableWindows(Exception exception)
    {
        _windowsEnabled = false;
        Log($"LegACEy windows disabled: {exception}");
        _bar?.SetOpen(MenuSlot, false);
        foreach (var window in _windows?.ZOrder.ToArray() ?? Array.Empty<ManagedWindow>())
            _windows!.Close(window.Id);
        try { _clientUi?.EndSession(); }
        catch (Exception cleanupError) { Log($"Could not clean up feature UI after window failure: {cleanupError}"); }
        _clientUi = null;
        // Plugin windows are not in the client UI framework; release them here too.
        ReleaseFeatureWindows();
    }

    private void ReleaseFeatureWindows()
    {
        foreach (var id in _featureSurfaces.Keys.Concat(_hiddenSurfaces.Keys).ToArray())
            ReleaseFeatureWindow(id);
    }

    private static string SessionCharacter() => CoreManager.Current.CharacterFilter.Name;
    private static string SessionServer() => CoreManager.Current.CharacterFilter.Server;

    private void PublishGameState() => _gameStatePoller.Poll(_inGame);

    private static GameStateSnapshot ReadGameState()
    {
        var filter = CoreManager.Current.CharacterFilter;
        return new GameStateSnapshot(SessionCharacter(), SessionServer(),
            filter.Health, filter.EffectiveVital[Decal.Adapter.Wrappers.CharFilterVitalType.Health],
            filter.Stamina, filter.EffectiveVital[Decal.Adapter.Wrappers.CharFilterVitalType.Stamina],
            filter.Mana, filter.EffectiveVital[Decal.Adapter.Wrappers.CharFilterVitalType.Mana]);
    }

    private sealed class FeatureWindow : IDisposable
    {
        private readonly ClientUiRuntime _owner;
        private readonly string _id;
        public FeatureWindow(ClientUiRuntime owner, string id) { _owner = owner; _id = id; }
        public void Dispose() => _owner.ReleaseFeatureWindow(_id);
    }

    private sealed class NativeBarPort : IRetailTakeoverPort
    {
        private readonly uint _rootId;
        public NativeBarPort(uint rootId) => _rootId = rootId;
        private IntPtr Element => NativeUi.GetElement(_rootId);
        public bool Exists => Element != IntPtr.Zero;
        public IntPtr ElementIdentity => Element;
        public bool IsVisible { get { var element = Element; return element != IntPtr.Zero && NativeUi.IsVisible(element); } }
        public Rectangle GetBounds() { var element = Element; if (element == IntPtr.Zero) throw new InvalidOperationException("Retail indicators element disappeared."); return NativeUi.GetBounds(element); }
        public void SetVisible(bool visible) { var element = Element; if (element != IntPtr.Zero) NativeUi.SetVisible(element, visible); }
        public void SetSaveLocation(bool save) { var element = Element; if (element == IntPtr.Zero) throw new InvalidOperationException("Retail indicators element disappeared."); NativeUi.SetSaveLocation(element, save); }
        public void MoveTo(Point location) { var element = Element; if (element == IntPtr.Zero) throw new InvalidOperationException("Retail indicators element disappeared."); NativeUi.MoveTo(element, location); }
        public bool IsUiLocked => NativeUi.IsUiLocked;
    }

    private sealed class SurfaceTakeoverPort : IRetailTakeoverSurface
    {
        private readonly ScreenSurface _surface;
        public SurfaceTakeoverPort(ScreenSurface surface) => _surface = surface;
        public Point Location => _surface.Location;
        public Size Size => _surface.Bounds.Size;
        public bool Visible { get => _surface.Visible; set => _surface.Visible = value; }
        public void SetLocation(Point location) => _surface.Location = location;
    }

    private void TearDown()
    {
        if (_itemDragActive)
            try { _retailDrag?.ClearDropIndicator(); } catch (Exception exception) { Log($"Could not clear the inventory drop indicator: {exception.Message}"); }
        _itemDragActive = false;
        _retailDragIcon = null;
        HideDragIcon();
        try { _clientUi?.EndSession(); }
        catch (Exception exception) { Log($"Could not clean up feature UI during unload: {exception}"); }
        _clientUi = null;
        try { _serverChannel?.Reset(); }
        catch (Exception exception) { Log($"Could not fail outstanding server channel requests: {exception}"); }
        RestoreNativeBar();
        _hovered = null;
        _barSurface?.Dispose();
        _barSurface = null;
        _barRenderer = null;
        _postUiDrawHook?.Dispose();
        _postUiDrawHook = null;
        _windows = null;
        ReleaseFeatureWindows();
        _windowFailures.Clear();
        _windowsEnabled = false;
        _portal?.Dispose();
        _portal = null;
        foreach (var cursor in _datCursors.Values)
            if (cursor != IntPtr.Zero) DestroyCursor(cursor);
        _datCursors.Clear();
        _bar = null;
        _drawnModelViews.Clear();
        ReleaseModelRenderers(_drawnModelViews);
        GameArtImageExtension.CurrentSource = null;
    }
}
