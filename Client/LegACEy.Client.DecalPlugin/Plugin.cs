using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using IOPath = System.IO.Path;
using Decal.Adapter;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using Microsoft.DirectX.Direct3D;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Proof of concept: replace the retail floating indicators bar with an Avalonia bar drawn
/// straight onto the game's device. It keeps the retail buttons and adds a Breakout slot.
/// </summary>
[FriendlyName("LegACEy Avalonia Panel")]
public sealed class Plugin : FilterBase
{
    private const int BreakoutWidth = 360;
    private const int BreakoutHeight = 260;
    private const string BreakoutSlot = "Breakout";
    private static readonly string PluginDirectory = IOPath.GetDirectoryName(typeof(Plugin).Assembly.Location)!;

    private readonly Stopwatch _frameClock = new();
    private Device? _device;
    private PortalDat? _portal;
    private IndicatorBar? _bar;
    private BreakoutGame? _game;
    private ScreenSurface? _barSurface;
    private ScreenSurface? _breakoutSurface;
    private ScreenSurface? _hovered;
    private ScreenSurface? _captured;
    private Rectangle? _nativeBarBounds;
    private bool _inGame;
    private bool _failed;

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

    /// <summary>Build the bar and the Breakout panel the first time a character is in the world.</summary>
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
        _inGame = false;
        _nativeBarBounds = null;
        _hovered = null;
        _captured = null;
        if (_barSurface != null) _barSurface.Visible = false;
        if (_breakoutSurface != null) _breakoutSurface.Visible = false;
    }

    private void CreateUi()
    {
        _device = GameDevice.Open();
        var acclient = Process.GetCurrentProcess().MainModule!.FileName;
        _portal = new PortalDat(IOPath.Combine(IOPath.GetDirectoryName(acclient)!, "client_portal.dat"));

        var slots = new[]
        {
            new IndicatorSlot("Link status", 0x06007498, () => NativeUi.ToggleRootElement(NativeUi.LinkStatus)),
            new IndicatorSlot("Positive effects", 0x0600749C, () => NativeUi.ToggleRootElement(NativeUi.PositiveEffects)),
            new IndicatorSlot("Negative effects", 0x0600749F, () => NativeUi.ToggleRootElement(NativeUi.NegativeEffects)),
            new IndicatorSlot("Vitae", 0x060074A1, () => NativeUi.ToggleRootElement(NativeUi.Vitae)),
            new IndicatorSlot("Character info", 0x060074A2, () => NativeUi.ToggleRootElement(NativeUi.CharacterInfo)),
            new IndicatorSlot("Mini-game", 0x060074A6, () => NativeUi.ToggleRootElement(NativeUi.MiniGame)),
            new IndicatorSlot(BreakoutSlot, 0x06004D20, ToggleBreakout, "B"),
            // UIElementManager::DoVisibilityToggleAction(ClientAction.LOGOUT), which Chorizite uses, did
            // nothing in game, so log out the way /logout does, through Decal.
            new IndicatorSlot("Log out", 0x060074B1, () => CoreManager.Current.Actions.Logout())
        };
        var size = IndicatorBar.MeasureFor(slots.Length);
        var barPanel = AvaloniaPanel.Create(() => _bar = new IndicatorBar(slots, _portal.ReadImage), size.Width, size.Height);
        _barSurface = new ScreenSurface(_device, barPanel);

        var breakoutPanel = AvaloniaPanel.Create(() => _game = new BreakoutGame(BreakoutWidth, BreakoutHeight), BreakoutWidth, BreakoutHeight);
        _breakoutSurface = new ScreenSurface(_device, breakoutPanel);
        Log("Indicator bar replacement ready.");
    }

    private void ToggleBreakout()
    {
        if (_breakoutSurface == null || _barSurface == null)
            return;

        _breakoutSurface.Visible = !_breakoutSurface.Visible;
        _bar?.SetOpen(BreakoutSlot, _breakoutSurface.Visible);
        if (_breakoutSurface.Visible)
            PlaceBreakout();
        else if (_hovered == _breakoutSurface)
            _hovered = null;
    }

    /// <summary>Open Breakout just below the bar, or above it when there's no room below.</summary>
    private void PlaceBreakout()
    {
        var bar = _barSurface!.Bounds;
        var screen = _device!.Viewport;
        var x = Math.Max(0, Math.Min(bar.Left, screen.Width - BreakoutWidth));
        var y = bar.Bottom + 4 + BreakoutHeight <= screen.Height ? bar.Bottom + 4 : Math.Max(0, bar.Top - 4 - BreakoutHeight);
        _breakoutSurface!.Location = new Point(x, y);
    }

    /// <summary>
    /// Each frame in game: take over the retail bar if the client is showing it, then draw our
    /// surfaces. Breakout steps by real elapsed time and pauses while closed.
    /// </summary>
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (_failed || !_inGame || _barSurface == null)
            return;

        Guard(() =>
        {
            TakeOverNativeBar();
            _barSurface.Render();

            if (_breakoutSurface is { Visible: true })
            {
                _game?.Step(_frameClock.Elapsed);
                _frameClock.Restart();
                _breakoutSurface.Render();
            }
            else
            {
                _frameClock.Reset();
            }
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
        if (_breakoutSurface is { Visible: true })
            PlaceBreakout();
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

    /// <summary>
    /// Route the left mouse button and pointer moves to our surfaces. A press on a surface is eaten
    /// so the world behind it doesn't get the click, and the matching release goes to the same surface.
    /// </summary>
    private void OnWindowMessage(object? sender, WindowMessageEventArgs e)
    {
        if (_failed || !_inGame || _barSurface == null)
            return;

        const short MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, LeftDoubleClick = 0x0203, RightDown = 0x0204, RightUp = 0x0205;
        if (e.Msg < MouseMove || e.Msg > RightUp)
            return;

        var point = new Point((short)(e.LParam & 0xFFFF), (short)((e.LParam >> 16) & 0xFFFF));
        Guard(() =>
        {
            var target = _captured ?? SurfaceAt(point);
            switch (e.Msg)
            {
                case MouseMove:
                    if (target != _hovered)
                    {
                        _hovered?.Panel.PointerLeave();
                        _hovered = target;
                    }
                    target?.Panel.PointerMove(point.X - target.Location.X, point.Y - target.Location.Y);
                    break;
                case LeftDown:
                case LeftDoubleClick:
                    if (target == null) return;
                    _captured = target;
                    target.Panel.PointerDown(point.X - target.Location.X, point.Y - target.Location.Y);
                    e.Eat = true;
                    break;
                case LeftUp:
                    if (_captured == null) return;
                    _captured = null;
                    target!.Panel.PointerUp(point.X - target.Location.X, point.Y - target.Location.Y);
                    e.Eat = true;
                    break;
                case RightDown:
                case RightUp:
                    if (target != null) e.Eat = true;
                    break;
            }
        });
    }

    private ScreenSurface? SurfaceAt(Point point)
    {
        if (_breakoutSurface is { Visible: true } && _breakoutSurface.Bounds.Contains(point))
            return _breakoutSurface;
        if (_barSurface is { Visible: true } && _barSurface.Bounds.Contains(point))
            return _barSurface;
        return null;
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
        _captured = null;
        _barSurface?.Dispose();
        _barSurface = null;
        _breakoutSurface?.Dispose();
        _breakoutSurface = null;
        _portal?.Dispose();
        _portal = null;
        _bar = null;
        _game = null;
    }
}
