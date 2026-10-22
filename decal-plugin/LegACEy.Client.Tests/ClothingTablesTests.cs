using System.Diagnostics;
using System.Linq;
using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class ClothingTablesTests
{
    private const uint HumanMale = 0x02000001;
    private const uint KneeBoots = 0x10000007; // draws the shins (2, 6) as well as the feet

    [Fact]
    public void Real_dat_boots_named_for_the_feet_are_found_on_the_shins_too_and_the_trousers_under_them_are_not()
    {
        var path = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var dat = new PortalDat(path);

        var tables = ClothingTables.Read(dat, HumanMale);
        var effects = tables.Effects;
        var boots = effects.Single(effect => effect.Table == KneeBoots);
        // Trousers: the abdomen and both whole legs, no feet.
        var trousers = effects.First(effect => new[] { 0, 1, 2, 5, 6 }.All(effect.Parts.ContainsKey) && !effect.Parts.ContainsKey(3)
            && !effect.Parts.ContainsKey(9) && effect.Parts.Values.Any(part => part.Textures.Count > 0));
        var baseParts = DatModels.ReadSetup(dat, HumanMale)!.Parts;
        // The look ACE sends: the trousers, then the boots over them.
        var look = Wearing(trousers, boots);

        // Claims in the order the look draws the items: the trousers, then the boots.
        var drawn = tables.PartsDrawn(look, new[] { new[] { 0, 1, 2, 5, 6 }, new[] { 3, 4, 7, 8 } });

        Assert.Equal(new[] { 2, 3, 4, 6, 7, 8 }, drawn[1]);
        // The trousers keep the parts they change themselves (some tables list a part only to leave it bare) that the boots don't cover.
        Assert.Equal(trousers.Parts.Where(part => part.Value.Model != baseParts[part.Key] || part.Value.Textures.Any(t => t.NewTexture != t.OldTexture))
            .Select(part => part.Key).Except(boots.Parts.Keys).OrderBy(part => part), drawn[0]);
        Assert.Contains(1, drawn[0]);
    }

    [Fact]
    public void Real_dat_a_coat_over_a_shirt_worn_on_the_same_parts_draws_them_and_the_shirt_draws_nothing()
    {
        var path = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var dat = new PortalDat(path);

        var tables = ClothingTables.Read(dat, HumanMale);
        var shirt = tables.Effects.Single(effect => effect.Table == 0x100000FD); // Baggy Shirt
        var coat = tables.Effects.Single(effect => effect.Table == 0x10000286);  // Academy Coat
        var look = Wearing(shirt, coat);
        // The shirt is worn on the abdomen too, so it claims all the coat does.
        int[] shirtClaim = [0, 9, 10, 11, 13, 14], coatClaim = [0, 9, 10, 11, 13, 14];

        var drawn = tables.PartsDrawn(look, [shirtClaim, coatClaim]);

        Assert.Empty(drawn[0]);
        Assert.Equal(coatClaim, drawn[1]);
    }

    [Fact]
    public void Real_dat_boots_over_a_shirt_draw_the_legs_and_feet_and_leave_the_shirts_sleeves_to_it()
    {
        var path = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var dat = new PortalDat(path);

        var tables = ClothingTables.Read(dat, HumanMale);
        var shirt = tables.Effects.Single(effect => effect.Table == 0x100000FD); // Baggy Shirt
        var boots = tables.Effects.Single(effect => effect.Table == KneeBoots);  // Leather Boots
        var look = Wearing(shirt, boots);

        var drawn = tables.PartsDrawn(look, [[0, 9, 10, 11, 13, 14], [2, 3, 4, 6, 7, 8]]);

        Assert.Equal(new[] { 2, 3, 4, 6, 7, 8 }, drawn[1]);
        Assert.Contains(11, drawn[0]);
    }

    private static CharacterAppearance Wearing(params ClothingEffect[] items) => new(HumanMale, 0, Array.Empty<SubPalette>(),
        items.SelectMany(item => item.Parts.Values.SelectMany(part => part.Textures)).ToList(),
        items.SelectMany(item => item.Parts.Select(part => new PartChange(part.Key, part.Value.Model))).ToList());
}
