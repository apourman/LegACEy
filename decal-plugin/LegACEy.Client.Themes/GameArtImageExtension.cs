using System;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Themes;

/// <summary>Loads a portal.dat image from XAML by its render-surface id.</summary>
public sealed class GameArtImageExtension : MarkupExtension
{
    public static IGameArtSource? CurrentSource { get; set; }
    public uint Id { get; set; }

    public GameArtImageExtension() { }
    public GameArtImageExtension(uint id) => Id = id;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CreateBitmap(CurrentSource, Id) ?? CreateFallbackBitmap();

    /// <summary>Build a nearest-neighbour Avalonia bitmap; null ids remain a supported case.</summary>
    public static WriteableBitmap? CreateBitmap(IGameArtSource? source, uint id) => CreateBitmap(source?.ReadImage(id));

    public static WriteableBitmap? CreateBitmap(GameImage? image)
    {
        if (image == null || image.Width <= 0 || image.Height <= 0 || image.Pixels.Length < image.Width * image.Height * 4)
            return null;

        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var locked = bitmap.Lock();
        for (var row = 0; row < image.Height; row++)
            System.Runtime.InteropServices.Marshal.Copy(image.Pixels, row * image.Width * 4, IntPtr.Add(locked.Address, row * locked.RowBytes), image.Width * 4);
        return bitmap;
    }

    private static WriteableBitmap CreateFallbackBitmap()
    {
        var bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var locked = bitmap.Lock();
        System.Runtime.InteropServices.Marshal.Copy(new byte[] { 0x30, 0x30, 0x38, 0xff }, 0, locked.Address, 4);
        return bitmap;
    }
}
