using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
    private const string InputTestSlot = "Input test";
    private const string ThemeGallerySlot = "Theme gallery";
    private static readonly string PluginDirectory = IOPath.GetDirectoryName(typeof(Plugin).Assembly.Location)!;

    private Device? _device;
    private PortalDat? _portal;
    private IndicatorBar? _bar;
    private InputTestPanel? _inputTest;
    private ThemeGalleryControl? _themeGallery;
    private ScreenSurface? _barSurface;
    private ScreenSurface? _inputTestSurface;
    private ScreenSurface? _themeGallerySurface;
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
            _inGame = true;
        });
    }

    private void OnLogoff(object? sender, EventArgs e)
    {
        Guard(() => ApplyReset(_inputRouter.Route(new NativeInputMessage(InputRouterService.WmLogoff, IntPtr.Zero, IntPtr.Zero), GetInputSurfaces())));
        Guard(() => _hovered?.Panel.PointerLeave());
        _inGame = false;
        _nativeBarBounds = null;
        _hovered = null;
        _dragOffset = null;
        if (_barSurface != null) _barSurface.Visible = false;
        if (_inputTestSurface != null) _inputTestSurface.Visible = false;
        if (_themeGallerySurface != null) _themeGallerySurface.Visible = false;
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
                if (_inputTestSurface != null) _inputTestSurface.Visible = false;
                _bar?.SetOpen(InputTestSlot, false);
                if (_hovered == _inputTestSurface) _hovered = null;
            };
            return chrome;
        }, InputWindowWidth, InputWindowHeight);
        _inputTestSurface = new ScreenSurface(_device, inputPanel);
        var galleryPanel = AvaloniaPanel.Create(() =>
        {
            _themeGallery = new ThemeGalleryControl();
            _themeGallery.ThemeSwitchRequested += (_, _) => SwitchTheme();
            var chrome = new ThemeWindowChrome(_portal, "Theme gallery", _themeGallery);
            chrome.CloseRequested += (_, _) =>
            {
                if (_themeGallerySurface != null) _themeGallerySurface.Visible = false;
                _bar?.SetOpen(ThemeGallerySlot, false);
                if (_hovered == _themeGallerySurface) _hovered = null;
            };
            return chrome;
        }, 580, 560);
        _themeGallerySurface = new ScreenSurface(_device, galleryPanel);
        ApplyCurrentTheme();
        Log("Indicator bar replacement ready.");
    }

    private void ToggleThemeGallery()
    {
        if (_themeGallerySurface == null) return;
        _themeGallerySurface.Visible = !_themeGallerySurface.Visible;
        _bar?.SetOpen(ThemeGallerySlot, _themeGallerySurface.Visible);
        if (_themeGallerySurface.Visible)
            PlaceThemeGallery();
    }

    private void PlaceThemeGallery()
    {
        var screen = _device!.Viewport;
        var size = _themeGallerySurface!.Bounds.Size;
        _themeGallerySurface.Location = new Point(
            Math.Max(0, Math.Min((screen.Width - size.Width) / 2, screen.Width - size.Width)),
            Math.Max(0, Math.Min((screen.Height - size.Height) / 2, screen.Height - size.Height)));
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
        if (_inputTestSurface == null || _barSurface == null)
            return;

        _inputTestSurface.Visible = !_inputTestSurface.Visible;
        _bar?.SetOpen(InputTestSlot, _inputTestSurface.Visible);
        if (_inputTestSurface.Visible)
            PlaceInputTest();
        else if (_hovered == _inputTestSurface)
            _hovered = null;
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
        if (_inputTestSurface is { Visible: true })
            PlaceInputTest();
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

    /// <summary>Open the input test panel just below the bar, or above it when there's no room below.</summary>
    private void PlaceInputTest()
    {
        var bar = _barSurface!.Bounds;
        var screen = _device!.Viewport;
        var x = Math.Max(0, Math.Min(bar.Left, screen.Width - InputWindowWidth));
        var y = bar.Bottom + 4 + InputWindowHeight <= screen.Height ? bar.Bottom + 4 : Math.Max(0, bar.Top - 4 - InputWindowHeight);
        _inputTestSurface!.Location = new Point(x, y);
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

            if (_inputTestSurface is { Visible: true })
                _inputTestSurface.Render();
            if (_themeGallerySurface is { Visible: true })
                _themeGallerySurface.Render();
        });
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
        if (_inputTestSurface is { Visible: true })
            PlaceInputTest();
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
        Guard(() =>
        {
            var target = route.SurfaceId == null ? null : SurfaceById(route.SurfaceId);
            switch (route.Action)
            {
                case InputAction.PointerMove:
                    var point = new Point((short)(e.LParam & 0xffff), (short)((e.LParam >> 16) & 0xffff));
                    _pointer = point;
                    if (target != _hovered)
                    {
                        _hovered?.Panel.PointerLeave();
                        _hovered = target;
                    }
                    if (_dragOffset != null)
                        DragBar(point);
                    else
                        target?.Panel.PointerMove(route.X, route.Y);
                    break;
                case InputAction.PointerDown:
                    _pointer = new Point(route.X + target!.Location.X, route.Y + target.Location.Y);
                    target.Panel.PointerDown(route.X, route.Y);
                    e.Eat = route.Eat;
                    break;
                case InputAction.PointerUp:
                    target!.Panel.PointerUp(route.X, route.Y);
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

    private InputSurface[] GetInputSurfaces()
    {
        var surfaces = new System.Collections.Generic.List<InputSurface>(2);
        if (_barSurface is { Visible: true })
            surfaces.Add(new InputSurface("bar", _barSurface.Location.X, _barSurface.Location.Y, _barSurface.Bounds.Width, _barSurface.Bounds.Height, 0, _barSurface.Panel.WantsKeyboard));
        if (_inputTestSurface is { Visible: true })
            surfaces.Add(new InputSurface("input-test", _inputTestSurface.Location.X, _inputTestSurface.Location.Y, _inputTestSurface.Bounds.Width, _inputTestSurface.Bounds.Height, 1, _inputTestSurface.Panel.WantsKeyboard));
        if (_themeGallerySurface is { Visible: true })
            surfaces.Add(new InputSurface("theme-gallery", _themeGallerySurface.Location.X, _themeGallerySurface.Location.Y, _themeGallerySurface.Bounds.Width, _themeGallerySurface.Bounds.Height, 2, _themeGallerySurface.Panel.WantsKeyboard));
        return surfaces.ToArray();
    }

    private ScreenSurface? SurfaceById(string? id) => id switch
    {
        "bar" => _barSurface,
        "input-test" => _inputTestSurface,
        "theme-gallery" => _themeGallerySurface,
        _ => null
    };

    private void ApplyReset(InputRoute route)
    {
        SurfaceById(route.ReleaseCaptureSurfaceId)?.Panel.PointerUp(-1, -1);
        SurfaceById(route.ClearFocusSurfaceId)?.Panel.ClearFocus();
        _hovered?.Panel.PointerLeave();
        _hovered = null;
        _dragOffset = null;
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
        _failed = true;
        Log($"Indicator bar replacement disabled: {exception}");
        RestoreNativeBar();
        TearDown();
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
        _portal?.Dispose();
        _portal = null;
        _bar = null;
        _inputTest = null;
        _themeGallery = null;
        GameArtImageExtension.CurrentSource = null;
    }
}
