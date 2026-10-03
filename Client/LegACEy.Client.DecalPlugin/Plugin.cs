using System;
using System.Numerics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaColor = Avalonia.Media.Color;
using AvaloniaBrushes = Avalonia.Media.Brushes;
using Bitmap = System.Drawing.Bitmap;
using Decal.Adapter;
using ImGuiNET;
using LegACEy.Client.PanelHost;
using UtilityBelt.Service;
using UtilityBelt.Service.Views;

namespace LegACEy.Client.DecalPlugin;

[FriendlyName("LegACEy Avalonia Panel")]
public sealed class Plugin : FilterBase
{
    private const int PanelWidth = 360;
    private const int PanelHeight = 180;

    private Hud? _hud;
    private AvaloniaPanel? _panel;
    private object? _texture;
    private bool _failed;

    protected override void Startup()
    {
        CoreManager.Current.FilterInitComplete += OnFilterInitComplete;
    }

    protected override void Shutdown()
    {
        CoreManager.Current.FilterInitComplete -= OnFilterInitComplete;
        TearDown();
    }

    private void OnFilterInitComplete(object? sender, EventArgs e)
    {
        CoreManager.Current.FilterInitComplete -= OnFilterInitComplete;
        try
        {
            AvaloniaPanel.EnsureRuntimeInitialized();
            _panel = AvaloniaPanel.Create(CreateDemoControl(), PanelWidth, PanelHeight);
            _hud = UBService.Huds.CreateHud("LegACEy Avalonia");
            _hud.Title = "LegACEy Avalonia";
            _hud.OnRender += OnRender;
            _hud.OnDestroyTextures += OnDestroyTextures;
            _hud.OnCreateTextures += OnCreateTextures;
            CreateTexture();
        }
        catch (Exception exception)
        {
            DisablePanel(exception);
        }
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
        if (_failed || _panel == null || _texture == null)
            return;

        try
        {
            _panel.Tick(TimeSpan.FromMilliseconds(16));
            UploadFrame(_panel.Frame);
            var texturePointer = (IntPtr)_texture.GetType().GetProperty("TexturePtr")!.GetValue(_texture, null)!;
            ImGui.Image(texturePointer, new Vector2(PanelWidth, PanelHeight));
        }
        catch (Exception exception)
        {
            DisablePanel(exception);
        }
    }

    private void OnDestroyTextures(object? sender, EventArgs e) => DestroyTexture();

    private void OnCreateTextures(object? sender, EventArgs e)
    {
        if (_failed)
            return;

        try
        {
            CreateTexture();
        }
        catch (Exception exception)
        {
            DisablePanel(exception);
        }
    }

    private void CreateTexture()
    {
        DestroyTexture();
        using (var blank = new Bitmap(PanelWidth, PanelHeight))
        {
            var managedTextureType = typeof(UBService).Assembly.GetType("UtilityBelt.Service.Views.ManagedTexture", throwOnError: true)!;
            var constructor = managedTextureType.GetConstructor(new[] { typeof(Bitmap) })
                ?? throw new MissingMethodException(managedTextureType.FullName, ".ctor(Bitmap)");
            _texture = constructor.Invoke(new object[] { blank });
        }
        if (_panel != null)
            UploadFrame(_panel.Frame);
    }

    private void UploadFrame(PanelFrame frame)
    {
        if (_texture == null)
            return;

        var nativeTexture = _texture.GetType().GetProperty("Texture")!.GetValue(_texture, null);
        if (nativeTexture == null)
            return;
        var nativeType = nativeTexture.GetType();
        var lockMethod = Array.Find(nativeType.GetMethods(BindingFlags.Instance | BindingFlags.Public), method =>
        {
            if (method.Name != "LockRectangle") return false;
            var parameters = method.GetParameters();
            return parameters.Length == 3 &&
                parameters[0].ParameterType == typeof(int) &&
                parameters[1].ParameterType.IsEnum &&
                parameters[2].IsOut &&
                parameters[2].ParameterType == typeof(int).MakeByRefType();
        }) ?? throw new MissingMethodException(nativeType.FullName, "LockRectangle(int, LockFlags, out int)");
        var unlockMethod = nativeType.GetMethod("UnlockRectangle", new[] { typeof(int) })
            ?? throw new MissingMethodException(nativeType.FullName, "UnlockRectangle(int)");
        var flagsType = lockMethod.GetParameters()[1].ParameterType;
        var discard = Enum.Parse(flagsType, "Discard");
        var lockArguments = new object[] { 0, discard, 0 };
        var locked = lockMethod.Invoke(nativeTexture, lockArguments)
            ?? throw new InvalidOperationException("The dynamic D3D texture lock returned no locked region.");

        try
        {
            var bits = (IntPtr)locked.GetType().GetProperty("InternalData")!.GetValue(locked, null)!;
            var pitch = (int)lockArguments[2];
            var rowBytes = checked(frame.Width * 4);
            if (bits == IntPtr.Zero || pitch < rowBytes)
                throw new InvalidOperationException($"The dynamic D3D texture returned an invalid lock (pitch {pitch}, row {rowBytes}).");

            for (var row = 0; row < frame.Height; row++)
                System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, row * frame.Stride, IntPtr.Add(bits, row * pitch), rowBytes);
        }
        finally
        {
            unlockMethod.Invoke(nativeTexture, new object[] { 0 });
        }
    }

    private void DestroyTexture()
    {
        (_texture as IDisposable)?.Dispose();
        _texture = null;
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
            _hud.OnDestroyTextures -= OnDestroyTextures;
            _hud.OnCreateTextures -= OnCreateTextures;
            _hud.Dispose();
            _hud = null;
        }

        DestroyTexture();
        _panel?.Dispose();
        _panel = null;
    }
}
