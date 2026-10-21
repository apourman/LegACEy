using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>Saves a panel's latest frame as a PNG, for a person to look at in the harness renders.</summary>
internal static class PanelPng
{
    public static void Save(AvaloniaPanel panel, string path)
    {
        var frame = panel.Frame;
        using var bitmap = new WriteableBitmap(new Avalonia.PixelSize(frame.Width, frame.Height), new Avalonia.Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = bitmap.Lock())
        {
            Assert.Equal(frame.Stride, locked.RowBytes);
            Marshal.Copy(frame.Pixels, 0, locked.Address, frame.Pixels.Length);
        }
        bitmap.Save(path);
    }
}
