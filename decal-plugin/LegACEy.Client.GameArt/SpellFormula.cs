using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.GameArt;

/// <summary>
/// The spell formula rules the client and server share (ACE.DatLoader's SpellTable and ACE.Server's SpellFormula, which follow the client's
/// SpellFormula::RandomizeVersion1–3 and CSpellBase::InqScarabOnlyFormula): the string hash, the tapers each account casts with, the
/// scarab-only formula a focus allows, and the spell level the spellbook filters by.
/// </summary>
public static class SpellFormula
{
    /// <summary>The lowest taper's id in the component table (Red Taper); the twelve tapers follow it.</summary>
    public const uint LowestTaper = 63;
    /// <summary>The prismatic taper, which stands in for the other components in a scarab-only formula.</summary>
    public const uint PrismaticTaper = 188;
    private const uint Chorizite = 111;

    // Each scarab's spell level and power: lead, iron, copper, silver, gold, pyreal, diamond, platinum, dark, mana.
    private static readonly Dictionary<uint, (uint Level, uint Power)> Scarabs = new()
    {
        [1] = (1, 1), [2] = (2, 2), [3] = (3, 3), [4] = (4, 4), [5] = (5, 5), [6] = (6, 6),
        [110] = (6, 7), [112] = (7, 8), [192] = (7, 9), [193] = (8, 10)
    };

    /// <summary>The client's string hash over Windows-1252 bytes, each read as a signed byte.</summary>
    public static uint Hash(IEnumerable<byte> text)
    {
        long result = 0;
        foreach (var b in text)
        {
            result = (sbyte)b + (result << 4);
            if ((result & 0xF0000000) != 0) result = (result ^ ((result & 0xF0000000) >> 24)) & 0x0FFFFFFF;
        }
        return (uint)result;
    }

    public static uint Hash(string text) => Hash(text.Select(Windows1252.ToByte));

    public static bool IsScarab(uint component) => Scarabs.ContainsKey(component);

    /// <summary>
    /// The spell's level as the client's spellbook filters read it: the level of the scarab that starts the formula, or 0 when it starts with
    /// something else.
    /// </summary>
    public static uint Level(SpellInfo spell) =>
        spell.Formula.Count > 0 && Scarabs.TryGetValue(spell.Formula[0], out var scarab) ? scarab.Level : 0;

    /// <summary>The power of the scarab that starts the formula (1 lead to 10 mana), or 0 when it starts with something else.</summary>
    public static uint Power(SpellInfo spell) =>
        spell.Formula.Count > 0 && Scarabs.TryGetValue(spell.Formula[0], out var scarab) ? scarab.Power : 0;

    /// <summary>The formula the account casts the spell with: the table's, with its tapers chosen from the account name's hash.</summary>
    public static IReadOnlyList<uint> ForAccount(SpellInfo spell, string account)
    {
        var comps = spell.Formula.ToList();
        if (account.Length == 0) return comps;
        try
        {
            switch (spell.FormulaVersion)
            {
                case 1: Version1(comps, Hash(account)); break;
                case 2: Version2(comps, Hash(account)); break;
                case 3: Version3(comps, Hash(account)); break;
            }
        }
        // A short or odd formula (some have fewer than eight components) keeps the table's components, as nothing else can be told.
        catch (System.ArgumentOutOfRangeException) { return spell.Formula.ToList(); }
        catch (System.DivideByZeroException) { return spell.Formula.ToList(); }
        return comps;
    }

    /// <summary>The formula a focus or an Infused augmentation allows: every scarab (and chorizite), then prismatic tapers by the scarab's power.</summary>
    public static IReadOnlyList<uint> ScarabOnly(SpellInfo spell)
    {
        var comps = spell.Formula.Where(c => IsScarab(c) || c == Chorizite).ToList();
        var tapers = Power(spell) switch { 1 => 1, 2 => 2, 3 or 4 or 7 => 3, 5 or 6 or 8 or 9 or 10 => 4, _ => 0 };
        for (var i = 0; i < tapers; i++) comps.Add(PrismaticTaper);
        return comps;
    }

    private static void Version1(List<uint> comps, uint key)
    {
        var seed = key % 0x13D573;
        var scarab = comps[0];
        var herbIndex = comps.Count > 5 ? 2 : 1;
        var herb = comps[herbIndex];
        var powderIndex = herbIndex + 1 + (comps.Count > 6 ? 1 : 0);
        var powder = comps[powderIndex];
        var potion = comps[powderIndex + 1];
        var talismanIndex = powderIndex + 2 + (comps.Count > 7 ? 1 : 0);
        var talisman = comps[talismanIndex];
        if (comps.Count > 5) comps[1] = (powder + 2 * herb + potion + talisman + scarab) % 0xC + LowestTaper;
        if (comps.Count > 6) comps[3] = (scarab + herb + talisman + 2 * (powder + potion)) * (seed / (scarab + (powder + potion))) % 0xC + LowestTaper;
        if (comps.Count > 7) comps[6] = (powder + 2 * talisman + potion + herb + scarab) * (seed / (talisman + scarab)) % 0xC + LowestTaper;
    }

    private static void Version2(List<uint> comps, uint key)
    {
        var seed = key % 0x13D573;
        var p1 = comps[0];
        var c = comps[4];
        var x = comps[5];
        var a = comps[7];
        comps[3] = (a + 2 * comps[0] + 2 * c * x + comps[0] + comps[2] + comps[1]) % 0xC + LowestTaper;
        comps[6] = (a + 2 * p1 * comps[2] + 2 * x + p1 * comps[2] + c) * (seed / (comps[1] * a + 2 * c)) % 0xC + LowestTaper;
    }

    private static void Version3(List<uint> comps, uint key)
    {
        var hash0 = (key % 0x13D573 + comps[0]) % 0xC;
        var hash1 = (key % 0x4AEFD + comps[1]) % 0xC;
        var hash2 = (key % 0x96A7F + comps[2]) % 0xC;
        var hash4 = (key % 0x100A03 + comps[4]) % 0xC;
        var hash5 = (key % 0xEB2EF + comps[5]) % 0xC;
        // Some spells have fewer than eight components (2697, Aerfalle's Touch); the eighth then counts as 0.
        var hash7 = (key % 0x121E7D + (comps.Count < 8 ? 0 : comps[7])) % 0xC;
        comps[3] = (hash0 + hash1 + hash2 + hash4 + hash5 + hash2 * hash5 + hash0 * hash1 + hash7 * (hash4 + 1)) % 0xC + LowestTaper;
        comps[6] = (hash0 + hash1 + hash2 + hash4 + key % 0x65039 % 0xC + hash7 * (hash4 * (hash0 * hash1 * hash2 * hash5 + 7) + 1) + hash5
            + 4 * hash0 * hash1 + hash0 * hash1 + 11 * hash2 * hash5) % 0xC + LowestTaper;
    }
}
