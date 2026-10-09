#nullable enable

// Shared with Source/ACE.MarketApi (linked there, as the Market's tests link MarketTestDatabase.cs): keep it to plain byte arrays.
namespace LegACEy.Client.GameArt;

/// <summary>
/// The retail item icon outline rule (research: .research/client-avalonia-ui/item-icon-outlines.md). The icon's white outline is a key colour:
/// every opaque white pixel takes the same pixel of the item's UI-effect outline texture, or black when it has no effect.
/// Pixels are width × height × 4 bytes in one channel order; the icon and overlay are premultiplied, as the client's images are.
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

    /// <summary>The outline texture for an item's UiEffects: the lowest effect's, or <see cref="Black"/> when it has none of the twelve</summary>
    public static uint TextureFor(uint uiEffects)
    {
        for (var bit = 0; bit < outlineByBit.Length; bit++)
            if ((uiEffects & (1u << bit)) != 0) return outlineByBit[bit];
        return Black;
    }

    /// <summary>
    /// The icon with its overlay composited over it (premultiplied over), then its white outline replaced from <paramref name="outline"/>.
    /// A null or mismatched overlay is left out; a null or mismatched outline is black.
    /// </summary>
    public static byte[] Compose(int width, int height, byte[] icon, byte[]? overlay, byte[]? outline)
    {
        var length = width * height * 4;
        var pixels = (byte[])icon.Clone();

        if (overlay != null && overlay.Length == length)
            for (var i = 0; i < length; i += 4)
            {
                var alpha = overlay[i + 3];
                for (var c = 0; c < 4; c++)
                    pixels[i + c] = (byte)(overlay[i + c] + pixels[i + c] * (255 - alpha) / 255);
            }

        var key = outline != null && outline.Length == length ? outline : null;
        for (var i = 0; i < length; i += 4)
        {
            if (pixels[i] != 0xFF || pixels[i + 1] != 0xFF || pixels[i + 2] != 0xFF || pixels[i + 3] != 0xFF) continue;
            for (var c = 0; c < 3; c++)
                pixels[i + c] = key == null ? (byte)0 : key[i + c];
        }

        return pixels;
    }
}
