using LegACEy.GameArt;

namespace LegACEy.Client.GameArt;

/// <summary>
/// Spell icons drawn the way retail draws them (ClientMagicSystem::CompositeSpellIcon): the backdrop for the scarab's power, the spell's icon
/// with its outline in the colour of a reversed or plain spell's frame, then the mark of a fellowship or self-targeted spell. The art is the portal's UISpellBackgrounds
/// (DID mapper 0x2500000A) and UISpellOverlays (0x2500000B).
/// </summary>
public static class SpellIcon
{
    // UISpellBackgrounds by scarab power: lead, iron, copper, silver, gold, pyreal, diamond, platinum, dark, mana; the default after.
    private static readonly uint[] Backgrounds =
    {
        0x060011C5, 0x060013F4, 0x060013F5, 0x060013F6, 0x060013F7, 0x060013F8, 0x060013F9, 0x060013F6, 0x06001F63, 0x060013F6, 0x060067A6
    };
    private const uint ReversedFrame = 0x06004C3E, PlainFrame = 0x06004C3F, SelfMark = 0x060013F3, FellowshipMark = 0x060030D7;
    // ACE's SpellFlags.
    private const uint SelfTargeted = 0x8, Reversed = 0x10, FellowshipSpell = 0x2000;

    /// <summary>The spell's icon as one image; null when the icon itself is not in the DAT. Layers missing from the DAT are left out.</summary>
    public static GameImage? Draw(IGameArtSource art, SpellInfo spell)
    {
        var icon = art.ReadImage(spell.Icon);
        if (icon == null) return null;
        var length = icon.Pixels.Length;
        GameImage? Layer(uint id) => art.ReadImage(id) is { } layer && layer.Pixels.Length == length ? layer : null;
        // The frame colours the icon's white outline, as an item's UI effect does: red for a reversed spell, black for the rest.
        var frame = Layer((spell.Bitfield & Reversed) != 0 ? ReversedFrame : PlainFrame);
        var pixels = ItemIconOutline.Compose(icon.Width, icon.Height, icon.Pixels, null, frame?.Pixels);
        var power = SpellFormula.Power(spell);
        if (Layer(Backgrounds[power < Backgrounds.Length ? power : 0]) is { } back) pixels = ItemIconOutline.Over(pixels, back.Pixels);
        var mark = (spell.Bitfield & FellowshipSpell) != 0 ? FellowshipMark : (spell.Bitfield & SelfTargeted) != 0 ? SelfMark : 0;
        if (mark != 0 && Layer(mark) is { } above) pixels = ItemIconOutline.Over(above.Pixels, pixels);
        return new GameImage(icon.Width, icon.Height, pixels);
    }
}
