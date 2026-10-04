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
internal sealed class ClientUiRuntime : IClientUiHost
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

    private const int InputTestWidth = 360;
    private const int InputTestHeight = 300;
    private const int InputWindowWidth = 380;
    private const int InputWindowHeight = 350;
    private const int BreakoutWindowWidth = 500;
    private const int BreakoutWindowHeight = 390;
    private const string InputTestSlot = "Input test";
    private const string ThemeGallerySlot = "Theme gallery";
    private const string BreakoutSlot = "Breakout";
    private const string LiveDataWindowId = "live-game-data";
    private const string ElementInspectorWindowId = "element-inspector";
    private static readonly string PluginDirectory = IOPath.GetDirectoryName(typeof(ClientUiRuntime).Assembly.Location)!;

    private Device? _device;
    private PortalDat? _portal;
    private IndicatorBar? _bar;
    private BreakoutGame? _breakout;
    private ScreenSurface? _barSurface;
    private RetailSurfaceRenderer? _barRenderer;
    private WindowManager? _windows;
    private PostUiDrawHook? _postUiDrawHook;
    private bool _windowsEnabled;
    private bool _firstPostUiWindow = true;
    private DateTime _lastBreakoutStep = DateTime.UtcNow;
    private ScreenSurface? _hovered;
    private readonly InputRouterService _inputRouter = new();
    private Point _pointer;
    private Size? _dragOffset;
    private RetailTakeoverLifecycle? _barTakeover;
    private readonly GameStatePort _gameState = new(new GameStateSnapshot("unknown-character", "unknown-server", 0, 0, 0, 0, 0, 0));
    private ClientUiFramework? _clientUi;
    private readonly Dictionary<string, ScreenSurface> _featureSurfaces = new(StringComparer.Ordinal);
    private readonly List<FeatureTakeover> _featureTakeovers = new();
    private bool _inGame;
    private bool _failed;
    private bool _acThemeActive = true;

    public void Startup()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginDirectory;
        NativeUi.Initialize(Log);
        CoreManager.Current.FilterInitComplete += OnFilterInitComplete;
    }

    public void Shutdown()
    {
        CoreManager.Current.FilterInitComplete -= OnFilterInitComplete;
        CoreManager.Current.CharacterFilter.LoginComplete -= OnLoginComplete;
        CoreManager.Current.CharacterFilter.Logoff -= OnLogoff;
        CoreManager.Current.RenderFrame -= OnRenderFrame;
        CoreManager.Current.WindowMessage -= OnWindowMessage;
        _postUiDrawHook?.Dispose();
        _postUiDrawHook = null;
        RestoreNativeBar();
        TearDown();
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
            File.AppendAllText(IOPath.Combine(PluginDirectory, "legacey-avalonia.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}");
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
    }

    /// <summary>Enable character-specific windows once Decal has finished loading character identity.</summary>
    private void OnLoginComplete(object? sender, EventArgs e)
    {
        if (_failed || !NativeUi.Ready)
            return;

        Guard(() =>
        {
            PublishGameState();
            if (_barSurface == null)
                CreateUi();
            if (_windows == null)
                CreateWindowManager();
            _clientUi ??= new ClientUiFramework(this, _gameState, CurrentTheme());
            _inGame = true;
        });
    }

    private void OnLogoff(object? sender, EventArgs e)
    {
        Guard(() => ApplyReset(_inputRouter.Route(new NativeInputMessage(InputRouterService.WmLogoff, IntPtr.Zero, IntPtr.Zero), GetInputSurfaces())));
        Guard(() => _hovered?.Panel.PointerLeave());
        _inGame = false;
        try { _clientUi?.EndSession(); }
        catch (Exception exception) { Log($"Could not clean up client UI at logoff: {exception}"); }
        _clientUi = null;
        // Keep replacing retail roots through logout. RenderFrame hides any native
        // re-show until the element disappears; only unload or failure gives it back.
        foreach (var window in _windows?.ZOrder.ToArray() ?? Array.Empty<ManagedWindow>())
            _windows!.Close(window.Id);
        _windows = null;
        _hovered = null;
        _dragOffset = null;
        foreach (var slot in new[] { InputTestSlot, ThemeGallerySlot, BreakoutSlot })
            _bar?.SetOpen(slot, false);
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
            new IndicatorSlot(InputTestSlot, 0x06004D20, ToggleInputTest, "B"),
            new IndicatorSlot(ThemeGallerySlot, AcClientTheme.WindowChromeCenterId, ToggleThemeGallery, "T"),
            new IndicatorSlot(BreakoutSlot, 0x06004D20, ToggleBreakout, "R"),
            new IndicatorSlot("Live data", 0x06004D20, ToggleLiveData, "V"),
            new IndicatorSlot("Element inspector", 0x06004D20, ToggleElementInspector, "I"),
            new IndicatorSlot("Log out", 0x060074B1, NativeUi.RequestLogOut)
        };
        var size = IndicatorBar.MeasureFor(slots.Length);
        var barPanel = AvaloniaPanel.Create(() => _bar = new IndicatorBar(slots, _portal.ReadImage), size.Width, size.Height);
        _barSurface = new ScreenSurface(_device, barPanel);
        _barRenderer = new RetailSurfaceRenderer(_barSurface.Prepare, _barSurface.DrawNow);
        _barTakeover = new RetailTakeoverLifecycle(new NativeBarPort(NativeUi.Indicators), new SurfaceTakeoverPort(_barSurface));

        ApplyCurrentTheme();
        EnsurePostUiDrawHook();
        Log("Indicator bar replacement ready.");
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
        _postUiDrawHook ??= new PostUiDrawHook(DrawAfterRetailUi, DisableWindows);
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

    private void ToggleThemeGallery()
    {
        ToggleWindow("theme-gallery", 580, 560, new Point(120, 70));
    }

    private void SwitchTheme()
    {
        _acThemeActive = !_acThemeActive;
        if (_clientUi != null) _clientUi.SetTheme(CurrentTheme());
        else ApplyCurrentTheme();
    }

    private void ApplyCurrentTheme()
    {
        ApplyTheme(CurrentTheme());
    }

    void IClientUiHost.ApplyTheme(IClientTheme theme) => ApplyTheme(theme);

    private void ApplyTheme(IClientTheme theme)
    {
        _barSurface?.Panel.ApplyTheme(theme);
        foreach (var surface in _featureSurfaces.Values)
            surface.Panel.ApplyTheme(theme);
        foreach (var takeover in _featureTakeovers)
            takeover.Surface?.Panel.ApplyTheme(theme);
    }

    private IClientTheme CurrentTheme() => _acThemeActive && _portal != null ? new AcClientTheme(_portal) : new SimpleClientTheme();

    private void ToggleLiveData()
    {
        if (_clientUi == null || _windows == null || !_windowsEnabled) return;
        if (_windows.Get(LiveDataWindowId) != null) { _clientUi.CloseWindow(LiveDataWindowId); return; }
        var panel = new LiveGameDataPanel(_gameState);
        var chrome = new ThemeWindowChrome(_portal!, "Live game data", panel);
        chrome.CloseRequested += (_, _) => _clientUi?.CloseWindow(LiveDataWindowId);
        chrome.DetachedFromVisualTree += (_, _) => panel.Dispose();
        _clientUi.OpenWindow(new WindowDefinition(LiveDataWindowId, "Live game data", 360, 260), chrome);
        ApplyCurrentTheme();
    }

    private void ToggleElementInspector()
    {
        if (_clientUi == null || _windows == null || !_windowsEnabled) return;
        if (_windows.Get(ElementInspectorWindowId) != null) { _clientUi.CloseWindow(ElementInspectorWindowId); return; }
        var prefix = "RootElementId::";
        var roots = NativeUiCatalogue.Entries
            .Where(entry => entry.ConstantValue.HasValue && entry.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => new RetailRootDescriptor(entry.Name.Substring(prefix.Length), entry.ConstantValue!.Value))
            .ToArray();
        var inspector = new ElementInspectorControl(roots,
            id => { var element = NativeUi.GetElement(id); return element != IntPtr.Zero && NativeUi.IsVisible(element); },
            id => { var element = NativeUi.GetElement(id); return element == IntPtr.Zero ? Rectangle.Empty : NativeUi.GetBounds(element); },
            id => _clientUi!.HideRoot(id),
            (id, location) => _clientUi!.MoveRoot(id, location),
            id => _clientUi!.TakeOverRoot(id, new Border { Background = Avalonia.Media.Brushes.Black, Child = new TextBlock { Text = "LegACEy placeholder surface", Margin = new Avalonia.Thickness(12), Foreground = Avalonia.Media.Brushes.White } }));
        var chrome = new ThemeWindowChrome(_portal!, "Element inspector", inspector);
        chrome.CloseRequested += (_, _) => _clientUi?.CloseWindow(ElementInspectorWindowId);
        chrome.DetachedFromVisualTree += (_, _) => inspector.Dispose();
        _clientUi.OpenWindow(new WindowDefinition(ElementInspectorWindowId, "Element inspector", 660, 480), chrome);
        ApplyCurrentTheme();
    }

    private void ToggleInputTest()
    {
        ToggleWindow("input-test", InputWindowWidth, InputWindowHeight, new Point(120, 70));
    }

    private void ToggleBreakout() => ToggleWindow("breakout", BreakoutWindowWidth, BreakoutWindowHeight, new Point(180, 80));

    private void ToggleWindow(string id, int width, int height, Point defaultLocation)
    {
        if (!_windowsEnabled || _windows == null || _clientUi == null)
            return;
        if (_postUiDrawHook?.HasRun != true)
        {
            Log("LegACEy windows unavailable: the installed retail EndScene hook has not run. Keeping the indicator bar interactive.");
            return;
        }
        if (_windows.Get(id) != null)
        {
            _clientUi.CloseWindow(id);
            _bar?.SetOpen(SlotByWindowId(id), false);
            return;
        }
        _clientUi.OpenWindow(new WindowDefinition(id, id, width, height), CreateFeatureWindowContent(id), defaultLocation);
        _bar?.SetOpen(SlotByWindowId(id), true);
    }

    private Control CreateFeatureWindowContent(string id)
    {
        Control content;
        switch (id)
        {
            case "input-test":
                content = new InputTestPanel(InputTestWidth, InputTestHeight);
                break;
            case "theme-gallery":
                var gallery = new ThemeGalleryControl();
                gallery.ThemeSwitchRequested += (_, _) => SwitchTheme();
                content = gallery;
                break;
            case "breakout":
                _breakout = new BreakoutGame(BreakoutWindowWidth - 16, BreakoutWindowHeight - 42);
                content = _breakout;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown feature window.");
        }
        var chrome = new ThemeWindowChrome(_portal!, id, content);
        chrome.CloseRequested += (_, _) =>
        {
            _clientUi?.CloseWindow(id);
            _bar?.SetOpen(SlotByWindowId(id), false);
        };
        return chrome;
    }

    private static string SlotByWindowId(string id) => id switch
    {
        "input-test" => InputTestSlot,
        "theme-gallery" => ThemeGallerySlot,
        "breakout" => BreakoutSlot,
        _ => id
    };

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
        if (_barTakeover != null && _failed)
            RestoreNativeBar();
        if (_failed || !NativeUi.Ready)
            return;

        Guard(() =>
        {
            PublishGameState();
            if (_barSurface == null)
            {
                var element = NativeUi.GetElement(NativeUi.Indicators);
                if (element == IntPtr.Zero || !NativeUi.IsVisible(element))
                    return;
                CreateUi();
            }
            TakeOverNativeBar();
            foreach (var takeover in _featureTakeovers.ToArray())
            {
                takeover.Lifecycle.Tick(viewport: new Size(_device!.Viewport.Width, _device.Viewport.Height));
                takeover.Renderer?.RenderFrame(_inGame, _postUiDrawHook?.IsInstalled == true);
            }
            _barRenderer!.RenderFrame(_inGame, _postUiDrawHook?.IsInstalled == true);
        });

        if (_failed || !_inGame || !_windowsEnabled)
            return;

        GuardWindows(() =>
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

            foreach (var surface in _featureSurfaces.Values)
                PrepareWindow(surface);
            if (_breakout is { } breakout)
            {
                var now = DateTime.UtcNow;
                breakout.Step(now - _lastBreakoutStep);
                _lastBreakoutStep = now;
            }
        });
    }

    private static void PrepareWindow(ScreenSurface? surface)
    {
        if (surface is { Visible: true })
            surface.Prepare();
    }

    /// <summary>Called by IDirect3DDevice9.EndScene after retail and Decal UI drawing.</summary>
    private void DrawAfterRetailUi()
    {
        if (_failed) return;
        Guard(() =>
        {
            _barRenderer?.DrawAfterRetailUi();
            foreach (var takeover in _featureTakeovers.ToArray())
                takeover.Renderer?.DrawAfterRetailUi();
        });
        if (_failed || !_windowsEnabled || _windows == null)
            return;
        foreach (var window in _windows.ZOrder.Reverse())
        {
            SurfaceById(window.Id)?.DrawNow();
            if (_firstPostUiWindow)
            {
                _firstPostUiWindow = false;
                Log("LegACEy post-UI window draw reached through retail RenderDeviceD3D.EndScene.");
            }
        }
    }

    /// <summary>
    /// Hide the retail indicators bar whenever the client shows it, and put ours where it was.
    /// Checking every frame also catches the client showing it again, for example after a resize.
    /// </summary>
    private void TakeOverNativeBar()
    {
        if (_featureTakeovers.Any(takeover => takeover.RootElementId == NativeUi.Indicators)) return;
        _barTakeover ??= new RetailTakeoverLifecycle(new NativeBarPort(NativeUi.Indicators), new SurfaceTakeoverPort(_barSurface!));
        var viewport = _device!.Viewport;
        if (_barTakeover?.Tick(_dragOffset != null, new Size(viewport.Width, viewport.Height)) == true)
            _dragOffset = null;
    }

    IDisposable IClientUiHost.OpenWindow(WindowDefinition definition, Control content, Point requestedLocation)
    {
        if (!_windowsEnabled || _windows == null || _device == null || _postUiDrawHook?.HasRun != true)
            throw new InvalidOperationException("LegACEy windows are unavailable until the post-UI renderer is ready.");
        if (_featureSurfaces.ContainsKey(definition.Id))
            throw new InvalidOperationException($"A feature window named '{definition.Id}' is already registered.");
        var panel = AvaloniaPanel.Create(() => content, definition.Width, definition.Height);
        var surface = new ScreenSurface(_device, panel);
        var opened = false;
        try
        {
            var window = _windows.Open(definition, requestedLocation);
            opened = true;
            surface.Location = window.Location;
            surface.Visible = true;
            panel.ApplyTheme(_clientUi?.Theme ?? CurrentTheme());
            _featureSurfaces.Add(definition.Id, surface);
            return new FeatureWindow(this, definition.Id);
        }
        catch
        {
            try { surface.Dispose(); }
            finally { if (opened) _windows.Close(definition.Id); }
            throw;
        }
    }

    IDisposable IClientUiHost.TakeOverRoot(uint rootElementId, Control content) => RegisterRoot(rootElementId, content);
    IDisposable IClientUiHost.HideRoot(uint rootElementId) => RegisterRoot(rootElementId, null);

    private IDisposable RegisterRoot(uint rootElementId, Control? content)
    {
        if (_device == null)
            throw new InvalidOperationException("The game rendering device is unavailable.");
        if (_featureTakeovers.Any(takeover => takeover.RootElementId == rootElementId))
            throw new InvalidOperationException("The retail root already has a feature owner.");
        ScreenSurface? surface = null;
        RetailTakeoverLifecycle? lifecycle = null;
        try
        {
            if (content != null)
            {
                var panel = AvaloniaPanel.Create(() => content, 280, 120);
                surface = new ScreenSurface(_device, panel);
                panel.ApplyTheme(_clientUi?.Theme ?? CurrentTheme());
            }
            // Give the inspector exclusive ownership of the bar's retail root.
            if (rootElementId == NativeUi.Indicators)
            {
                RestoreNativeBar();
                if (_barTakeover != null) throw new InvalidOperationException("Could not release the indicators bar.");
                _barSurface!.Visible = false;
            }
            lifecycle = new RetailTakeoverLifecycle(new NativeElementPort(rootElementId),
                surface == null ? new MoveOnlySurface() : new SurfaceTakeoverPort(surface));
            lifecycle.Tick();
            var registration = new FeatureTakeover(this, rootElementId, lifecycle, surface);
            _featureTakeovers.Add(registration);
            return registration;
        }
        catch
        {
            try { lifecycle?.Dispose(); }
            finally { surface?.Dispose(); }
            throw;
        }
    }

    bool IClientUiHost.MoveRoot(uint rootElementId, Point location)
    {
        var active = _featureTakeovers.LastOrDefault(takeover => takeover.RootElementId == rootElementId);
        if (active != null)
            return active.Lifecycle.MoveTo(location);

        if (rootElementId == NativeUi.Indicators && _barTakeover != null)
            return _barTakeover.MoveTo(location);

        using var lifecycle = new RetailTakeoverLifecycle(new NativeElementPort(rootElementId), new MoveOnlySurface());
        lifecycle.Tick();
        return lifecycle.MoveTo(location);
    }

    private void ReleaseFeatureWindow(string id)
    {
        if (!_featureSurfaces.TryGetValue(id, out var surface)) return;
        if (_hovered == surface) _hovered = null;
        surface.Visible = false;
        surface.Dispose();
        _featureSurfaces.Remove(id);
        _windows?.Close(id);
    }

    private void ReleaseFeatureTakeover(FeatureTakeover registration)
    {
        if (!_featureTakeovers.Contains(registration)) return;
        registration.Lifecycle.Dispose();
        if (_hovered == registration.Surface) _hovered = null;
        registration.Surface?.Dispose();
        _featureTakeovers.Remove(registration);
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
                    _pointer = new Point(route.X + target!.Location.X, route.Y + target.Location.Y);
                    if (route.SurfaceId != null && _windows?.Get(route.SurfaceId) != null)
                    {
                        _windows.Press(_pointer);
                        SyncWindowLocations();
                    }
                    target.Panel.PointerDown(route.X, route.Y);
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
            if (route.Eat)
                e.Eat = true;
        });
    }

    private void GuardInput(InputRoute route, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            var id = route.SurfaceId ?? route.ClearFocusSurfaceId ?? route.ReleaseCaptureSurfaceId;
            var windowFailure = id != "bar" && (id != null || (_hovered != null && _hovered != _barSurface));
            if (windowFailure)
            {
                _postUiDrawHook?.Dispose();
                _postUiDrawHook = null;
                DisableWindows(exception);
            }
            else
                Disable(exception);
        }
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
        foreach (var takeover in _featureTakeovers)
        {
            var surface = takeover.Surface;
            if (surface is not { Visible: true }) continue;
            surfaces.Add(new InputSurface(takeover.InputId, surface.Location.X, surface.Location.Y,
                surface.Bounds.Width, surface.Bounds.Height, 0, surface.Panel.WantsKeyboard));
        }
        return surfaces.ToArray();
    }

    private ScreenSurface? SurfaceById(string? id)
    {
        if (id == "bar") return _barSurface;
        if (id != null && _featureSurfaces.TryGetValue(id, out var surface)) return surface;
        return _featureTakeovers.FirstOrDefault(takeover => takeover.InputId == id)?.Surface;
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
        foreach (var window in _windows.ZOrder)
            SurfaceById(window.Id)!.Location = window.Location;
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

    private void GuardWindows(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _postUiDrawHook?.Dispose();
            _postUiDrawHook = null;
            DisableWindows(exception);
        }
    }

    /// <summary>Stop drawing, give the player the retail bar back, and log why.</summary>
    private void Disable(Exception exception)
    {
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
        foreach (var slot in new[] { InputTestSlot, ThemeGallerySlot, BreakoutSlot, "Live data", "Element inspector" })
            _bar?.SetOpen(slot, false);
        foreach (var window in _windows?.ZOrder.ToArray() ?? Array.Empty<ManagedWindow>())
            _windows!.Close(window.Id);
        try { _clientUi?.EndSession(); }
        catch (Exception cleanupError) { Log($"Could not clean up feature UI after window failure: {cleanupError}"); }
        _clientUi = null;
    }

    private static string SessionCharacter() => CoreManager.Current.CharacterFilter.Name;
    private static string SessionServer() => CoreManager.Current.CharacterFilter.Server;

    private void PublishGameState()
    {
        var filter = CoreManager.Current.CharacterFilter;
        _gameState.Publish(new GameStateSnapshot(SessionCharacter(), SessionServer(),
            filter.Health, filter.EffectiveVital[Decal.Adapter.Wrappers.CharFilterVitalType.Health],
            filter.Stamina, filter.EffectiveVital[Decal.Adapter.Wrappers.CharFilterVitalType.Stamina],
            filter.Mana, filter.EffectiveVital[Decal.Adapter.Wrappers.CharFilterVitalType.Mana]));
    }

    private sealed class NativeElementPort : IRetailTakeoverPort
    {
        private readonly uint _id;
        public NativeElementPort(uint id) => _id = id;
        private IntPtr Element => NativeUi.GetElement(_id);
        public bool Exists => Element != IntPtr.Zero;
        public IntPtr ElementIdentity => Element;
        public bool IsVisible { get { var element = Element; return element != IntPtr.Zero && NativeUi.IsVisible(element); } }
        public Rectangle GetBounds() { var element = Element; return element == IntPtr.Zero ? Rectangle.Empty : NativeUi.GetBounds(element); }
        public void SetVisible(bool visible) { var element = Element; if (element != IntPtr.Zero) NativeUi.SetVisible(element, visible); }
        public void SetSaveLocation(bool save) { var element = Element; if (element != IntPtr.Zero) NativeUi.SetSaveLocation(element, save); }
        public void MoveTo(Point location) { var element = Element; if (element != IntPtr.Zero) NativeUi.MoveTo(element, location); }
        public bool IsUiLocked => NativeUi.IsUiLocked;
    }

    private sealed class FeatureWindow : IDisposable
    {
        private readonly ClientUiRuntime _owner;
        private readonly string _id;
        public FeatureWindow(ClientUiRuntime owner, string id) { _owner = owner; _id = id; }
        public void Dispose() => _owner.ReleaseFeatureWindow(_id);
    }

    private sealed class FeatureTakeover : IDisposable
    {
        private readonly ClientUiRuntime _owner;
        public FeatureTakeover(ClientUiRuntime owner, uint rootElementId, RetailTakeoverLifecycle lifecycle, ScreenSurface? surface)
        {
            _owner = owner; RootElementId = rootElementId; Lifecycle = lifecycle; Surface = surface;
            if (surface != null) Renderer = new RetailSurfaceRenderer(surface.Prepare, surface.DrawNow);
        }
        public uint RootElementId { get; }
        public RetailTakeoverLifecycle Lifecycle { get; }
        public ScreenSurface? Surface { get; }
        public RetailSurfaceRenderer? Renderer { get; }
        public string InputId => "retail-root-" + RootElementId;
        public void Dispose() => _owner.ReleaseFeatureTakeover(this);
    }

    private sealed class MoveOnlySurface : IRetailTakeoverSurface
    {
        public Point Location { get; private set; }
        public Size Size => Size.Empty;
        public bool Visible { get; set; }
        public void SetLocation(Point location) => Location = location;
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
        try { _clientUi?.EndSession(); }
        catch (Exception exception) { Log($"Could not clean up feature UI during unload: {exception}"); }
        _clientUi = null;
        RestoreNativeBar();
        _hovered = null;
        _barSurface?.Dispose();
        _barSurface = null;
        _barRenderer = null;
        _postUiDrawHook?.Dispose();
        _postUiDrawHook = null;
        _windows = null;
        foreach (var surface in _featureSurfaces.Values) surface.Dispose();
        _featureSurfaces.Clear();
        foreach (var takeover in _featureTakeovers.ToArray())
        {
            try { takeover.Dispose(); }
            catch (Exception exception) { Log($"Could not restore an inspected retail root: {exception}"); }
        }
        _windowsEnabled = false;
        _portal?.Dispose();
        _portal = null;
        _bar = null;
        _breakout = null;
        GameArtImageExtension.CurrentSource = null;
    }
}
