using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class ItemIconTests
{
    // a plain icon with a white outline, and the Fire outline texture
    private const uint Icon = 0x06000FC7;
    private const uint FireOutline = 0x06001B2E;

    [Theory]
    [InlineData(0u)]
    [InlineData(0x20u)]
    public void Outline_pixels_come_from_the_ui_effect_texture_or_are_black(uint uiEffects)
    {
        var path = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (string.IsNullOrWhiteSpace(path)) return;

        using var dat = new PortalDat(path);
        var icon = dat.ReadImage(Icon)!;
        var composed = ItemIcon.Draw(dat, 0, Icon, 0, 0, uiEffects)!;
        var outline = uiEffects == 0 ? null : dat.ReadImage(FireOutline)!.Pixels;

        var outlinePixels = 0;
        for (var i = 0; i < icon.Pixels.Length; i += 4)
        {
            var white = icon.Pixels[i] == 0xFF && icon.Pixels[i + 1] == 0xFF && icon.Pixels[i + 2] == 0xFF && icon.Pixels[i + 3] == 0xFF;
            if (white) outlinePixels++;
            for (var c = 0; c < 3; c++)
                Assert.Equal(white ? (outline?[i + c] ?? 0) : icon.Pixels[i + c], composed.Pixels[i + c]);
            Assert.Equal(icon.Pixels[i + 3], composed.Pixels[i + 3]);
        }
        Assert.True(outlinePixels > 0, "the icon has no white outline to check");
    }
}
