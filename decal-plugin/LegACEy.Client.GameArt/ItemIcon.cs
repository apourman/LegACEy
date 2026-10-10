using LegACEy.GameArt;

namespace LegACEy.Client.GameArt;

/// <summary>Item icons drawn the way retail draws them, from the portal DAT.</summary>
public static class ItemIcon
{
    /// <summary>
    /// The item's icon as one image: the item-type plate, the underlay, then the icon with its overlay and UI-effect outline, then the
    /// secondary overlay. Layers missing from the DAT are left out; null when the icon itself isn't in the DAT.
    /// </summary>
    public static GameImage? Draw(IGameArtSource art, uint underlay, uint icon, uint overlay, uint overlaySecondary, uint uiEffects, uint plate = 0)
    {
        var image = art.ReadImage(icon);
        if (image == null) return null;

        var pixels = ItemIconOutline.Compose(image.Width, image.Height, image.Pixels,
            art.ReadImage(overlay)?.Pixels, art.ReadImage(ItemIconOutline.TextureFor(uiEffects))?.Pixels);

        var below = art.ReadImage(underlay);
        if (below != null && below.Pixels.Length == pixels.Length) pixels = ItemIconOutline.Over(pixels, below.Pixels);
        var back = art.ReadImage(plate);
        if (back != null && back.Pixels.Length == pixels.Length) pixels = ItemIconOutline.Over(pixels, back.Pixels);
        var above = art.ReadImage(overlaySecondary);
        if (above != null && above.Pixels.Length == pixels.Length) pixels = ItemIconOutline.Over(above.Pixels, pixels);

        return new GameImage(image.Width, image.Height, pixels);
    }

    /// <summary>The plate retail draws behind an item's cell, by item type (the server's VaultChannelActions.Plate).</summary>
    public static uint PlateFor(uint itemType)
    {
        if ((itemType & (0x1u | 0x100u | 0x8000u)) != 0) return 0x060011D2; // MeleeWeapon, MissileWeapon, Caster
        if ((itemType & 0x2u) != 0) return 0x060011CF;                      // Armor
        if ((itemType & 0x4u) != 0) return 0x060011F3;                      // Clothing
        if ((itemType & 0x8u) != 0) return 0x060011D5;                      // Jewelry
        if ((itemType & 0x800u) != 0) return 0x060011D3;                    // Gem
        return 0x060011D4;
    }
}
