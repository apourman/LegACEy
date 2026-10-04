using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using DrawingColor = System.Drawing.Color;
using IOPath = System.IO.Path;
using Decal.Adapter;
using LegACEy.Client.PanelHost;
using VirindiViewService;

namespace LegACEy.Client.DecalPlugin;

[FriendlyName("LegACEy Avalonia Panel")]
public sealed class Plugin : FilterBase
{
    private const int PanelWidth = 360;
    private const int PanelHeight = 260;
    private static readonly string PluginDirectory = IOPath.GetDirectoryName(typeof(Plugin).Assembly.Location)!;

    private readonly Stopwatch _frameClock = new();
    private BreakoutGame? _game;
    private AvaloniaPanel? _panel;
    private HudView? _view;
    private AvaloniaHudControl? _control;
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
        Guard(() =>
        {
            _panel = AvaloniaPanel.Create(() => _game = new BreakoutGame(PanelWidth, PanelHeight), PanelWidth, PanelHeight);
            CoreManager.Current.CharacterFilter.LoginComplete += OnLoginComplete;
            CoreManager.Current.CharacterFilter.Logoff += OnLogoff;
            CoreManager.Current.RenderFrame += OnRenderFrame;
        });
    }

    /// <summary>Create the VVS window, with its sidebar icon, once a character is in the world.</summary>
    private void OnLoginComplete(object? sender, EventArgs e)
    {
        if (_failed || _panel == null || _view != null)
            return;

        Guard(() =>
        {
            _view = new HudView("LegACEy Breakout", PanelWidth, PanelHeight, new ACImage(DrawingColor.FromArgb(0x32, 0x75, 0x8d)))
            {
                UserResizeable = false
            };
            _control = new AvaloniaHudControl(_panel);
            _control.PointerMoved += point => Guard(() => _game?.PointAt(point.X));
            _control.PointerPressed += _ => Guard(() => _game?.Click());
            _view.Controls.HeadControl = _control;
        });
    }

    private void OnLogoff(object? sender, EventArgs e)
    {
        _view?.Dispose();
        _view = null;
        _control = null;
    }

    /// <summary>
    /// Step the game and tick Avalonia on the game's render thread while the window is open, and
    /// redraw the control only when its pixels changed. The game pauses while the window is closed.
    /// </summary>
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (_failed || _panel == null || _view is not { Visible: true })
        {
            _frameClock.Reset();
            return;
        }

        Guard(() =>
        {
            _game?.Step(_frameClock.Elapsed);
            _frameClock.Restart();
            if (_panel.Tick())
                _control?.Invalidate();
        });
    }

    /// <summary>Run panel work from a game callback; any exception disables the panel instead of escaping.</summary>
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            DisablePanel(exception);
        }
    }

    private void DisablePanel(Exception exception)
    {
        _failed = true;
        TearDown();
        Log($"Panel disabled: {exception}");
    }

    private void TearDown()
    {
        _view?.Dispose();
        _view = null;
        _control = null;
        _panel?.Dispose();
        _panel = null;
    }
}
