#nullable enable
using System;

// Shared by the client (decal-plugin/LegACEy.Client.GameArt links it) and the Market API (ACE.MarketApi links it). Plain byte arrays only,
// so it builds for netstandard2.0 and net10.
namespace LegACEy.GameArt;

/// <summary>
/// The retail item icon outline rule (research: .research/client-avalonia-ui/item-icon-outlines.md). The icon's white outline is a key colour:
/// every opaque white pixel takes the same pixel of the item's UI-effect outline texture, or opaque black when it has no effect.
/// Pixels are width × height × 4 bytes in one channel order. Icons, overlays and outlines are premultiplied, as the client's images are.
/// </summary>
public static class ItemIconOutline
{
    /// <summary>Solid black: the outline of an item with no UI effect, and of anything that isn't an item. A null texture also means black.</summary>
    public const uint Black = 0x060011C5;

    // the outline by the lowest set UiEffects bit (Magical first); BoostMana repeats Magical's texture, as the game's map does
    private static readonly uint[] outlineByBit =
    {
        0x060011CA, 0x060011C6, 0x06001B05, 0x060011CA, 0x06001B06, 0x06001B2E,
        0x06001B2D, 0x06001B2F, 0x06001B2C, 0x060033C3, 0x060033C2, 0x060033C4,
    };

    /// <summary>The outline texture for a UiEffects value: the lowest effect's, or <see cref="Black"/> when it has none of the twelve</summary>
    public static uint TextureFor(uint uiEffects)
    {
        for (var bit = 0; bit < outlineByBit.Length; bit++)
            if ((uiEffects & (1u << bit)) != 0) return outlineByBit[bit];
        return Black;
    }

    /// <summary>The one effect the Market API names an outline by: the lowest of the twelve bits, or 0 when there is none</summary>
    public static uint LowestEffect(uint uiEffects) => uiEffects & (0u - uiEffects) & 0xFFF;

    /// <summary>Straight RGBA to premultiplied</summary>
    public static byte[] Premultiply(byte[] rgba)
    {
        var pixels = (byte[])rgba.Clone();
        for (var i = 0; i < pixels.Length; i += 4)
            for (var c = 0; c < 3; c++)
                pixels[i + c] = (byte)(pixels[i + c] * pixels[i + 3] / 255);
        return pixels;
    }

    /// <summary>The premultiplied <paramref name="top"/> composited over <paramref name="bottom"/>, both the same size</summary>
    public static byte[] Over(byte[] top, byte[] bottom)
    {
        var pixels = (byte[])bottom.Clone();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = top[i + 3];
            for (var c = 0; c < 4; c++)
                pixels[i + c] = (byte)(top[i + c] + bottom[i + c] * (255 - alpha) / 255);
        }
        return pixels;
    }

    /// <summary>
    /// The icon with its overlay composited over it, then its white outline replaced from <paramref name="outline"/>. A null or mismatched
    /// overlay is left out; a null or mismatched outline is black.
    /// </summary>
    public static byte[] Compose(int width, int height, byte[] icon, byte[]? overlay, byte[]? outline)
    {
        var length = width * height * 4;
        var pixels = overlay != null && overlay.Length == length ? Over(overlay, icon) : (byte[])icon.Clone();
        var key = outline != null && outline.Length == length ? outline : null;

        for (var i = 0; i < length; i += 4)
        {
            if (pixels[i] != 0xFF || pixels[i + 1] != 0xFF || pixels[i + 2] != 0xFF || pixels[i + 3] != 0xFF) continue;
            // the whole pixel is copied, as retail's ReplaceColor copies it; black is opaque
            if (key != null)
                Array.Copy(key, i, pixels, i, 4);
            else
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
            }
        }

        return pixels;
    }
}
