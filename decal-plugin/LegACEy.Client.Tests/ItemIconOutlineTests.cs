using LegACEy.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

// The outline rule on synthetic pixels, so CI checks it without the DAT (ItemIconTests checks the same rule on real icons).
public sealed class ItemIconOutlineTests
{
    [Fact]
    public void White_takes_the_outline_pixel_whole_and_opaque_black_when_there_is_none()
    {
        var icon = new byte[] { 255, 255, 255, 255 };
        var outline = new byte[] { 10, 20, 30, 255 };

        Assert.Equal(outline, ItemIconOutline.Compose(1, 1, icon, null, outline));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, ItemIconOutline.Compose(1, 1, icon, null, null));
    }

    [Fact]
    public void A_semi_transparent_overlay_blends_premultiplied_over_the_icon()
    {
        var icon = new byte[] { 100, 100, 100, 255 };
        var overlay = new byte[] { 64, 0, 0, 128 }; // premultiplied: half-alpha red

        Assert.Equal(new byte[] { 113, 49, 49, 255 }, ItemIconOutline.Compose(1, 1, icon, overlay, null));
    }

    [Fact]
    public void The_lowest_effect_picks_the_outline_and_nether_is_black()
    {
        Assert.Equal(ItemIconOutline.Black, ItemIconOutline.TextureFor(0));
        Assert.Equal(ItemIconOutline.Black, ItemIconOutline.TextureFor(0x1000)); // Nether has no outline texture
        Assert.Equal(0x060011CAu, ItemIconOutline.TextureFor(0x8)); // BoostMana uses Magical's texture
        Assert.Equal(0x060011C6u, ItemIconOutline.TextureFor(0x42)); // Poisoned, the lower bit, over Lightning

        Assert.Equal(0x8u, ItemIconOutline.LowestEffect(0x8));
        Assert.Equal(0x2u, ItemIconOutline.LowestEffect(0x42));
        Assert.Equal(0u, ItemIconOutline.LowestEffect(0x1000));
    }
}
