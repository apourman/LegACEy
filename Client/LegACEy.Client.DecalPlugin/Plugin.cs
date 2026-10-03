using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaColor = Avalonia.Media.Color;
using AvaloniaBrushes = Avalonia.Media.Brushes;
using Bitmap = System.Drawing.Bitmap;
using IOPath = System.IO.Path;
using Decal.Adapter;
using ImGuiNET;
using LegACEy.Client.PanelHost;
using Microsoft.DirectX.Direct3D;
using UtilityBelt.Service;
using UtilityBelt.Service.Views;

namespace LegACEy.Client.DecalPlugin;

[FriendlyName("LegACEy Avalonia Panel")]
public sealed class Plugin : FilterBase
{
    private const int PanelWidth = 360;
    private const int PanelHeight = 180;
    private static readonly string PluginDirectory = IOPath.GetDirectoryName(typeof(Plugin).Assembly.Location)!;

    private Hud? _hud;
    private AvaloniaPanel? _panel;
    private ManagedTexture? _texture;
    private bool _failed;

    protected override void Startup()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginDirectory;
        CoreManager.Current.FilterInitComplete += OnFilterInitComplete;
    }

    protected override void Shutdown()
    {
        CoreManager.Current.FilterInitComplete -= OnFilterInitComplete;
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

            // UtilityBelt releases and recreates managed textures from this bitmap around device
            // resets; the next render uploads the current frame again.
            using (var blank = new Bitmap(PanelWidth, PanelHeight))
                _texture = new ManagedTexture(blank);

            _hud = UBService.Huds.CreateHud("LegACEy Avalonia");
            _hud.Title = "LegACEy Avalonia";
            _hud.OnRender += OnRender;
        });
    }

    private static Control CreateDemoControl() => new Border
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
                        Text = "Rendered with Avalonia and CPU Skia",
                        Foreground = AvaloniaBrushes.White
                    }
                },
                new Button
                {
                    Content = "Demo button",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
                }
            }
        }
    };

    private void OnRender(object? sender, EventArgs e)
    {
        if (_failed || _panel == null || _texture?.Texture == null)
            return;

        Guard(() =>
        {
            _panel.Tick();
            Upload(_panel.Frame, _texture.Texture);
            ImGui.Image(_texture.TexturePtr, new Vector2(PanelWidth, PanelHeight));
        });
    }

    /// <summary>Copy the whole BGRA frame into the dynamic A8R8G8B8 texture, honouring its pitch.</summary>
    private static void Upload(PanelFrame frame, Texture texture)
    {
        var level = texture.GetLevelDescription(0);
        if (level.Width != frame.Width || level.Height != frame.Height || level.Format != Format.A8R8G8B8)
            throw new InvalidOperationException(
                $"The panel frame ({frame.Width}x{frame.Height} BGRA) does not match the texture ({level.Width}x{level.Height} {level.Format}).");

        var bits = texture.LockRectangle(0, LockFlags.Discard, out var pitch);
        try
        {
            if (bits.InternalData == IntPtr.Zero || pitch < frame.Stride)
                throw new InvalidOperationException($"The dynamic D3D texture returned an invalid lock (pitch {pitch}, row {frame.Stride}).");

            for (var row = 0; row < frame.Height; row++)
                Marshal.Copy(frame.Pixels, row * frame.Stride, IntPtr.Add(bits.InternalData, row * pitch), frame.Stride);
        }
        finally
        {
            texture.UnlockRectangle(0);
        }
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
        try
        {
            UBService.LogException(exception);
        }
        catch
        {
            // Logging must not let a panel failure escape into the client render callback.
        }
    }

    private void TearDown()
    {
        if (_hud != null)
        {
            _hud.OnRender -= OnRender;
            _hud.Dispose();
            _hud = null;
        }

        _texture?.Dispose();
        _texture = null;
        _panel?.Dispose();
        _panel = null;
    }
}
