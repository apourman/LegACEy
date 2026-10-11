using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>A figure in the summary: a label under a value, such as Value 75.</summary>
public sealed record AppraisalFigure(string Label, string Value);

/// <summary>How a value is coloured: raised by a buff or spell (green), lowered by a debuff (red), or as it is.</summary>
public enum AppraisalTone { Normal, Raised, Lowered }

/// <summary>
/// One line of a section. A line with a label is a label and a value; a line without one is text across the section. The tone colours the
/// value.
/// </summary>
public record AppraisalRow(string? Label, string Text, AppraisalTone Tone = AppraisalTone.Normal);

/// <summary>A spell: its name, with its description under it.</summary>
public sealed record AppraisalSpellRow(string Name, string Description, AppraisalTone Tone = AppraisalTone.Normal)
    : AppraisalRow(Name, Description, Tone);

/// <summary>A meter filled to Current of Maximum, labelled, with an optional note under it such as a cost.</summary>
public sealed record AppraisalMeterRow(string Name, int Current, int Maximum, string? Note, AppraisalTone Tone = AppraisalTone.Normal)
    : AppraisalRow(Name, string.Format(CultureInfo.InvariantCulture, "{0:N0} / {1:N0}", Current, Maximum), Tone);

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
    private const uint SpellcraftInt = 106, CurManaInt = 107, MaxManaInt = 108, ArcaneLoreInt = 109, AllegianceRankInt = 110, SkillLevelInt = 115;
    private const uint UsageSkillInt = 176, AttributeInt = 257, AttributeLevelInt = 258, SecondAttributeInt = 259, SecondAttributeLevelInt = 260;
    private const uint ManaRateFloat = 5;
    // The high bit the server sets on an enchantment's spell id in the spell book.
    private const uint EnchantmentBit = 0x80000000;
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
        new("Combat", (response, _) => WeaponRows(response)),
        new("Armor", (response, _) => ArmorRows(response)),
        new("Spells", (response, spells) => SpellRows(response, spells)),
        new("Requirements", (response, _) => RequirementRows(response)),
        new("Use", (response, _) => TextRows(response.Strings, UseString)),
    };

    /// <summary>
    /// Builds what the window shows from one appraisal. <paramref name="spells"/> gives each spell's name and description by id (the
    /// portal DAT's spell table); a spell it does not know is named by its id.
    /// </summary>
    public static AppraisalContent From(AppraisalResponse response, IReadOnlyDictionary<uint, SpellInfo>? spells = null)
    {
        var figures = FigureRules
            .Select(rule => (rule.Label, Text: rule.Text(response)))
            .Where(figure => figure.Text != null)
            .Select(figure => new AppraisalFigure(figure.Label, figure.Text!))
            .ToList();
        var sections = SectionRules
            .Select(rule => new AppraisalSection(rule.Heading, rule.Rows(response, spells ?? NoSpells).ToList()))
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

    private static readonly IReadOnlyDictionary<uint, SpellInfo> NoSpells = new Dictionary<uint, SpellInfo>();

    /// <summary>The spells in the order the server lists them, cantrips included, each with its description under it.</summary>
    private static IEnumerable<AppraisalRow> SpellRows(AppraisalResponse response, IReadOnlyDictionary<uint, SpellInfo> spells) =>
        response.SpellBook.Select(raw => SpellRow(raw & ~EnchantmentBit, spells));

    private static AppraisalRow SpellRow(uint id, IReadOnlyDictionary<uint, SpellInfo> spells) =>
        spells.TryGetValue(id, out var spell)
            ? new AppraisalSpellRow(spell.Name, spell.Description)
            : new AppraisalSpellRow("Spell " + id.ToString(CultureInfo.InvariantCulture), string.Empty);

    /// <summary>The wield lines, then activation, spellcraft and the mana meter. The wording follows OpenAC's ItemAppraisalTextFormatter (MIT).</summary>
    private static IEnumerable<AppraisalRow> RequirementRows(AppraisalResponse response)
    {
        foreach (var slot in WieldSlots)
        {
            if (!response.Ints.TryGetValue(slot.Requirement, out var type) || !response.Ints.TryGetValue(slot.Stat, out var stat)
                || !response.Ints.TryGetValue(slot.Level, out var level)) continue;
            if (WieldText(type, stat, level) is { } text) yield return new AppraisalRow("Wield", text);
        }
        var activation = ActivationParts(response).ToList();
        if (activation.Count > 0) yield return new AppraisalRow("Activation", string.Join(", ", activation));
        if (Number(response, SpellcraftInt) is { } spellcraft) yield return new AppraisalRow("Spellcraft", spellcraft);
        if (ManaMeter(response) is { } mana) yield return mana;
    }

    /// <summary>The four wield requirement slots: the requirement type, the skill or attribute it names, and its level.</summary>
    private static readonly (uint Requirement, uint Stat, uint Level)[] WieldSlots =
    {
        (158, 159, 160), (270, 271, 272), (273, 274, 275), (276, 277, 278),
    };

    /// <summary>
    /// One wield requirement by its type: 1 skill, 2 base skill, 3-6 the primary or secondary attribute (base for 4 and 6), 7 level, 8 a
    /// trained or specialized skill. Types 9-12 (standing, creature, heritage) are not shown yet.
    /// </summary>
    private static string? WieldText(int type, int stat, int level) => type switch
    {
        1 => Requirement(Named(SkillNames, stat), level),
        2 => Requirement(Named(SkillNames, stat), level, "Base "),
        3 => Requirement(Named(PrimaryAttributeNames, stat), level),
        4 => Requirement(Named(PrimaryAttributeNames, stat), level, "Base "),
        5 => Requirement(Named(SecondaryAttributeNames, stat), level),
        6 => Requirement(Named(SecondaryAttributeNames, stat), level, "Base "),
        7 => level > 0 ? "Level " + level.ToString(CultureInfo.InvariantCulture) : null,
        8 => Named(SkillNames, stat) is { } skill ? (level == 3 ? "Specialized " : "Trained ") + skill : null,
        _ => null,
    };

    /// <summary>Arcane Lore, Allegiance Rank, the usage skill, then the primary and secondary attributes, as retail lists them.</summary>
    private static IEnumerable<string> ActivationParts(AppraisalResponse response) => new[]
    {
        Requirement("Arcane Lore", Int(response, ArcaneLoreInt)),
        Requirement("Allegiance Rank", Int(response, AllegianceRankInt)),
        Requirement(Named(SkillNames, Int(response, UsageSkillInt)), Int(response, SkillLevelInt)),
        Requirement(Named(PrimaryAttributeNames, Int(response, AttributeInt)), Int(response, AttributeLevelInt)),
        Requirement(Named(SecondaryAttributeNames, Int(response, SecondAttributeInt)), Int(response, SecondAttributeLevelInt)),
    }.OfType<string>();

    /// <summary>The mana meter: current and maximum mana, with retail's cost line from the mana rate. ACE sends the rate negative (it burns -rate a second).</summary>
    private static AppraisalMeterRow? ManaMeter(AppraisalResponse response)
    {
        if (!response.Ints.TryGetValue(CurManaInt, out var current) || !response.Ints.TryGetValue(MaxManaInt, out var maximum)) return null;
        var cost = response.Floats.TryGetValue(ManaRateFloat, out var rate) && Math.Abs(rate) > 0.000001 ? ManaCost(Math.Abs(rate)) : null;
        return new AppraisalMeterRow("Mana", current, maximum, cost);
    }

    private static string ManaCost(double rate)
    {
        var seconds = (int)Math.Round(1 / rate);
        return "1 point per " + seconds.ToString(CultureInfo.InvariantCulture) + (seconds == 1 ? " second" : " seconds");
    }

    /// <summary>"name level", with an optional prefix, when the name is known and the level is set; otherwise nothing.</summary>
    private static string? Requirement(string? name, int level, string prefix = "") =>
        name == null || level <= 0 ? null : prefix + name + " " + level.ToString(CultureInfo.InvariantCulture);

    private static string? Named(IReadOnlyList<string> names, int id) => id > 0 && id < names.Count ? names[id] : null;

    private static int Int(AppraisalResponse response, uint property) => response.Ints.TryGetValue(property, out var value) ? value : 0;

    // The attribute names by the server's PropertyAttribute and PropertyAttribute2nd ids (ACE.Entity), as OpenAC names them.
    private static readonly string[] PrimaryAttributeNames = { string.Empty, "Strength", "Endurance", "Quickness", "Coordination", "Focus", "Self" };
    private static readonly string[] SecondaryAttributeNames =
    {
        string.Empty, "Max Health", "Health", "Max Stamina", "Stamina", "Max Mana", "Mana"
    };

    private sealed record SectionRule(string Heading, Func<AppraisalResponse, IReadOnlyDictionary<uint, SpellInfo>, IEnumerable<AppraisalRow>> Rows);
}
