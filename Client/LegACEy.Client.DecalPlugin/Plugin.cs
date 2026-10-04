using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaColor = Avalonia.Media.Color;
using AvaloniaBrushes = Avalonia.Media.Brushes;
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
    private const int PanelHeight = 220;
    private static readonly string PluginDirectory = IOPath.GetDirectoryName(typeof(Plugin).Assembly.Location)!;

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
            _panel = AvaloniaPanel.Create(CreateDemoControl, PanelWidth, PanelHeight);
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
            _view = new HudView("LegACEy Avalonia", PanelWidth, PanelHeight, new ACImage(DrawingColor.FromArgb(0x32, 0x75, 0x8d)))
            {
                UserResizeable = false
            };
            _control = new AvaloniaHudControl(_panel);
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
    /// Tick Avalonia on the game's render thread while the window is open, and redraw the control
    /// only when its pixels changed.
    /// </summary>
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (_failed || _panel == null || _view is not { Visible: true })
            return;

        Guard(() =>
        {
            if (_panel.Tick())
                _control?.Invalidate();
        });
    }

    private static Control CreateDemoControl()
    {
        var clock = new TextBlock { Foreground = AvaloniaBrushes.White, FontSize = 16 };
        var started = DateTime.Now;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => clock.Text = $"Live for {DateTime.Now - started:hh\\:mm\\:ss}";
        timer.Start();
        clock.Text = "Live for 00:00:00";

        return new Border
        {
            Background = new SolidColorBrush(AvaloniaColor.FromRgb(0x19, 0x27, 0x36)),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "LegACEy Avalonia",
                        FontSize = 22,
                        Foreground = AvaloniaBrushes.White
                    },
                    new Border
                    {
                        Background = new SolidColorBrush(AvaloniaColor.FromRgb(0x32, 0x75, 0x8d)),
                        Padding = new Thickness(10),
                        Child = new TextBlock
                        {
                            Text = "Hosted in Virindi View Service, no UtilityBelt",
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = AvaloniaBrushes.White
                        }
                    },
                    clock,
                    new Button
                    {
                        Content = "Demo button",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
                    }
                }
            }
        };
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
