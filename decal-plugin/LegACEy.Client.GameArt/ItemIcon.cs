using LegACEy.GameArt;

namespace LegACEy.Client.GameArt;

/// <summary>Item icons drawn the way retail draws them, from the portal DAT.</summary>
public static class ItemIcon
{
    /// <summary>
    /// The item's icon as one image: the underlay, then the icon with its overlay and UI-effect outline, then the secondary overlay.
    /// Layers missing from the DAT are left out; null when the icon itself isn't in the DAT.
    /// </summary>
    public static GameImage? Draw(IGameArtSource art, uint underlay, uint icon, uint overlay, uint overlaySecondary, uint uiEffects)
    {
        var image = art.ReadImage(icon);
        if (image == null) return null;

        var pixels = ItemIconOutline.Compose(image.Width, image.Height, image.Pixels,
            art.ReadImage(overlay)?.Pixels, art.ReadImage(ItemIconOutline.TextureFor(uiEffects))?.Pixels);

        var below = art.ReadImage(underlay);
        if (below != null && below.Pixels.Length == pixels.Length) pixels = ItemIconOutline.Over(pixels, below.Pixels);
        var above = art.ReadImage(overlaySecondary);
        if (above != null && above.Pixels.Length == pixels.Length) pixels = ItemIconOutline.Over(above.Pixels, pixels);

        return new GameImage(image.Width, image.Height, pixels);
    }
}
