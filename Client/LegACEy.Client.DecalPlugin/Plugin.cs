using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Input;
using IOPath = System.IO.Path;
using Decal.Adapter;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.InputRouter;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Microsoft.DirectX.Direct3D;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Proof of concept: replace the retail floating indicators bar with an Avalonia bar drawn
/// straight onto the game's device. It keeps the retail buttons and adds an input test slot.
/// </summary>
[FriendlyName("LegACEy Avalonia Panel")]
public sealed class Plugin : FilterBase
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
    private static readonly string PluginDirectory = IOPath.GetDirectoryName(typeof(Plugin).Assembly.Location)!;

    private Device? _device;
    private PortalDat? _portal;
    private IndicatorBar? _bar;
    private InputTestPanel? _inputTest;
    private ThemeGalleryControl? _themeGallery;
    private BreakoutGame? _breakout;
    private ScreenSurface? _barSurface;
    private ScreenSurface? _inputTestSurface;
    private ScreenSurface? _themeGallerySurface;
    private ScreenSurface? _breakoutSurface;
    private WindowManager? _windows;
    private PostUiDrawHook? _postUiDrawHook;
    private IntPtr _nativeDevice;
    private bool _windowsEnabled;
    private DateTime _lastBreakoutStep = DateTime.UtcNow;
    private ScreenSurface? _hovered;
    private readonly InputRouterService _inputRouter = new();
    private Point _pointer;
    private Size? _dragOffset;
    private Rectangle? _nativeBarBounds;
    private bool _inGame;
    private bool _failed;
    private bool _acThemeActive = true;

    protected override void Startup()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginDirectory;
        CoreManager.Current.FilterInitComplete += OnFilterInitComplete;
    }

    protected override void Shutdown()
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

    /// <summary>Build the bar and input test panel the first time a character is in the world.</summary>
    private void OnLoginComplete(object? sender, EventArgs e)
    {
        if (_failed)
            return;

        Guard(() =>
        {
            if (_barSurface == null)
                CreateUi();
            else if (_windows == null)
                CreateWindowManager();
            _inGame = true;
        });
    }

    private void OnLogoff(object? sender, EventArgs e)
    {
        Guard(() => ApplyReset(_inputRouter.Route(new NativeInputMessage(InputRouterService.WmLogoff, IntPtr.Zero, IntPtr.Zero), GetInputSurfaces())));
        Guard(() => _hovered?.Panel.PointerLeave());
        _inGame = false;
        foreach (var window in _windows?.ZOrder.ToArray() ?? Array.Empty<ManagedWindow>())
            _windows!.Close(window.Id);
        _windows = null;
        _nativeBarBounds = null;
        _hovered = null;
        _dragOffset = null;
        if (_barSurface != null) _barSurface.Visible = false;
        if (_inputTestSurface != null) _inputTestSurface.Visible = false;
        if (_themeGallerySurface != null) _themeGallerySurface.Visible = false;
        if (_breakoutSurface != null) _breakoutSurface.Visible = false;
        _bar?.SetOpen(InputTestSlot, false);
        _bar?.SetOpen(ThemeGallerySlot, false);
        _bar?.SetOpen(BreakoutSlot, false);
    }

    private void CreateUi()
    {
        _device = GameDevice.Open(out _nativeDevice);
        CreateWindowManager();
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
            new IndicatorSlot("Log out", 0x060074B1, NativeUi.RequestLogOut)
        };
        var size = IndicatorBar.MeasureFor(slots.Length);
        var barPanel = AvaloniaPanel.Create(() => _bar = new IndicatorBar(slots, _portal.ReadImage), size.Width, size.Height);
        _barSurface = new ScreenSurface(_device, barPanel);

        var inputPanel = AvaloniaPanel.Create(() =>
        {
            _inputTest = new InputTestPanel(InputTestWidth, InputTestHeight);
            var chrome = new ThemeWindowChrome(_portal, "Input test", _inputTest);
            chrome.CloseRequested += (_, _) =>
            {
                CloseWindow("input-test");
            };
            return chrome;
        }, InputWindowWidth, InputWindowHeight);
        _inputTestSurface = new ScreenSurface(_device, inputPanel);
        var galleryPanel = AvaloniaPanel.Create(() =>
        {
            _themeGallery = new ThemeGalleryControl();
            _themeGallery.ThemeSwitchRequested += (_, _) => SwitchTheme();
            var chrome = new ThemeWindowChrome(_portal, "Theme gallery", _themeGallery);
            chrome.CloseRequested += (_, _) => CloseWindow("theme-gallery");
            return chrome;
        }, 580, 560);
        _themeGallerySurface = new ScreenSurface(_device, galleryPanel);
        var breakoutPanel = AvaloniaPanel.Create(() =>
        {
            _breakout = new BreakoutGame(BreakoutWindowWidth - 16, BreakoutWindowHeight - 42);
            var chrome = new ThemeWindowChrome(_portal, "Breakout", _breakout);
            chrome.CloseRequested += (_, _) => CloseWindow("breakout");
            return chrome;
        }, BreakoutWindowWidth, BreakoutWindowHeight);
        _breakoutSurface = new ScreenSurface(_device, breakoutPanel);
        ApplyCurrentTheme();
        Log("Indicator bar replacement ready.");
    }

    private void CreateWindowManager()
    {
        if (_device == null) return;
        _windows = new WindowManager(new Size(_device.Viewport.Width, _device.Viewport.Height), new FileWindowPositionStore(IOPath.Combine(PluginDirectory, "window-positions.txt")), SessionServer(), SessionCharacter());
        _postUiDrawHook ??= new PostUiDrawHook(DrawWindowsAfterRetailUi, DisableWindows);
        _windowsEnabled = _postUiDrawHook.Install(_nativeDevice);
        if (!_windowsEnabled)
            Log("LegACEy windows disabled: IDirect3DDevice9.EndScene hook could not be installed.");
    }

    private void ToggleThemeGallery()
    {
        ToggleWindow("theme-gallery", ThemeGallerySlot, 580, 560, new Point(120, 70));
    }

    private void SwitchTheme()
    {
        _acThemeActive = !_acThemeActive;
        ApplyCurrentTheme();
    }

    private void ApplyCurrentTheme()
    {
        if (_barSurface == null || _inputTestSurface == null || _themeGallerySurface == null || _portal == null) return;
        IClientTheme theme = _acThemeActive ? new AcClientTheme(_portal) : new SimpleClientTheme();
        _barSurface.Panel.ApplyTheme(theme);
        _inputTestSurface.Panel.ApplyTheme(theme);
        _themeGallerySurface.Panel.ApplyTheme(theme);
    }

    private void ToggleInputTest()
    {
        ToggleWindow("input-test", InputTestSlot, InputWindowWidth, InputWindowHeight, new Point(120, 70));
    }

    private void ToggleBreakout() => ToggleWindow("breakout", BreakoutSlot, BreakoutWindowWidth, BreakoutWindowHeight, new Point(180, 80));

    private void ToggleWindow(string id, string slot, int width, int height, Point defaultLocation)
    {
        if (!_windowsEnabled || _windows == null)
            return;
        var surface = SurfaceById(id);
        if (surface == null)
            return;
        if (surface.Visible)
        {
            CloseWindow(id);
            return;
        }

        var window = _windows.Open(new WindowDefinition(id, id, width, height), defaultLocation);
        surface.Location = window.Location;
        surface.Visible = true;
        _bar?.SetOpen(slot, true);
    }

    private void CloseWindow(string id)
    {
        var surface = SurfaceById(id);
        if (surface != null) surface.Visible = false;
        _windows?.Close(id);
        if (_hovered == surface) _hovered = null;
        if (id == "input-test") _bar?.SetOpen(InputTestSlot, false);
        if (id == "theme-gallery") _bar?.SetOpen(ThemeGallerySlot, false);
        if (id == "breakout") _bar?.SetOpen(BreakoutSlot, false);
    }

    /// <summary>The handle was pressed: the bar follows the pointer until the button goes up.</summary>
    private void BeginBarDrag()
    {
        var location = _barSurface!.Location;
        _dragOffset = new Size(_pointer.X - location.X, _pointer.Y - location.Y);
    }

    private void DragBar(Point pointer)
    {
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
        var native = NativeUi.GetElement(NativeUi.Indicators);
        if (native == IntPtr.Zero)
            return;

        NativeUi.MoveTo(native, _barSurface!.Location);
        _nativeBarBounds = NativeUi.GetBounds(native);
        Log($"Moved the retail indicators bar to {_nativeBarBounds}.");
    }

    /// <summary>
    /// Each frame in game: take over the retail bar if the client is showing it, then draw our
    /// surfaces.
    /// </summary>
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (_failed || !_inGame || _barSurface == null)
            return;

        Guard(() =>
        {
            TakeOverNativeBar();
            _barSurface.Render();
        });

        if (_failed || !_windowsEnabled)
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

            PrepareWindow(_inputTestSurface);
            PrepareWindow(_themeGallerySurface);
            PrepareWindow(_breakoutSurface);
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
    private void DrawWindowsAfterRetailUi()
    {
        if (!_windowsEnabled || _windows == null)
            return;
        foreach (var window in _windows.ZOrder.Reverse())
            SurfaceById(window.Id)?.DrawNow();
    }

    /// <summary>
    /// Hide the retail indicators bar whenever the client shows it, and put ours where it was.
    /// Checking every frame also catches the client showing it again, for example after a resize.
    /// </summary>
    private void TakeOverNativeBar()
    {
        var native = NativeUi.GetElement(NativeUi.Indicators);
        if (native == IntPtr.Zero || !NativeUi.IsVisible(native))
            return;

        var bounds = NativeUi.GetBounds(native);
        NativeUi.SetVisible(native, false);
        if (_nativeBarBounds != bounds)
            Log($"Replaced the retail indicators bar at {bounds}.");
        _nativeBarBounds = bounds;
        _barSurface!.Location = bounds.Location;
        _barSurface.Visible = true;
    }

    private void RestoreNativeBar()
    {
        if (!_inGame || _nativeBarBounds == null)
            return;

        try
        {
            var native = NativeUi.GetElement(NativeUi.Indicators);
            if (native != IntPtr.Zero)
                NativeUi.SetVisible(native, true);
        }
        catch (Exception exception)
        {
            Log($"Could not restore the retail indicators bar: {exception}");
        }
        _nativeBarBounds = null;
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
                    if (target != _barSurface && _windows != null)
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
        return surfaces.ToArray();
    }

    private ScreenSurface? SurfaceById(string? id) => id switch
    {
        "bar" => _barSurface,
        "input-test" => _inputTestSurface,
        "theme-gallery" => _themeGallerySurface,
        "breakout" => _breakoutSurface,
        _ => null
    };

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
        foreach (var surface in new[] { _inputTestSurface, _themeGallerySurface, _breakoutSurface })
            if (surface != null) surface.Visible = false;
        _bar?.SetOpen(InputTestSlot, false);
        _bar?.SetOpen(ThemeGallerySlot, false);
        _bar?.SetOpen(BreakoutSlot, false);
        foreach (var window in _windows?.ZOrder.ToArray() ?? Array.Empty<ManagedWindow>())
            _windows!.Close(window.Id);
    }

    private static string SessionCharacter()
    {
        var filter = CoreManager.Current.CharacterFilter;
        return PropertyText(filter, "Character") is { } character
            ? PropertyText(character, "Name") ?? character
            : PropertyText(filter, "Name") ?? "unknown-character";
    }

    private static string SessionServer()
    {
        var filter = CoreManager.Current.CharacterFilter;
        var server = PropertyText(filter, "Server");
        return server ?? "unknown-server";
    }

    private static string? PropertyText(object target, string property)
    {
        try
        {
            var value = target.GetType().GetProperty(property)?.GetValue(target, null);
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void TearDown()
    {
        _hovered = null;
        _barSurface?.Dispose();
        _barSurface = null;
        _inputTestSurface?.Dispose();
        _inputTestSurface = null;
        _themeGallerySurface?.Dispose();
        _themeGallerySurface = null;
        _breakoutSurface?.Dispose();
        _breakoutSurface = null;
        _postUiDrawHook?.Dispose();
        _postUiDrawHook = null;
        _windows = null;
        _windowsEnabled = false;
        _portal?.Dispose();
        _portal = null;
        _bar = null;
        _inputTest = null;
        _themeGallery = null;
        _breakout = null;
        GameArtImageExtension.CurrentSource = null;
    }
}
