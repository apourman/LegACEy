using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>A figure in the summary: a label under a value, such as Value 75.</summary>
public sealed record AppraisalFigure(string Label, string Value);

/// <summary>One line of a section. A line with a label is a label and a value; a line without one is text across the section.</summary>
public sealed record AppraisalRow(string? Label, string Text);

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
    /// left out. Later tickets add Combat, Spells, Requirements and Other here, in that order.
    /// </summary>
    private static readonly SectionRule[] SectionRules =
    {
        new("Use", response => TextRows(response.Strings, UseString)),
        new("Other", OtherRows),
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

    // ---- Other: the lines retail shows that no other section covers. Ids are the server's assessed property ids (ACE.Entity Properties).
    private const uint NumTimesTinkeredInt = 171, EquipmentSetInt = 265, VitalityInt = 379, UniqueInt = 279, CleavingInt = 292,
        SlayerInt = 166, ResistMagicInt = 36, BondedInt = 33, AttunedInt = 114, ResistanceTypeInt = 263, MinLevelInt = 86, MaxLevelInt = 87,
        PortalBitmaskInt = 111, StructureInt = 92, NumKeysInt = 193, RareIdInt = 17, PagesUsedInt = 174, MaxPagesInt = 175,
        LifespanInt = 267, RemainingLifespanInt = 268, BoosterInt = 89, BoostValueInt = 90, ResistLockpickInt = 38, LockpickChanceInt = 173;
    private const uint AbsorbFloat = 159, CooldownFloat = 167, CrushingFloat = 136, BitingFloat = 147, ArmorCleaveFloat = 155,
        ResistCleaveFloat = 157, HealkitModFloat = 100, ItemEfficiencyFloat = 87, DestroyChanceFloat = 137;
    private const uint UnlimitedUseBool = 63, RetainedBool = 91, IvoryableBool = 99, DyableBool = 100, LockedBool = 3, RareUsesTimerBool = 108,
        IsSellableBool = 69;
    private const uint TinkererString = 39, ImbuerString = 40, CraftsmanString = 25, DestinationString = 38, ProcSpellDid = 55;

    /// <summary>The imbue properties: the server sends each of the five imbue ints, and their bits are ORed together.</summary>
    private static readonly uint[] ImbueIntIds = { 179, 303, 304, 305, 306 };

    /// <summary>
    /// One line of the Other section. A label gives a row with a label and a value; no label gives a sentence across the section. A rule
    /// that returns null gives no line.
    /// </summary>
    private sealed record OtherRule(string? Label, Func<AppraisalResponse, string?> Text);

    /// <summary>
    /// The Other section's lines, in the order retail's report builds them: tinkering and salvage, set and ratings, then the properties
    /// retail lists (imbues, Bael'Zharon's Hate, attunement and bonding, cleaving, cast on strike), then cleave, cooldown, carry limit,
    /// level and portal lines, lifespan, uses, keys, creator, rare, pages, use boosts, kit bonuses, locks and mana stone efficiency. The
    /// wording is retail's (ItemAppraisalTextFormatter in OpenAC, AcDream.App/UI/Layout, MIT). Material, gems and creature slayers are not
    /// here: their names come from game data tables the client does not have yet.
    /// </summary>
    private static readonly OtherRule[] OtherRules =
    {
        new(null, Tinkered),
        new(null, r => StringOf(r, TinkererString) is string tinkerer ? $"Last tinkered by {tinkerer}." : null),
        new(null, r => StringOf(r, ImbuerString) is string imbuer ? $"Imbued by {imbuer}." : null),
        new(null, r => IntOf(r, SalvagedItemsInt) is int salvaged && salvaged > 0 ? $"Salvaged from {salvaged} items." : null),
        new("Set", r => IntOf(r, EquipmentSetInt) is int setId ? SetName(setId) : null),
        new("Ratings", Ratings),
        new(null, r => IntOf(r, VitalityInt) is int vitality && vitality > 0 ? $"This item adds {vitality} Vitality." : null),
        new("Properties", PropertiesText),
        new(null, r => ImbuedMask(r) != 0 ? "This item cannot be further imbued." : null),
        new(null, r => IntOf(r, CleavingInt) is int cleave && cleave > 1 ? $"Cleave: {cleave} enemies in front arc." : null),
        new("Cooldown When Used", r => FloatOf(r, CooldownFloat) is double seconds && seconds > 0 ? FormatCooldown(seconds) : null),
        new(null, r => IntOf(r, UniqueInt) is int carry && carry > 0 ? $"You can only carry {carry} of these items." : null),
        new(null, Level),
        new("Destination", r => StringOf(r, DestinationString)),
        new(null, Flag(PortalBitmaskInt, 0x2, "Player Killers may not use this portal.")),
        new(null, Flag(PortalBitmaskInt, 0x4, "Lite Player Killers may not use this portal.")),
        new(null, Flag(PortalBitmaskInt, 0x8, "Non-Player Killers may not use this portal.")),
        new(null, Flag(PortalBitmaskInt, 0x10, "This portal cannot be summoned.")),
        new(null, Flag(PortalBitmaskInt, 0x20, "This portal cannot be recalled nor linked to.")),
        new(null, Lifespan),
        new("Number of uses remaining", Uses),
        new(null, r => IntOf(r, NumKeysInt) is int keys ? $"Contains {keys} {(keys == 1 ? "key" : "keys")}." : null),
        new(null, r => StringOf(r, CraftsmanString) is string creator ? $"Created by {creator}." : null),
        new(null, r => BoolOf(r, IsSellableBool) == false ? "This item cannot be sold." : null),
        new(null, r => IntOf(r, RareIdInt) is int rare && rare > 0 ? $"Rare #{rare}" : null),
        new(null, r => BoolOf(r, RareUsesTimerBool) == true
            ? "This rare item has a timer restriction of 3 minutes. You will not be able to use another rare item with a timer within 3 minutes of using this one."
            : null),
        new(null, r => IntOf(r, MaxPagesInt) is int pages && pages > 0 ? $"{IntOf(r, PagesUsedInt) ?? 0} of {pages} pages full." : null),
        new(null, Booster),
        new("Bonus to Healing Skill", r => FloatOf(r, HealkitModFloat) != null ? IntOf(r, BoostValueInt)?.ToString(CultureInfo.InvariantCulture) : null),
        new("Restoration Bonus", r => FloatOf(r, HealkitModFloat) is double kit ? Percent(kit) : null),
        new(null, r => BoolOf(r, LockedBool) switch { true => "Locked", false => "Unlocked", _ => null }),
        new(null, LockDifficulty),
        new(null, r => BoolOf(r, LockedBool) == true && IntOf(r, ResistLockpickInt) is null ? "You can't tell how hard the lock is to pick." : null),
        new("Bonus to Lockpick Skill", r => BoolOf(r, LockedBool) is null && IntOf(r, ResistLockpickInt) is int bonus && bonus != 0
            ? bonus.ToString("+0;-0;0", CultureInfo.InvariantCulture)
            : null),
        new("Efficiency", r => FloatOf(r, ItemEfficiencyFloat) is double efficiency ? Percent(efficiency) : null),
        new("Chance of Destruction", r => FloatOf(r, DestroyChanceFloat) is double destroy ? Percent(destroy) : null),
    };

    /// <summary>The Other section's rows: each rule that gives text, in rule order.</summary>
    private static IEnumerable<AppraisalRow> OtherRows(AppraisalResponse response)
    {
        foreach (var rule in OtherRules)
        {
            if (rule.Text(response) is string text) yield return new AppraisalRow(rule.Label, text);
        }
    }

    private static int? IntOf(AppraisalResponse response, uint property) => response.Ints.TryGetValue(property, out var value) ? value : null;

    private static double? FloatOf(AppraisalResponse response, uint property) => response.Floats.TryGetValue(property, out var value) ? value : null;

    private static bool? BoolOf(AppraisalResponse response, uint property) => response.Bools.TryGetValue(property, out var value) ? value : null;

    private static string? StringOf(AppraisalResponse response, uint property) =>
        response.Strings.TryGetValue(property, out var value) && value.Length > 0 ? value : null;

    /// <summary>A rule that gives its sentence when the int has the bit set.</summary>
    private static Func<AppraisalResponse, string?> Flag(uint property, int bit, string sentence) =>
        response => IntOf(response, property) is int value && (value & bit) != 0 ? sentence : null;

    /// <summary>"This item has been tinkered 2 times." Retail shows the count only when it is not zero.</summary>
    private static string? Tinkered(AppraisalResponse response) => IntOf(response, NumTimesTinkeredInt) is int times && times > 0
        ? $"This item has been tinkered {times} {(times == 1 ? "time" : "times")}."
        : null;

    /// <summary>"Dam 5, Crit 3". The ratings the server sends that are above zero, in retail's order.</summary>
    private static string? Ratings(AppraisalResponse response)
    {
        (uint Property, string Label)[] ratings =
        {
            (370, "Dam"), (371, "Dam Resist"), (372, "Crit"), (374, "Crit Dam"), (373, "Crit Resist"), (375, "Crit Dam Resist"),
            (376, "Heal Boost"), (377, "Nether Resist"), (378, "Life Resist"),
        };
        var parts = ratings
            .Where(rating => IntOf(response, rating.Property) is int value && value > 0)
            .Select(rating => $"{rating.Label} {response.Ints[rating.Property].ToString(CultureInfo.InvariantCulture)}")
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>The set's name in retail's words (the names are retail's, from ItemAppraisalTextFormatter), or null for an unknown set.</summary>
    private static string? SetName(int id)
    {
        if (id is >= 94 and <= 129)
        {
            string tier = ((id - 94) / 12) switch { 0 => "Minor", 1 => "Major", _ => "Blackfire" };
            int member = (id - 94) % 12;
            string effect = (member % 4) switch { 0 => "Stinging", 1 => "Sparking", 2 => "Smoldering", _ => "Shivering" };
            string soul = (member / 4) switch { 0 => "Shrouded Soul", 1 => "Darkened Mind", _ => "Clouded Spirit" };
            return $"{tier} {effect} {soul}";
        }

        return id switch
        {
            4 => "Carraida's Benediction", 5 => "Noble Relic", 6 => "Ancient Relic", 7 => "Alduressa Relic",
            8 => "Shou-jen", 9 => "Empyrean Rings", 10 => "Arm, Mind, Heart", 11 => "Coat of Perfect Light",
            12 => "Leggings of Perfect Light", 13 => "Soldier's", 14 => "Adept's", 15 => "Archer's",
            16 => "Defender's", 17 => "Tinker's", 18 => "Crafter's", 19 => "Hearty",
            20 => "Dexterous", 21 => "Wise", 22 => "Swift", 23 => "Hardened",
            24 => "Reinforced", 25 => "Interlocking", 26 => "Flame Proof", 27 => "Acid Proof",
            28 => "Cold Proof", 29 => "Lightning Proof", 30 => "Dedication", 31 => "Gladiatorial Clothing",
            32 => "Ceremonial Clothing", 33 => "Protective Clothing", 35 => "Sigil of Defense", 36 => "Sigil of Destruction",
            37 => "Sigil of Fury", 38 => "Sigil of Growth", 39 => "Sigil of Vigor", 40 => "Heroic Protector",
            41 => "Heroic Destroyer", 49 => "Weave of Alchemy", 50 => "Weave of Arcane Lore", 51 => "Weave of Armor Tinkering",
            52 => "Weave of Assess Person", 53 or 67 or 74 or 75 or 79 => "Weave of Light Weapons", 54 or 57 or 77 => "Weave of Missile Weapons",
            55 => "Weave of Cooking", 56 => "Weave of Creature Enchantment", 58 => "Weave of Finesse Weapons", 59 => "Weave of Deception",
            60 => "Weave of Fletching", 61 => "Weave of Healing", 62 => "Weave of Item Enchantment", 63 => "Weave of Item Tinkering",
            64 => "Weave of Leadership", 65 => "Weave of Life Magic", 66 => "Weave of Loyalty", 68 => "Weave of Magic Defense",
            69 => "Weave of Magic Item Tinkering", 70 => "Weave of Mana Conversion", 71 => "Weave of Melee Defense", 72 => "Weave of Missile Defense",
            73 => "Weave of Salvaging", 76 => "Weave of Heavy Weapons", 78 => "Weave of Two Handed Combat", 80 => "Weave of Void Magic",
            81 => "Weave of War Magic", 82 => "Weave of Weapon Tinkering", 83 => "Weave of Assess Creature", 84 => "Weave of Dirty Fighting",
            85 => "Weave of Dual Wield", 86 => "Weave of Recklessness", 87 => "Weave of Shield", 88 => "Weave of Sneak Attack",
            89 => "Shou-jen Shozoku", 90 => "Weave of Summoning", 91 => "Shrouded Soul", 92 => "Darkened Mind",
            93 => "Clouded Spirit", 130 => "Shimmering Shadows", _ => null,
        };
    }

    /// <summary>The imbue bits the server sends, as one mask.</summary>
    private static uint ImbuedMask(AppraisalResponse response) =>
        ImbueIntIds.Aggregate(0u, (mask, property) => mask | unchecked((uint)(IntOf(response, property) ?? 0)));

    /// <summary>Retail's names for the imbue bits, in retail's order.</summary>
    private static readonly (uint Flag, string Name)[] ImbueNames =
    {
        (0x1, "Critical Strike"), (0x2, "Crippling Blow"), (0x4, "Armor Rending"), (0x8, "Slash Rending"), (0x10, "Pierce Rending"),
        (0x20, "Bludgeon Rending"), (0x40, "Acid Rending"), (0x80, "Cold Rending"), (0x100, "Lightning Rending"), (0x200, "Fire Rending"),
        (0x400, "+1 Melee Defense"), (0x800, "+1 Missile Defense"), (0x1000, "+1 Magic Defense"), (0x4000, "Nether Rending"),
        (0x80000000, "Phantasmal"),
    };

    /// <summary>
    /// The Properties line: the imbues, Bael'Zharon's Hate (the one slayer whose name needs no creature table), magic absorbing,
    /// unenchantable, attuned, bonded, retained, the strike and cleave effects, cast on strike, ivoryable and dyeable. Retail's order.
    /// </summary>
    private static string? PropertiesText(AppraisalResponse response)
    {
        var list = new List<string>();
        uint imbued = ImbuedMask(response);
        list.AddRange(ImbueNames.Where(name => (imbued & name.Flag) != 0).Select(name => name.Name));
        if (IntOf(response, SlayerInt) == 31) list.Add("Bael'Zharon's Hate");
        if (FloatOf(response, AbsorbFloat) != null) list.Add("Magic Absorbing");
        if (IntOf(response, ResistMagicInt) is int magic && magic >= 9_999) list.Add("Unenchantable");
        if (IntOf(response, AttunedInt) is 1 or 2) list.Add("Attuned");
        if (Bond(IntOf(response, BondedInt)) is string bond) list.Add(bond);
        if (BoolOf(response, RetainedBool) == true) list.Add("Retained");
        if (FloatOf(response, CrushingFloat) != null) list.Add("Crushing Blow");
        if (FloatOf(response, BitingFloat) != null) list.Add("Biting Strike");
        if (FloatOf(response, ArmorCleaveFloat) != null) list.Add("Armor Cleaving");
        if (FloatOf(response, ResistCleaveFloat) != null && IntOf(response, ResistanceTypeInt) is int type)
            list.Add($"Resistance Cleaving: {DamageName(type)}");
        if (response.Dids.ContainsKey(ProcSpellDid)) list.Add("Cast on Strike");
        if (BoolOf(response, IvoryableBool) == true) list.Add("Ivoryable");
        if (BoolOf(response, DyableBool) == true) list.Add("Dyeable");
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    private static string? Bond(int? status) => status switch
    {
        -2 => "Destroyed on Death",
        -1 => "Dropped on Death",
        1 => "Bonded",
        _ => null,
    };

    /// <summary>The damage type the resistance cleaving names, from retail's damage type bits.</summary>
    private static string DamageName(int type) => type switch
    {
        1 => "Slashing", 2 => "Piercing", 4 => "Bludgeoning", 8 => "Cold", 16 => "Fire", 32 => "Acid", 64 => "Electric", 1024 => "Nether",
        _ => $"type {type.ToString(CultureInfo.InvariantCulture)}",
    };

    /// <summary>"Restricted to characters of Level 50 or greater." The level restriction as retail words it.</summary>
    private static string? Level(AppraisalResponse response)
    {
        int minimum = IntOf(response, MinLevelInt) ?? 0;
        int maximum = IntOf(response, MaxLevelInt) ?? 0;
        if (minimum > 0 && maximum > 0)
            return minimum == maximum
                ? $"Restricted to characters of Level {minimum}."
                : $"Restricted to characters of Levels {minimum} to {maximum}.";
        if (minimum > 0) return $"Restricted to characters of Level {minimum} or greater.";
        return maximum > 0 ? $"Restricted to characters of Level {maximum} or below." : null;
    }

    /// <summary>The lifespan line: the server sends the remaining lifespan whenever the item has one.</summary>
    private static string? Lifespan(AppraisalResponse response)
    {
        if (!response.Ints.ContainsKey(LifespanInt) || IntOf(response, RemainingLifespanInt) is not int remaining) return null;
        return remaining < 0 ? "This item is in the act of disintegrating." : $"This item expires in {FormatExpiry(remaining)}";
    }

    private static readonly (int Size, string Unit)[] ExpiryUnits = { (31_536_000, "years"), (86_400, "days"), (3_600, "hours"), (60, "minutes") };

    /// <summary>"2 days, 3 hours, 4 seconds." as retail words the time left.</summary>
    private static string FormatExpiry(int remainingSeconds)
    {
        int remaining = Math.Max(0, remainingSeconds);
        var parts = new List<string>();
        foreach (var (size, unit) in ExpiryUnits)
        {
            if (remaining <= size) continue;
            parts.Add($"{remaining / size} {unit}");
            remaining %= size;
        }
        parts.Add($"{remaining} seconds");
        return string.Join(", ", parts) + ".";
    }

    private static readonly (int Size, string Unit)[] CooldownUnits = { (2_592_000, "mo"), (86_400, "d"), (3_600, "h"), (60, "m") };

    /// <summary>"1h 2m 3s", the cooldown in retail's short form.</summary>
    private static string FormatCooldown(double seconds)
    {
        int remaining = Math.Max(0, (int)Math.Truncate(seconds));
        var parts = new List<string>();
        foreach (var (size, unit) in CooldownUnits)
        {
            if (remaining / size > 0) parts.Add($"{remaining / size}{unit}");
            remaining %= size;
        }
        parts.Add($"{remaining}s");
        return string.Join(" ", parts);
    }

    /// <summary>The uses left: "Unlimited" when the item has no limit, otherwise the count the server sent.</summary>
    private static string? Uses(AppraisalResponse response) =>
        BoolOf(response, UnlimitedUseBool) == true ? "Unlimited" : IntOf(response, StructureInt)?.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Restores 40 Health when used." A healing kit also has the healing modifier, and its own lines take the place of this one.</summary>
    private static string? Booster(AppraisalResponse response)
    {
        if (FloatOf(response, HealkitModFloat) != null || IntOf(response, BoosterInt) is not int booster) return null;
        if (IntOf(response, BoostValueInt) is not int amount) return null;
        return booster switch
        {
            2 => Boost(amount, "Health", "used"),
            4 => Boost(amount, "Stamina", "consumed"),
            6 => Boost(amount, "Mana", "used"),
            _ => null,
        };
    }

    private static string Boost(int amount, string resource, string when) =>
        $"{(amount >= 0 ? "Restores" : "Depletes")} {Math.Abs(amount).ToString(CultureInfo.InvariantCulture)} {resource} when {when}.";

    /// <summary>"The lock looks quite difficult to pick (Resistance 30)." Only for a locked item the server sent a chance for.</summary>
    private static string? LockDifficulty(AppraisalResponse response)
    {
        if (BoolOf(response, LockedBool) != true || IntOf(response, ResistLockpickInt) is not int resistance) return null;
        if (IntOf(response, LockpickChanceInt) is not int chance) return null;
        string? difficulty = LockpickWords(chance);
        return difficulty == null ? null : $"The lock looks {difficulty} to pick (Resistance {resistance.ToString(CultureInfo.InvariantCulture)}).";
    }

    private static string? LockpickWords(int successPercent) => successPercent switch
    {
        < 0 => null,
        0 => "impossible",
        < 5 => "ridiculously difficult",
        < 15 => "extremely difficult",
        < 35 => "quite difficult",
        < 50 => "difficult",
        < 70 => "challenging",
        < 85 => "mildly challenging",
        < 95 => "easy",
        _ => "trivial",
    };

    /// <summary>A fraction shown as a percentage with no decimals: 0.5 gives "50%", as retail does.</summary>
    private static string Percent(double fraction) => (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private sealed record SectionRule(string Heading, Func<AppraisalResponse, IEnumerable<AppraisalRow>> Rows);
}
