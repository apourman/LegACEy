using System.Collections.Generic;

namespace LegACEy.Client.GameArt;

/// <summary>Item icons drawn the way retail draws them, from the portal DAT.</summary>
public static class ItemIcon
{
    /// <summary>The icon with its overlay, its outline taken from the UI effect. Null when the icon isn't in the DAT.</summary>
    public static GameImage? Compose(IGameArtSource art, uint icon, uint overlay, uint uiEffects)
    {
        var image = art.ReadImage(icon);
        if (image == null) return null;
        var pixels = ItemIconOutline.Compose(image.Width, image.Height, image.Pixels,
            art.ReadImage(overlay)?.Pixels, art.ReadImage(ItemIconOutline.TextureFor(uiEffects))?.Pixels);
        return new GameImage(image.Width, image.Height, pixels);
    }

    /// <summary>An item's layers bottom to top: underlay, the composed icon, then the secondary overlay. Missing layers are left out.</summary>
    public static IReadOnlyList<GameImage> Layers(IGameArtSource art, uint underlay, uint icon, uint overlay, uint overlaySecondary, uint uiEffects)
    {
        var layers = new List<GameImage>(3);
        var below = art.ReadImage(underlay);
        if (below != null) layers.Add(below);
        var composed = Compose(art, icon, overlay, uiEffects);
        if (composed != null) layers.Add(composed);
        var above = art.ReadImage(overlaySecondary);
        if (above != null) layers.Add(above);
        return layers;
    }
}
