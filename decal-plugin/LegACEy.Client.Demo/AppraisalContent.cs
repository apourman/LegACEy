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

/// <summary>The colour a meter fills with: mana blue, health red or stamina gold.</summary>
public enum AppraisalMeterFill { Mana, Health, Stamina }

/// <summary>A meter filled to Current of Maximum, labelled, with an optional note under it such as a cost.</summary>
public sealed record AppraisalMeterRow(string Name, int Current, int Maximum, string? Note, AppraisalTone Tone = AppraisalTone.Normal,
    AppraisalMeterFill Fill = AppraisalMeterFill.Mana)
    : AppraisalRow(Name, string.Format(CultureInfo.InvariantCulture, "{0:N0} / {1:N0}", Current, Maximum), Tone);

/// <summary>A section below the summary: a heading and its rows. A section with no rows is never built.</summary>
public sealed record AppraisalSection(string Heading, IReadOnlyList<AppraisalRow> Rows);

/// <summary>
/// What the appraisal window shows for one object: the header, the summary figures, the sections and the long description. The window
/// draws this model and nothing else, so the rules that build it are tested without drawing. Built by <see cref="From"/>.
/// </summary>
public sealed record AppraisalContent(uint ObjectId, string Name, ItemVisual? Visual, string TypeLine, IReadOnlyList<AppraisalFigure> Figures,
    IReadOnlyList<AppraisalSection> Sections, string Description, AppraisalInscription? Inscription, string Subtitle = "")
{
    // The server's property ids (ACE.Entity PropertyInt, PropertyString and PropertyDataId).
    private const uint ItemTypeInt = 1, EncumbranceValInt = 5, ValueInt = 19, ItemWorkmanshipInt = 105, SalvagedItemsInt = 170;
    private const uint NameString = 1, UseString = 14, LongDescString = 16;
    private const uint IconDid = 8, IconOverlayDid = 50, IconUnderlayDid = 52;
    private const uint SpellcraftInt = 106, CurManaInt = 107, MaxManaInt = 108, ArcaneLoreInt = 109, AllegianceRankInt = 110, SkillLevelInt = 115;
    private const uint UsageSkillInt = 176, AttributeInt = 257, AttributeLevelInt = 258, SecondAttributeInt = 259, SecondAttributeLevelInt = 260;
    private const uint ManaRateFloat = 5;
    // The high bit the server sets on an enchantment's spell id in the spell book.
    private const uint EnchantmentBit = 0x80000000;
    private const uint ArmorLevelInt = 28, WeaponTypeInt = 353;
    // The bonuses as PropertyFloat ids (ACE.Entity), each an AssessmentProperty: melee, missile and magic defence, mana conversion, and the
    // elemental damage modifier against monsters. The damage type of a spell's elemental bonus is PropertyInt DamageType (45), and the
    // elemental damage bonus of a weapon is PropertyInt ElementalDamageBonus (204).
    private const uint WeaponDefenseFloat = 29, ManaConversionFloat = 144, MissileDefenseFloat = 149, MagicDefenseFloat = 150,
        ElementalDamageModFloat = 152, DamageTypeInt = 45, ElementalDamageBonusInt = 204;
    // Creatures and characters (ACE.Entity PropertyInt and PropertyString), as OpenAC's creature appraisal reads them (CreatureAppraisalRows, MIT).
    private const uint LevelInt = 25, CreatureTypeInt = 2, HeritageGroupInt = 188, GenderInt = 113, TitleIdInt = 261, RankInt = 30,
        FollowersInt = 35, DeathsInt = 43, ChessRankInt = 181, FishingSkillInt = 192, TitlesEarnedInt = 262, AgeInt = 125,
        FactionBitsInt = 281, SocietyCelHanInt = 287, SocietyEldWebInt = 288, SocietyRadBloInt = 289,
        PlayerKillerStatusInt = 134;
    private const uint DamageRatingInt = 307, DamageResistRatingInt = 308, CritRatingInt = 313, CritDamageRatingInt = 314,
        CritResistRatingInt = 315, CritDamageResistRatingInt = 316, DotResistRatingInt = 350, LifeResistRatingInt = 351;
    private const uint MonarchsTitleString = 21, PatronsTitleString = 35, FellowshipString = 10, DateOfBirthString = 43, TemplateString = 5;
    // The attribute block the server writes after a successful appraisal: Strength, Endurance, Quickness, Coordination, Focus, Self, then
    // Stamina, Mana and their maximums. The highlight bits are the attributes' own (Health 0x40, Stamina 0x80, Mana 0x100).
    private const int AttributeCount = 10, StaminaAt = 6, ManaAt = 7, StaminaMaxAt = 8, ManaMaxAt = 9;
    private const int HealthBit = 6, StaminaBit = 7, ManaBit = 8;
    private const uint UnenchantableArmorLevel = 9999;
    // The society faction bits (OpenAC's CreatureAppraisalRows: Celestial Hand, Eldrytch Web, Radiant Blood).
    private const int CelestialHandBit = 0x1, EldrytchWebBit = 0x2, RadiantBloodBit = 0x4;
    // The value a retail appraisal shows where the server did not send the figure.
    private const string UnknownValue = "???";
    // Values closer than this are the same figure (a bonus of 1.0 is none).
    private const float Epsilon = 0.0002f;

    // Highlight and colour bits as ACE.Server's WeaponMask and ArmorMask set them. A highlighted bit with its colour bit set is raised;
    // highlighted without it, lowered.
    private const uint WeaponAttackBit = 0x1, WeaponMeleeDefenseBit = 0x2, WeaponSpeedBit = 0x4, WeaponDamageBit = 0x8, WeaponDamageVarianceBit = 0x10,
        WeaponDamageModBit = 0x20;
    private const uint ResistManaConversionBit = 0x1000, ResistElementalDamageBit = 0x2000;
    // ACE's Skill ids of the two launchers, which the weapon profile names.
    private const uint BowSkill = 2, CrossbowSkill = 3;
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
    /// The sections below the summary, in the order retail's examination text runs: tinkering, set and ratings, the weapon's and armour's
    /// lines, the spells by name, the special properties, use, requirements, the rest, then the magic (spellcraft, mana and each spell's
    /// description) and enchantments, as OpenAC's BuildReport orders them (MIT). A rule gives a heading and its rows; a section with no
    /// rows is left out. The grouped lines' rules are in AppraisalOtherLines.cs.
    /// </summary>
    private static readonly SectionRule[] SectionRules =
    {
        new("Tinkering", (response, _) => AppraisalOtherLines.Rows(response, AppraisalOtherLines.Tinkering)),
        new("Set & Ratings", (response, _) => AppraisalOtherLines.Rows(response, AppraisalOtherLines.SetAndRatings)),
        new("Combat", (response, _) => CombatRows(response)),
        new("Armor", (response, _) => ArmorRows(response)),
        new("Spells", (response, spells) => SpellNameRows(response, spells)),
        new("Properties", (response, _) => AppraisalOtherLines.Rows(response, AppraisalOtherLines.Properties)),
        new("Use", (response, _) => TextRows(response.Strings, UseString)),
        new("Requirements", (response, _) => RequirementRows(response)),
        new("Other", (response, _) => AppraisalOtherLines.Rows(response, AppraisalOtherLines.Other)),
        new("Magic", (response, spells) => MagicRows(response, spells)),
        new("Enchantments", (response, spells) => SpellRows(response, spells, enchantments: true)),
    };

    /// <summary>
    /// Builds what the window shows from one appraisal. <paramref name="spells"/> gives each spell's name and description by id (the
    /// portal DAT's spell table, empty when there is none); a spell it does not know is named by its id. The inscription field depends on
    /// who the character is and whether the item is carried, which the appraisal does not say, so the caller passes them; with neither, an
    /// item is never editable. A creature (one with a creature profile) takes the creature layout instead of the item's sections and
    /// figures; <paramref name="names"/> gives a creature's type and a character's title, and a name it does not know is left out.
    /// <paramref name="validLocations"/> and <paramref name="coverage"/> are the item's equipable slots and clothing coverage from the world
    /// (the appraisal does not carry them); a piece of clothing or armour names what it covers on the type line.
    /// </summary>
    /// <summary>
    /// The window's title as retail names the object: a stack's count and plural name ("5 Mana Potions"; without a plural name, "s", or
    /// after an s "es"), with the material in front ("Iron Mace") unless the name already has it (a gem named for its material).
    /// </summary>
    private static string TitleOf(AppraisalResponse response, InventoryObjectRead? world, string plural)
    {
        var name = world is { Name.Length: > 0 } ? world.Name
            : response.Strings.TryGetValue(NameString, out var sent) && sent.Length > 0 ? sent : "Unknown item";
        var count = world?.StackCount ?? 0;
        if (count > 1) name = plural.Length > 0 ? plural : name + (name.EndsWith("s", StringComparison.Ordinal) ? "es" : "s");
        if (AppraisalOtherLines.Material(response) is { } material && name.IndexOf(material, StringComparison.Ordinal) < 0)
            name = material + " " + name;
        return count > 1 ? Invariant(count) + " " + name : name;
    }

    public static AppraisalContent From(AppraisalResponse response, IReadOnlyDictionary<uint, SpellInfo> spells,
        string characterName = "", bool carried = false, CharacterNames? names = null, uint validLocations = 0, uint coverage = 0,
        InventoryObjectRead? world = null, string plural = "", uint descriptionFlags = 0)
    {
        var hasCreatureProfile = response.Creature != null;
        var figures = hasCreatureProfile
            ? new List<AppraisalFigure>()
            : FigureRules
                .Select(rule => (rule.Label, Text: rule.Text(response)))
                .Where(figure => figure.Text != null)
                .Select(figure => new AppraisalFigure(figure.Label, figure.Text!))
                .ToList();
        var covered = CoveredAreas(validLocations, coverage);
        var sections = (hasCreatureProfile
                ? CreatureSections(response)
                : SectionRules.Select(rule => new AppraisalSection(rule.Heading, rule.Rows(response, spells).ToList())))
            .Where(section => section.Rows.Count > 0)
            .ToList();
        // The server's appraisal carries no name or icon (retail takes both from the object's create message), so the world's come first.
        var visual = world is { Visual.Icon: not 0 } ? world.Visual
            : response.Dids.TryGetValue(IconDid, out var icon) && icon != 0
                ? new ItemVisual(icon, Did(response, IconUnderlayDid), Did(response, IconOverlayDid), 0)
                : null;
        return new AppraisalContent(
            response.ObjectId,
            TitleOf(response, world, plural),
            visual,
            hasCreatureProfile ? CreatureTypeLine(response, names ?? CharacterNames.None) : TypeLineOf(response, covered),
            figures,
            sections,
            response.Strings.TryGetValue(LongDescString, out var description) ? description : string.Empty,
            AppraisalInscription.From(response, characterName, carried, descriptionFlags),
            hasCreatureProfile ? TitleOf(response, names ?? CharacterNames.None) : string.Empty);
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
    /// ("Staff · Heavy Weapons"), then what a piece of clothing or armour covers ("Armor · Covers Chest, Upper Arms").
    /// Skill names are ACE's Skill.ToSentence (ACE.Entity/Enum/Skill.cs), indexed by the skill's id.
    /// </summary>
    private static string TypeLineOf(AppraisalResponse response, string covered)
    {
        var weaponType = response.Ints.TryGetValue(WeaponTypeInt, out var kind) && kind > 0 && kind < WeaponTypeNames.Length ? WeaponTypeNames[kind] : null;
        var type = weaponType ?? (response.Ints.TryGetValue(ItemTypeInt, out var bits) ? ItemTypeNames.FirstOrDefault(item => (bits & item.Bit) != 0).Name : null);
        // The weapon's skill comes from its profile: PropertyInt WeaponSkill is not an assessment property, so the server never sends it as one.
        var skill = response.Weapon is { } weapon && weapon.WeaponSkill > 0 && weapon.WeaponSkill < SkillNames.Length
            ? SkillNames[weapon.WeaponSkill]
            : null;
        return string.Join(" · ", new[] { type, skill, covered.Length == 0 ? null : "Covers " + covered }.Where(part => !string.IsNullOrEmpty(part)));
    }

    // The clothing slots (0x0800_7FFF) and the weapon and shield slots (0x03F0_0000), as OpenAC's formatter tests them.
    private const uint ClothingLocations = 0x0800_7FFF, WeaponAndShieldLocations = 0x03F0_0000;

    /// <summary>
    /// "Chest, Upper Arms" for a clothing or armour piece: one that is in a clothing slot and not a weapon or shield. Named by the
    /// coverage bits, as OpenAC's ClothingCoverage names them (MIT). Empty when the item covers nothing it names.
    /// </summary>
    private static string CoveredAreas(uint validLocations, uint coverage)
    {
        if ((validLocations & WeaponAndShieldLocations) != 0 || (validLocations & ClothingLocations) == 0) return string.Empty;
        return string.Join(", ", CoverageNames.Where(area => (coverage & area.Bits) != 0).Select(area => area.Name));
    }

    /// <summary>The coverage areas in OpenAC's order, each with the two priority bits that cover it.</summary>
    private static readonly (uint Bits, string Name)[] CoverageNames =
    {
        (0x4000, "Head"), (0x0008 | 0x0400, "Chest"), (0x0010 | 0x0800, "Abdomen"), (0x0020 | 0x1000, "Upper Arms"),
        (0x0040 | 0x2000, "Lower Arms"), (0x0080, "Hands"), (0x0002 | 0x0100, "Upper Legs"), (0x0004 | 0x0200, "Lower Legs"), (0x10000, "Feet")
    };

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
    /// The Combat rows, as OpenAC's formatter orders them (MIT): a weapon's damage (a launcher's damage bonus), its elemental bonus, a
    /// launcher's damage modifier, speed, a launcher's range or a melee weapon's attack; then the melee, missile and magic defence bonuses,
    /// and a caster's mana conversion and elemental damage. Values are coloured from the masks the server sent, and the client does not work
    /// out a buff itself.
    /// </summary>
    private static IEnumerable<AppraisalRow> CombatRows(AppraisalResponse response)
    {
        if (response.Weapon is { } weapon)
        {
            var launcher = IsLauncher(weapon);
            yield return launcher
                ? new AppraisalRow("Damage Bonus", DamageText(weapon, withType: false), DamageTone(response.WeaponMask))
                : new AppraisalRow("Damage", DamageText(weapon, withType: true), DamageTone(response.WeaponMask));
            if (response.Ints.TryGetValue(ElementalDamageBonusInt, out var elemental) && elemental > 0)
                yield return new AppraisalRow(null, $"Elemental Damage Bonus: {elemental.ToString(CultureInfo.InvariantCulture)}, {DamageWord(weapon.DamageType)}.");
            if (launcher)
                yield return new AppraisalRow("Damage Modifier", SignedPercent(weapon.DamageMod - 1), ToneOf(response.WeaponMask, WeaponDamageModBit));
            yield return new AppraisalRow("Speed", SpeedText(weapon.WeaponTime), ToneOf(response.WeaponMask, WeaponSpeedBit));
            if (launcher) yield return new AppraisalRow("Range", RangeText(weapon));
            else if (Math.Abs(weapon.WeaponOffense - 1) > Epsilon)
                yield return new AppraisalRow("Attack", SignedPercent(weapon.WeaponOffense - 1), ToneOf(response.WeaponMask, WeaponAttackBit));
        }
        if (Modifier(response, WeaponDefenseFloat) is { } melee)
            yield return new AppraisalRow("Melee defense", melee, ToneOf(response.WeaponMask, WeaponMeleeDefenseBit));
        if (Modifier(response, MissileDefenseFloat) is { } missile) yield return new AppraisalRow("Missile defense", missile);
        if (Modifier(response, MagicDefenseFloat) is { } magic) yield return new AppraisalRow("Magic defense", magic);
        if (response.Floats.TryGetValue(ManaConversionFloat, out var conversion))
            yield return new AppraisalRow("Mana conversion", SignedPercent(conversion), ToneOf(response.ResistMask, ResistManaConversionBit));
        if (response.Floats.TryGetValue(ElementalDamageModFloat, out var elementalMod) && response.Ints.TryGetValue(DamageTypeInt, out var spellType))
        {
            // Retail's elemental bonus: against monsters, and against players at a quarter of it (OpenAC's ShowCasterData).
            yield return new AppraisalRow(null, $"Damage bonus for {DamageWord((uint)spellType)} spells:", ToneOf(response.ResistMask, ResistElementalDamageBit));
            yield return new AppraisalRow("vs. Monsters", SignedPercent(elementalMod - 1));
            yield return new AppraisalRow("vs. Players", SignedPercent((elementalMod - 1) * 0.25));
        }
    }

    /// <summary>
    /// A launcher is a bow or a crossbow. OpenAC gates on the hook's valid locations and ammunition type, which the server sends only for
    /// hooks (its hook profile is not written for weapons), so the weapon's skill names the launcher here.
    /// </summary>
    private static bool IsLauncher(WeaponProfile weapon) => weapon.WeaponSkill == BowSkill || weapon.WeaponSkill == CrossbowSkill;

    /// <summary>A bonus the float holds as a multiplier, as "+21.0%"; null when it is missing or none.</summary>
    private static string? Modifier(AppraisalResponse response, uint property) =>
        response.Floats.TryGetValue(property, out var value) && Math.Abs(value - 1) > Epsilon ? SignedPercent(value - 1) : null;

    /// <summary>A signed percentage with one decimal, as the approved render shows a bonus: "+21.0%", "-5.0%", "0.0%".</summary>
    private static string SignedPercent(double fraction) => fraction.ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture);

    /// <summary>The damage range, "41.28 – 86", or one figure when the variance leaves no range; with the damage type when asked.</summary>
    private static string DamageText(WeaponProfile weapon, bool withType)
    {
        var high = FormatDamage(weapon.Damage);
        var low = (1 - weapon.DamageVariance) * weapon.Damage;
        var range = weapon.Damage - low > Epsilon ? $"{FormatDamage(low)} – {high}" : high;
        var type = DamageTypeName(weapon.DamageType);
        return withType && type.Length > 0 ? $"{range} {type}" : range;
    }

    /// <summary>The damage type's name as OpenAC names it, or "type N" for one it does not know. Used where a line names a type.</summary>
    private static string DamageWord(uint type) =>
        DamageTypeName(type) is { Length: > 0 } name ? name : $"type {type.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// A launcher's range in yards, from its maximum velocity as OpenAC works it out: capped at 85, rounded down to five yards from 10 up.
    /// The "(based on STRENGTH 100)" note is added when the server estimated the velocity.
    /// </summary>
    private static string RangeText(WeaponProfile weapon)
    {
        var raw = Math.Min(85d, 2d * Math.Pow(weapon.MaxVelocity, 2d) * (1d / 9.8d) * 1.094d);
        var range = raw < 10d ? (int)Math.Ceiling(raw) : (int)raw - (int)raw % 5;
        var text = range.ToString(CultureInfo.InvariantCulture) + " yds.";
        return weapon.MaxVelocityEstimated != 0 ? text + " (based on STRENGTH 100)" : text;
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
    /// Nether shows only on a piece that protects against it. A piece with no armour level has none. What it covers is on the type line.
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

    /// <summary>
    /// The item's own spells (cantrips included) or, with <paramref name="enchantments"/>, the enchantments on it, in the order the server
    /// lists them, each with its description under it. Neither ACE nor OpenAC sorts appraisal spells by the table's DisplayOrder, so this
    /// does not either.
    /// </summary>
    private static IEnumerable<AppraisalRow> SpellRows(AppraisalResponse response, IReadOnlyDictionary<uint, SpellInfo> spells, bool enchantments) =>
        response.SpellBook.Where(raw => (raw & EnchantmentBit) != 0 == enchantments).Select(raw => SpellRow(raw & ~EnchantmentBit, spells));

    /// <summary>Retail's short spell line, after the weapon's lines: the item's own spells by name, as one sentence.</summary>
    private static IEnumerable<AppraisalRow> SpellNameRows(AppraisalResponse response, IReadOnlyDictionary<uint, SpellInfo> spells)
    {
        var names = SpellRows(response, spells, enchantments: false).Select(row => row.Label).ToList();
        return names.Count == 0 ? Array.Empty<AppraisalRow>() : new[] { new AppraisalRow(null, string.Join(", ", names)) };
    }

    /// <summary>Spellcraft and the mana meter, then each of the item's own spells with its description, as retail ends its text.</summary>
    private static IEnumerable<AppraisalRow> MagicRows(AppraisalResponse response, IReadOnlyDictionary<uint, SpellInfo> spells)
    {
        if (Number(response, SpellcraftInt) is { } spellcraft) yield return new AppraisalRow("Spellcraft", spellcraft);
        if (ManaMeter(response) is { } mana) yield return mana;
        foreach (var spell in SpellRows(response, spells, enchantments: false)) yield return spell;
    }

    private static AppraisalRow SpellRow(uint id, IReadOnlyDictionary<uint, SpellInfo> spells) =>
        spells.TryGetValue(id, out var spell)
            ? new AppraisalSpellRow(spell.Name, spell.Description)
            : new AppraisalSpellRow("Spell " + id.ToString(CultureInfo.InvariantCulture), string.Empty);

    /// <summary>
    /// The level limits, the wield lines, the item's level, then activation, in retail's order. The wording follows OpenAC's
    /// ItemAppraisalTextFormatter (MIT).
    /// </summary>
    private static IEnumerable<AppraisalRow> RequirementRows(AppraisalResponse response)
    {
        foreach (var row in AppraisalOtherLines.Rows(response, AppraisalOtherLines.LevelLimits)) yield return row;
        foreach (var slot in WieldSlots)
        {
            if (!response.Ints.TryGetValue(slot.Requirement, out var type) || !response.Ints.TryGetValue(slot.Stat, out var stat)
                || !response.Ints.TryGetValue(slot.Level, out var level)) continue;
            if (WieldText(type, stat, level) is { } text) yield return new AppraisalRow("Wield", text);
        }
        foreach (var row in AppraisalOtherLines.Rows(response, AppraisalOtherLines.ItemLevel)) yield return row;
        var activation = ActivationParts(response).ToList();
        if (activation.Count > 0) yield return new AppraisalRow("Activation", string.Join(", ", activation));
    }

    /// <summary>The four wield requirement slots: the requirement type, the skill or attribute it names, and its level.</summary>
    private static readonly (uint Requirement, uint Stat, uint Level)[] WieldSlots =
    {
        (158, 159, 160), (270, 271, 272), (273, 274, 275), (276, 277, 278),
    };

    /// <summary>
    /// One wield requirement by its type, as OpenAC's ItemAppraisalTextFormatter words it: 1 skill, 2 base skill, 3-6 the primary or
    /// secondary attribute (base for 4 and 6), 7 level, 8 a trained or specialized skill (no level), 9-10 a faction standing. Types 11
    /// (creature) and 12 (heritage) need name tables this window does not carry yet, so they show no line.
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
        // SkillAdvancementClass (ACE.Entity): 2 trained, 3 specialized. Inactive and untrained show no line.
        8 => (Named(SkillNames, stat), level) switch
        {
            (string skill, 2) => "Trained " + skill,
            (string skill, 3) => "Specialized " + skill,
            _ => null,
        },
        9 or 10 => Requirement(Standing(stat), level),
        _ => null,
    };

    /// <summary>The faction a standing requirement names, by its id (OpenAC's names). An unknown id has no name.</summary>
    private static string? Standing(int id) => id switch
    {
        0x11F => "Standing with the Celestial Hand",
        0x120 => "Standing with the Eldrytch Web",
        0x121 => "Standing with the Radiant Blood",
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

    /// <summary>
    /// The mana meter: current and maximum mana, with the cost line from the mana rate. As ACE's appraisal rules
    /// (Source/ACE.MarketApi/AppraisalRules.cs) do, the cost shows only for a negative rate, which burns -rate points a second.
    /// </summary>
    private static AppraisalMeterRow? ManaMeter(AppraisalResponse response)
    {
        if (!response.Ints.TryGetValue(CurManaInt, out var current) || !response.Ints.TryGetValue(MaxManaInt, out var maximum)) return null;
        var cost = response.Floats.TryGetValue(ManaRateFloat, out var rate) && rate < 0 ? ManaCost(-1 / rate) : null;
        return new AppraisalMeterRow("Mana", current, maximum, cost);
    }

    /// <summary>One point per the given seconds, rounded and never under one second.</summary>
    private static string ManaCost(double seconds)
    {
        var whole = Math.Max(1, (int)Math.Round(seconds));
        return "1 point per " + whole.ToString(CultureInfo.InvariantCulture) + (whole == 1 ? " second" : " seconds");
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

    /// <summary>
    /// A character is a player, not a creature. Heritage alone is not enough: a creature can carry one too, since ACE's
    /// Creature.GenerateNewFace sets HeritageGroup from the weenie's HeritageGroupName (Source/ACE.Server/WorldObjects/Creature.cs:147-155).
    /// So a character also needs a marker only a player has: its template name, which PlayerFactory sets on every new character
    /// (Source/ACE.Server/Factories/PlayerFactory.cs:136; the privacy options do not hide it), its date of birth, which only Player's
    /// constructor sets (Player.cs:101; the options can hide it), or its display title. OpenAC picks its character view from the template
    /// name or the title (AcDream.App/UI/Layout/AppraisalUiController.cs, SelectView).
    /// </summary>
    private static bool IsCharacter(AppraisalResponse response) =>
        Int(response, HeritageGroupInt) > 0 && (StringOf(response, TemplateString) != null || StringOf(response, DateOfBirthString) != null
            || Int(response, TitleIdInt) > 0);

    // Heritage names by the server's HeritageGroup, as OpenAC's CharacterIdentityText names them (MIT).
    private static readonly string[] HeritageNames =
    {
        string.Empty, "Aluvian", "Gharu'ndim", "Sho", "Viamontian", "Umbraen", "Gearknight", "Tumerok", "Lugian", "Empyrean", "Penumbraen",
        "Undead", "Olthoi", "Olthoi",
    };

    /// <summary>
    /// A character's type line is its level, then heritage and gender ("Level 126 · Aluvian Male"). A creature's is its type, then its level
    /// ("Drudge · Level 80"). A name the names do not hold is left out.
    /// </summary>
    private static string CreatureTypeLine(AppraisalResponse response, CharacterNames names)
    {
        var level = response.Ints.TryGetValue(LevelInt, out var value) && value > 0 ? "Level " + Invariant(value) : null;
        if (IsCharacter(response))
        {
            var heritage = Named(HeritageNames, Int(response, HeritageGroupInt));
            var gender = Int(response, GenderInt) switch { 1 => "Male", 2 => "Female", _ => null };
            return Join(" · ", level, Join(" ", heritage, gender));
        }
        var kind = Int(response, CreatureTypeInt);
        var type = kind > 0 && names.CreatureTypes.TryGetValue((uint)kind, out var typeName) ? typeName : null;
        return Join(" · ", type, level);
    }

    /// <summary>A character's title, muted under the type line. A creature has none.</summary>
    private static string TitleOf(AppraisalResponse response, CharacterNames names)
    {
        if (!IsCharacter(response)) return string.Empty;
        var id = Int(response, TitleIdInt);
        return id > 0 && names.Titles.TryGetValue((uint)id, out var title) ? title : string.Empty;
    }

    /// <summary>
    /// A creature's sections, in the order the window shows them (the approved renders' layout): the allegiance, vitals and attributes, then
    /// for a character its armour levels and details. The ratings are listed for any creature that has them.
    /// </summary>
    private static IEnumerable<AppraisalSection> CreatureSections(AppraisalResponse response)
    {
        var creature = response.Creature!;
        var character = IsCharacter(response);
        if (character) yield return Section("Allegiance", AllegianceRows(response));
        yield return Section("Vitals", VitalRows(creature));
        yield return Section("Attributes", AttributeRows(creature));
        if (character) yield return Section("Armor", ArmorLevelRows(response.Levels));
        yield return Section("Ratings", RatingRows(response));
        if (character) yield return Section("Character", CharacterRows(response));
    }

    private static AppraisalSection Section(string heading, IEnumerable<AppraisalRow> rows) => new(heading, rows.ToList());

    /// <summary>
    /// A character's society, rank and allegiance, in retail's order (OpenAC's CreatureAppraisalRows): the society, then the monarch and
    /// patron (one line when they are the same), or "Alleg. Monarch" with the followers when no monarch is named, then the rank, the
    /// followers and the fellowship.
    /// </summary>
    private static IEnumerable<AppraisalRow> AllegianceRows(AppraisalResponse response)
    {
        if (SocietyText(response) is { } society) yield return new AppraisalRow("Society", society);
        var monarch = StringOf(response, MonarchsTitleString);
        var patron = StringOf(response, PatronsTitleString);
        int? followers = response.Ints.TryGetValue(FollowersInt, out var count) ? Math.Max(0, count) : null;
        if (monarch == null)
        {
            if (followers is { } alone) yield return new AppraisalRow("Alleg. Monarch", FollowerCount(alone));
        }
        else if (string.Equals(monarch, patron, StringComparison.Ordinal))
            yield return new AppraisalRow("Monarch/Patron", monarch);
        else
        {
            yield return new AppraisalRow("Monarch", monarch);
            if (patron != null) yield return new AppraisalRow("Patron", patron);
        }
        if (response.Ints.TryGetValue(RankInt, out var rank) && rank > 0) yield return new AppraisalRow("Rank", Invariant(rank));
        if (monarch != null && followers is { } named) yield return new AppraisalRow("Followers", Invariant(named));
        if (StringOf(response, FellowshipString) is { } fellowship) yield return new AppraisalRow("Fellowship", fellowship);
    }

    /// <summary>"1 Follower" or "12 Followers".</summary>
    private static string FollowerCount(int count) => Invariant(count) + (count == 1 ? " Follower" : " Followers");

    /// <summary>
    /// The society by its faction bit, with the rank's name ("Celestial Hand ~ Adept"); "???" when the bits name none. Retail colours it
    /// against the examiner's own society; this window does not know that, so the society is only text.
    /// </summary>
    private static string? SocietyText(AppraisalResponse response)
    {
        if (!response.Ints.TryGetValue(FactionBitsInt, out var bits)) return null;
        if ((bits & CelestialHandBit) != 0) return "Celestial Hand" + SocietyRank(response, SocietyCelHanInt);
        if ((bits & EldrytchWebBit) != 0) return "Eldrytch Web" + SocietyRank(response, SocietyEldWebInt);
        if ((bits & RadiantBloodBit) != 0) return "Radiant Blood" + SocietyRank(response, SocietyRadBloInt);
        return UnknownValue;
    }

    private static string SocietyRank(AppraisalResponse response, uint property) => Int(response, property) switch
    {
        >= 1 and <= 100 => " ~ Initiate",
        >= 101 and <= 300 => " ~ Adept",
        >= 301 and <= 600 => " ~ Knight",
        >= 601 and <= 1000 => " ~ Lord",
        >= 1001 and <= 1500 => " ~ Master",
        _ => string.Empty,
    };

    /// <summary>
    /// Health, stamina and mana as meters, current over maximum, as far as the appraisal revealed them. The attributes (so stamina and mana)
    /// come only with a successful appraisal. Without them health shows as a percentage, and stamina and mana as "???", as retail does.
    /// </summary>
    private static IEnumerable<AppraisalRow> VitalRows(CreatureProfile creature)
    {
        var attributes = creature.Attributes.Count == AttributeCount ? creature.Attributes : null;
        if (attributes == null && creature.HealthMax > 0)
            yield return new AppraisalRow("Health", Math.Round(100.0 * creature.Health / creature.HealthMax).ToString("0", CultureInfo.InvariantCulture) + " %");
        else
            yield return Vital("Health", creature.Health, creature.HealthMax, HealthBit, AppraisalMeterFill.Health, creature);
        yield return Vital("Stamina", attributes?[StaminaAt], attributes?[StaminaMaxAt], StaminaBit, AppraisalMeterFill.Stamina, creature);
        yield return Vital("Mana", attributes?[ManaAt], attributes?[ManaMaxAt], ManaBit, AppraisalMeterFill.Mana, creature);
    }

    /// <summary>A meter for a current and a maximum above zero, coloured from the attribute mask; otherwise the value is "???".</summary>
    private static AppraisalRow Vital(string name, uint? current, uint? maximum, int bit, AppraisalMeterFill fill, CreatureProfile creature) =>
        current is { } now && maximum is { } top && top > 0
            ? new AppraisalMeterRow(name, Clamp32(now), Clamp32(top), null, ToneOf(creature.AttributeMask, 1u << bit), fill)
            : new AppraisalRow(name, UnknownValue);

    // The attribute rows in retail's order, as PropertyAttribute ids (ACE.Entity: Strength 1, Endurance 2, Quickness 3, Coordination 4, Focus 5,
    // Self 6). The attribute block and the highlight bits index an attribute at its id less one.
    private static readonly uint[] AttributeOrder = { 1, 2, 4, 3, 5, 6 };

    /// <summary>Each attribute's current value, coloured raised or lowered from the creature's attribute masks; "???" without the attributes.</summary>
    private static IEnumerable<AppraisalRow> AttributeRows(CreatureProfile creature)
    {
        var attributes = creature.Attributes.Count == AttributeCount ? creature.Attributes : null;
        foreach (var id in AttributeOrder)
        {
            var index = (int)id - 1;
            var name = PrimaryAttributeNames[id];
            if (attributes == null || attributes[index] == 0) yield return new AppraisalRow(name, UnknownValue);
            else yield return new AppraisalRow(name, Invariant(attributes[index]), ToneOf(creature.AttributeMask, 1u << index));
        }
    }

    /// <summary>The armour level of each body part group, as "AL: 120/120/120". A value of 9999 or more is unenchantable (a star and the rest).</summary>
    private static IEnumerable<AppraisalRow> ArmorLevelRows(ArmorLevels? levels)
    {
        if (levels is not { } l || new[] { l.Head, l.Chest, l.Abdomen, l.UpperArm, l.LowerArm, l.Hand, l.UpperLeg, l.LowerLeg, l.Foot }.All(v => v == 0))
            yield break;
        yield return new AppraisalRow("Head/Chest/Groin", $"AL: {ArmorLevelPart(l.Head)}/{ArmorLevelPart(l.Chest)}/{ArmorLevelPart(l.Abdomen)}");
        yield return new AppraisalRow("Bicep/Wrist/Hand", $"AL: {ArmorLevelPart(l.UpperArm)}/{ArmorLevelPart(l.LowerArm)}/{ArmorLevelPart(l.Hand)}");
        yield return new AppraisalRow("Thigh/Shin/Foot", $"AL: {ArmorLevelPart(l.UpperLeg)}/{ArmorLevelPart(l.LowerLeg)}/{ArmorLevelPart(l.Foot)}");
    }

    private static string ArmorLevelPart(uint value) =>
        value >= UnenchantableArmorLevel ? "*" + (value - UnenchantableArmorLevel).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The damage and damage-resist ratings, then the damage-over-time and life-drain resists, each shown only when one of its figures is set.</summary>
    private static IEnumerable<AppraisalRow> RatingRows(AppraisalResponse response)
    {
        var damage = Int(response, DamageRatingInt);
        var damageResist = Int(response, DamageResistRatingInt);
        var crit = Int(response, CritRatingInt);
        var critDamage = Int(response, CritDamageRatingInt);
        var critResist = Int(response, CritResistRatingInt);
        var critDamageResist = Int(response, CritDamageResistRatingInt);
        var dotResist = Int(response, DotResistRatingInt);
        var lifeResist = Int(response, LifeResistRatingInt);
        if (damage > 0 || crit > 0 || critDamage > 0)
            yield return new AppraisalRow("Dmg/CritDmg", $"Rating: {Invariant(damage)}/{Invariant(critDamage)}");
        if (damageResist > 0 || critResist > 0 || critDamageResist > 0)
            yield return new AppraisalRow("Dmg/CritDmg", $"Resist: {Invariant(damageResist)}/{Invariant(critDamageResist)}");
        if (dotResist > 0 || lifeResist > 0)
            yield return new AppraisalRow("DoT/Life", $"Resist: {Invariant(dotResist)}/{Invariant(lifeResist)}");
    }

    /// <summary>
    /// The character's details in retail's order: the arrival date, the time in Dereth (retail's duration wording, shared with the other
    /// lines' breakdown), chess rank, fishing skill, deaths and titles earned, then retail's footer for the unenchantable marker.
    /// </summary>
    private static IEnumerable<AppraisalRow> CharacterRows(AppraisalResponse response)
    {
        if (PkStatusText(Int(response, PlayerKillerStatusInt)) is { } pk) yield return new AppraisalRow("PK Status", pk);
        if (StringOf(response, DateOfBirthString) is { } arrived) yield return new AppraisalRow("Arrived in Dereth", arrived);
        if (response.Ints.TryGetValue(AgeInt, out var age))
            yield return new AppraisalRow("Time in Dereth", AppraisalOtherLines.Breakdown(Math.Max(0, age), AppraisalOtherLines.CooldownUnits, compact: true));
        if (response.Ints.TryGetValue(ChessRankInt, out var chess)) yield return new AppraisalRow("Chess Rank", Invariant(chess));
        if (response.Ints.TryGetValue(FishingSkillInt, out var fishing)) yield return new AppraisalRow("Fishing Skill", Invariant(fishing));
        if (response.Ints.TryGetValue(DeathsInt, out var deaths)) yield return new AppraisalRow("Deaths", deaths <= 0 ? "Has never died" : Invariant(deaths));
        if (response.Ints.TryGetValue(TitlesEarnedInt, out var titles)) yield return new AppraisalRow("Titles Earned", Invariant(titles));
        yield return new AppraisalRow("* = Unenchantable", string.Empty);
    }

    /// <summary>The server's PlayerKillerStatus (ACE.Entity) in words; a status a character cannot have is left out.</summary>
    private static string? PkStatusText(int status) => status switch
    {
        0x02 => "Non-Player Killer",
        0x04 => "Player Killer",
        0x20 => "Free",
        0x40 => "Player Killer Lite",
        _ => null,
    };

    private static string? StringOf(AppraisalResponse response, uint property) =>
        response.Strings.TryGetValue(property, out var text) && text.Length > 0 ? text : null;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private static int Clamp32(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    /// <summary>The non-empty parts joined by the separator.</summary>
    private static string Join(string separator, params string?[] parts) => string.Join(separator, parts.Where(part => !string.IsNullOrEmpty(part)));

    private sealed record SectionRule(string Heading, Func<AppraisalResponse, IReadOnlyDictionary<uint, SpellInfo>, IEnumerable<AppraisalRow>> Rows);
}
