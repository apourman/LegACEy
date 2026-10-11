using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>A figure in the summary: a label under a value, such as Value 75.</summary>
public sealed record AppraisalFigure(string Label, string Value);

/// <summary>One line of a section. A line with a label is a label and a value; a line without one is text across the section.</summary>
public record AppraisalRow(string? Label, string Text);

/// <summary>A spell: its name, with its description under it.</summary>
public sealed record AppraisalSpellRow(string Name, string Description) : AppraisalRow(Name, Description);

/// <summary>A meter filled to Current of Maximum, labelled, with an optional note under it such as a cost.</summary>
public sealed record AppraisalMeterRow(string Name, int Current, int Maximum, string? Note)
    : AppraisalRow(Name, string.Format(CultureInfo.InvariantCulture, "{0:N0} / {1:N0}", Current, Maximum));

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
    /// left out. Later tickets add Combat and Other here, in that order.
    /// </summary>
    private static readonly SectionRule[] SectionRules =
    {
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

    /// <summary>
    /// The type line: the item's type, then its weapon skill when it has one ("Melee weapon · Heavy Weapons"). Skill names are ACE's
    /// Skill.ToSentence (ACE.Entity/Enum/Skill.cs), indexed by the skill's id.
    /// </summary>
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

    private static string TypeLineOf(AppraisalResponse response)
    {
        var type = response.Ints.TryGetValue(ItemTypeInt, out var bits) ? ItemTypeNames.FirstOrDefault(item => (bits & item.Bit) != 0).Name : null;
        var skill = response.Ints.TryGetValue(WeaponSkillInt, out var id) && id > 0 && id < SkillNames.Length ? SkillNames[id] : null;
        return string.Join(" · ", new[] { type, skill }.Where(part => !string.IsNullOrEmpty(part)));
    }

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
