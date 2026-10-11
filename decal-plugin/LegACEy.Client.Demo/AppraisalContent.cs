using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>A figure in the summary: a label under a value, such as Value 75.</summary>
public sealed record AppraisalFigure(string Label, string Value);

/// <summary>How a value is coloured: raised by a buff or spell (green), lowered by a debuff (red), or as it is.</summary>
public enum AppraisalTone { Normal, Raised, Lowered }

/// <summary>
/// One line of a section. A line with a label is a label and a value; a line without one is text across the section. The tone colours the
/// value.
/// </summary>
public sealed record AppraisalRow(string? Label, string Text, AppraisalTone Tone = AppraisalTone.Normal);

/// <summary>A section below the summary: a heading and its rows. A section with no rows is never built.</summary>
public sealed record AppraisalSection(string Heading, IReadOnlyList<AppraisalRow> Rows);

/// <summary>
/// What the appraisal window shows for one object: the header, the summary figures, the sections and the long description. The window
/// draws this model and nothing else, so the rules that build it are tested without drawing. Built by <see cref="From"/>.
/// </summary>
public sealed record AppraisalContent(uint ObjectId, string Name, ItemVisual? Visual, string TypeLine, IReadOnlyList<AppraisalFigure> Figures,
    IReadOnlyList<AppraisalSection> Sections, string Description)
{
    // The server's property ids (ACE.Entity PropertyInt, PropertyString and PropertyDataId).
    private const uint ItemTypeInt = 1, EncumbranceValInt = 5, ValueInt = 19, WeaponSkillInt = 48, ItemWorkmanshipInt = 105, SalvagedItemsInt = 170;
    private const uint NameString = 1, UseString = 14, LongDescString = 16;
    private const uint IconDid = 8, IconOverlayDid = 50, IconUnderlayDid = 52;
    private const uint ArmorLevelInt = 28, WeaponTypeInt = 353, WeaponDefenseFloat = 29;
    // Values closer than this are the same figure (a bonus of 1.0 is none).
    private const float Epsilon = 0.0002f;

    // Highlight and colour bits as ACE.Server's WeaponMask and ArmorMask set them. A highlighted bit with its colour bit set is raised;
    // highlighted without it, lowered.
    private const uint WeaponAttackBit = 0x1, WeaponMeleeDefenseBit = 0x2, WeaponSpeedBit = 0x4, WeaponDamageBit = 0x8, WeaponDamageVarianceBit = 0x10;
    private const uint ArmorLevelBit = 0x1, ArmorSlashingBit = 0x2, ArmorPiercingBit = 0x4, ArmorBludgeoningBit = 0x8, ArmorFireBit = 0x20,
        ArmorColdBit = 0x10, ArmorAcidBit = 0x40, ArmorLightningBit = 0x80;

    /// <summary>The summary figures, in the order they sit side by side. A figure the server did not send is left out.</summary>
    private static readonly (string Label, Func<AppraisalResponse, string?> Text)[] FigureRules =
    {
        ("Value", response => Number(response, ValueInt)),
        ("Burden", response => Number(response, EncumbranceValInt)),
        ("Workmanship", WorkmanshipText),
    };

    /// <summary>The item-type bits and the name the type line shows for the first bit set.</summary>
    private static readonly (int Bit, string Name)[] ItemTypeNames =
    {
        (0x1, "Melee weapon"), (0x2, "Armor"), (0x4, "Clothing"), (0x8, "Jewelry"), (0x10, "Creature"), (0x20, "Food"), (0x80, "Misc"),
        (0x100, "Missile weapon"), (0x200, "Container"), (0x800, "Gem"), (0x2000, "Writable")
    };

    /// <summary>
    /// The sections below the summary, in the order the window shows them. A rule gives a heading and its rows; a section with no rows is
    /// left out. Later tickets add Spells, Requirements and Other between Armor and Use, in that order.
    /// </summary>
    private static readonly SectionRule[] SectionRules =
    {
        new("Combat", WeaponRows),
        new("Armor", ArmorRows),
        new("Use", response => TextRows(response.Strings, UseString)),
    };

    /// <summary>Builds what the window shows from one appraisal.</summary>
    public static AppraisalContent From(AppraisalResponse response)
    {
        var figures = FigureRules
            .Select(rule => (rule.Label, Text: rule.Text(response)))
            .Where(figure => figure.Text != null)
            .Select(figure => new AppraisalFigure(figure.Label, figure.Text!))
            .ToList();
        var sections = SectionRules
            .Select(rule => new AppraisalSection(rule.Heading, rule.Rows(response).ToList()))
            .Where(section => section.Rows.Count > 0)
            .ToList();
        var visual = response.Dids.TryGetValue(IconDid, out var icon) && icon != 0
            ? new ItemVisual(icon, Did(response, IconUnderlayDid), Did(response, IconOverlayDid), 0)
            : null;
        return new AppraisalContent(
            response.ObjectId,
            response.Strings.TryGetValue(NameString, out var name) && name.Length > 0 ? name : "Unknown item",
            visual,
            TypeLineOf(response),
            figures,
            sections,
            response.Strings.TryGetValue(LongDescString, out var description) ? description : string.Empty);
    }

    /// <summary>A portal id the response sent, or zero when it did not.</summary>
    private static uint Did(AppraisalResponse response, uint property) => response.Dids.TryGetValue(property, out var id) ? id : 0;

    /// <summary>The figure a property holds, as text, or nothing when the server did not send it.</summary>
    private static string? Number(AppraisalResponse response, uint property) =>
        response.Ints.TryGetValue(property, out var value) ? value.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// Workmanship in retail's words: "Nearly flawless (6)". Items are banded by clamping to 0-10 and show the raw number. A salvaged item
    /// (the count in SalvagedItemsInt) shows the average workmanship per salvaged item, rounded to a band and written to two places.
    /// Mirrors ItemAppraisalTextFormatter in OpenAC (AcDream.App/UI/Layout), MIT. A workmanship of zero has no words, so no figure.
    /// </summary>
    private static string? WorkmanshipText(AppraisalResponse response)
    {
        if (!response.Ints.TryGetValue(ItemWorkmanshipInt, out var workmanship)) return null;
        if (response.Ints.TryGetValue(SalvagedItemsInt, out var salvaged) && salvaged > 0)
        {
            double average = (double)workmanship / salvaged;
            var adjective = WorkmanshipAdjective(Clamp((int)Math.Round(average), 0, 10));
            return adjective.Length == 0 ? null : $"{adjective} ({average.ToString("0.00", CultureInfo.InvariantCulture)})";
        }
        var words = WorkmanshipAdjective(Clamp(workmanship, 0, 10));
        return words.Length == 0 ? null : $"{words} ({workmanship.ToString(CultureInfo.InvariantCulture)})";
    }

    // Math.Clamp is not in netstandard2.0, which Demo targets.
    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private static string WorkmanshipAdjective(int workmanship) => workmanship switch
    {
        <= 0 => string.Empty,
        1 => "Poorly crafted",
        2 => "Well-crafted",
        3 => "Finely crafted",
        4 => "Exquisitely crafted",
        5 => "Magnificent",
        6 => "Nearly flawless",
        7 => "Flawless",
        8 => "Utterly flawless",
        9 => "Incomparable",
        _ => "Priceless",
    };

    /// <summary>
    /// The type line: the weapon's type word when the server sends one, else the item's type, then the weapon skill when it has one
    /// ("Staff · Heavy Weapons"). Skill names are ACE's Skill.ToSentence (ACE.Entity/Enum/Skill.cs), indexed by the skill's id.
    /// </summary>
    private static string TypeLineOf(AppraisalResponse response)
    {
        var weaponType = response.Ints.TryGetValue(WeaponTypeInt, out var kind) && kind > 0 && kind < WeaponTypeNames.Length ? WeaponTypeNames[kind] : null;
        var type = weaponType ?? (response.Ints.TryGetValue(ItemTypeInt, out var bits) ? ItemTypeNames.FirstOrDefault(item => (bits & item.Bit) != 0).Name : null);
        var skill = response.Ints.TryGetValue(WeaponSkillInt, out var id) && id > 0 && id < SkillNames.Length ? SkillNames[id] : null;
        return string.Join(" · ", new[] { type, skill }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>
    /// ACE's WeaponType names by its value (Unarmed 1 to Thrown 10), as OpenAC names them. Two-handed (11) and magic (12) have no word in
    /// either, so for them the type line falls back to the item type name.
    /// </summary>
    private static readonly string[] WeaponTypeNames =
    {
        "", "Unarmed", "Sword", "Axe", "Mace", "Spear", "Dagger", "Staff", "Bow", "Crossbow", "Thrown"
    };

    private static readonly string[] SkillNames =
    {
        "None", "Axe", "Bow", "Crossbow", "Dagger", "Mace", "Melee Defense", "Missile Defense", "Sling", "Spear", "Staff", "Sword",
        "Thrown Weapon", "Unarmed Combat", "Arcane Lore", "Magic Defense", "Mana Conversion", "Spellcraft", "Item Tinkering",
        "Assess Person", "Deception", "Healing", "Jump", "Lockpick", "Run", "Awareness", "Arms And Armor Repair", "Assess Creature",
        "Weapon Tinkering", "Armor Tinkering", "Magic Item Tinkering", "Creature Enchantment", "Item Enchantment", "Life Magic",
        "War Magic", "Leadership", "Loyalty", "Fletching", "Alchemy", "Cooking", "Salvaging", "Two Handed Combat", "Gearcraft",
        "Void Magic", "Heavy Weapons", "Light Weapons", "Finesse Weapons", "Missile Weapons", "Shield", "Dual Wield", "Recklessness",
        "Sneak Attack", "Dirty Fighting", "Challenge", "Summoning"
    };

    /// <summary>
    /// A weapon's Combat rows: damage and type, speed, and the attack and melee defence bonuses. Values are coloured from the masks the
    /// server sent. A weapon with no profile has none.
    /// </summary>
    private static IEnumerable<AppraisalRow> WeaponRows(AppraisalResponse response)
    {
        if (response.Weapon is not { } weapon) return Array.Empty<AppraisalRow>();
        var rows = new List<AppraisalRow>
        {
            new AppraisalRow("Damage", DamageText(weapon), DamageTone(response.WeaponMask)),
            new AppraisalRow("Speed", SpeedText(weapon.WeaponTime), ToneOf(response.WeaponMask, WeaponSpeedBit)),
        };
        if (Math.Abs(weapon.WeaponOffense - 1) > Epsilon)
            rows.Add(new AppraisalRow("Attack", (weapon.WeaponOffense - 1).ToString("+0%;-0%;0%", CultureInfo.InvariantCulture),
                ToneOf(response.WeaponMask, WeaponAttackBit)));
        if (response.Floats.TryGetValue(WeaponDefenseFloat, out var defence) && Math.Abs(defence - 1) > Epsilon)
            rows.Add(new AppraisalRow("Melee defense", (defence - 1).ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture),
                ToneOf(response.WeaponMask, WeaponMeleeDefenseBit)));
        return rows;
    }

    /// <summary>The damage range and type, "41.28 – 86 Bludgeoning", or one figure when the variance leaves no range.</summary>
    private static string DamageText(WeaponProfile weapon)
    {
        var high = FormatDamage(weapon.Damage);
        var low = (1 - weapon.DamageVariance) * weapon.Damage;
        var range = weapon.Damage - low > Epsilon ? $"{FormatDamage(low)} – {high}" : high;
        var type = DamageTypeName(weapon.DamageType);
        return type.Length == 0 ? range : $"{range} {type}";
    }

    // Two decimals at most, never an exponent: 41.28 and 86.
    private static string FormatDamage(double damage) => damage.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The damage type's name, by ACE's DamageType bit (Slash 1 to Nether 1024), named as retail's armour rows name it. Empty when unknown.</summary>
    private static string DamageTypeName(uint type) => type switch
    {
        1 => "Slashing",
        2 => "Piercing",
        4 => "Bludgeoning",
        8 => "Cold",
        16 => "Fire",
        32 => "Acid",
        64 => "Lightning",
        1024 => "Nether",
        _ => string.Empty,
    };

    /// <summary>The damage line takes the damage bit, and failing that the variance bit, as retail's damage line does.</summary>
    private static AppraisalTone DamageTone(HighlightMask? mask)
    {
        var tone = ToneOf(mask, WeaponDamageBit);
        return tone != AppraisalTone.Normal ? tone : ToneOf(mask, WeaponDamageVarianceBit);
    }

    /// <summary>Speed in retail's words and the server's number: "Very Fast (0)". The unknown speed is sent as the largest value.</summary>
    private static string SpeedText(uint time) => time == uint.MaxValue ? "Unknown" : $"{SpeedWords(time)} ({time})";

    private static string SpeedWords(uint time) => time switch
    {
        < 11u => "Very Fast",
        < 31u => "Fast",
        < 50u => "Average",
        < 80u => "Slow",
        _ => "Very Slow",
    };

    /// <summary>
    /// An armour piece's Armor rows: the armour level, then each protection in retail's words with its modifier ("Above Average (1.2)").
    /// Nether shows only on a piece that protects against it. A piece with no armour level has none.
    /// </summary>
    private static IEnumerable<AppraisalRow> ArmorRows(AppraisalResponse response)
    {
        if (response.Armor is not { } armor || !response.Ints.TryGetValue(ArmorLevelInt, out var level) || level <= 0)
            return Array.Empty<AppraisalRow>();
        var mask = response.ArmorMask;
        var rows = new List<AppraisalRow>
        {
            new AppraisalRow("Armor level", level.ToString(CultureInfo.InvariantCulture), ToneOf(mask, ArmorLevelBit)),
            Protection("Slashing", armor.Slashing, mask, ArmorSlashingBit),
            Protection("Piercing", armor.Piercing, mask, ArmorPiercingBit),
            Protection("Bludgeoning", armor.Bludgeoning, mask, ArmorBludgeoningBit),
            Protection("Fire", armor.Fire, mask, ArmorFireBit),
            Protection("Cold", armor.Cold, mask, ArmorColdBit),
            Protection("Acid", armor.Acid, mask, ArmorAcidBit),
        };
        if (armor.Nether > Epsilon) rows.Add(Protection("Nether", armor.Nether, null, 0));
        rows.Add(Protection("Lightning", armor.Lightning, mask, ArmorLightningBit));
        return rows;
    }

    private static AppraisalRow Protection(string label, float modifier, HighlightMask? mask, uint bit) =>
        new(label, $"{Quality(modifier)} ({modifier.ToString("0.0", CultureInfo.InvariantCulture)})", ToneOf(mask, bit));

    /// <summary>Retail's word for a protection modifier, from None to Unparalleled (OpenAC's bands).</summary>
    private static string Quality(float modifier) => modifier switch
    {
        <= Epsilon => "None",
        < 0.4f => "Poor",
        < 0.8f => "Below Average",
        < 1.2f => "Average",
        < 1.6f => "Above Average",
        < 2.0f => "Excellent",
        _ => "Unparalleled",
    };

    /// <summary>
    /// The tone of a value from its mask: raised when the highlight bit is set and the colour bit too, lowered when the highlight bit is set
    /// alone, and normal otherwise. The server decides both bits; the client never works out a buff itself.
    /// </summary>
    private static AppraisalTone ToneOf(HighlightMask? mask, uint bit)
    {
        if (mask is not { } masks || (masks.Highlight & bit) == 0) return AppraisalTone.Normal;
        return (masks.Color & bit) != 0 ? AppraisalTone.Raised : AppraisalTone.Lowered;
    }

    private static IEnumerable<AppraisalRow> TextRows(IReadOnlyDictionary<uint, string> strings, uint property) =>
        strings.TryGetValue(property, out var text) && text.Length > 0 ? new[] { new AppraisalRow(null, text) } : Array.Empty<AppraisalRow>();

    private sealed record SectionRule(string Heading, Func<AppraisalResponse, IEnumerable<AppraisalRow>> Rows);
}
