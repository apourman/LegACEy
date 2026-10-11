using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static System.FormattableString;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Other section: the lines of an appraisal that no other section covers. The rules are in the order the window shows them. First
/// come the properties ACE.MarketApi's appraisal rule table (AppraisalRules.cs) has, in that table's order. Then come the rest of retail's
/// lines, in the order OpenAC's BuildReport builds them (ItemAppraisalTextFormatter, MIT). A rule with a label gives a row with a label
/// and a value. A rule without one gives a sentence across the section. A rule that gives null gives no line.
/// </summary>
internal static class AppraisalOtherLines
{
    // The raw ids this section reads, named as ACE.Entity names them. Each is an AssessmentProperty, so the server sends it on appraisal.
    // PropertyInt: MaterialType 131, EquipmentSetId 265, NumTimesTinkered 171, NumItemsInMaterial 170 (salvaged items), ImbuedEffect 179,
    // ImbuedEffect2-5 303-306, GearDamage 370 to GearLifeResist 378 (the ratings), GearMaxHealth 379 (vitality), Unique 279 (carry limit),
    // CooldownDuration is a float, Cleaving 292, SlayerCreatureType 166, AttackType 47 (multi-strike bits), ResistMagic 36 (unenchantable),
    // Bonded 33, Attuned 114, ResistanceModifierType 263, MinLevel 86, MaxLevel 87, PortalBitmask 111, GemCount 177, GemType 178,
    // ItemCurMana 107 (stored mana), ResistLockpick 38, AppraisalLockpickSuccessPercent 173, AppraisalPages 174, AppraisalMaxPages 175,
    // Structure 92 (uses left), NumKeys 193, AllowedWielder 85 and AllowedActivator 94 are bools, CreationTimestamp 98, Lifespan 267,
    // RemainingLifespan 268, RareId 17, BoosterEnum 89, BoostValue 90, ItemMaxLevel 319, ItemXpStyle 320, CloakWeaveProc 352.
    private const uint MaterialTypeInt = 131, EquipmentSetInt = 265, NumTimesTinkeredInt = 171, NumItemsInMaterialInt = 170, VitalityInt = 379;
    private const uint ImbuedEffectInt = 179, UniqueInt = 279, CleavingInt = 292, SlayerInt = 166, AttackTypeInt = 47, ResistMagicInt = 36;
    private const uint BondedInt = 33, AttunedInt = 114, ResistanceTypeInt = 263, MinLevelInt = 86, MaxLevelInt = 87, PortalBitmaskInt = 111;
    private const uint GemCountInt = 177, GemTypeInt = 178, StoredManaInt = 107, ResistLockpickInt = 38, LockpickChanceInt = 173;
    private const uint PagesUsedInt = 174, MaxPagesInt = 175, StructureInt = 92, NumKeysInt = 193, CreationTimestampInt = 98;
    private const uint LifespanInt = 267, RemainingLifespanInt = 268, RareIdInt = 17, BoosterInt = 89, BoostValueInt = 90;
    private const uint ItemMaxLevelInt = 319, ItemXpStyleInt = 320, CloakWeaveProcInt = 352;
    private const uint ItemTotalXpInt64 = 4, ItemBaseXpInt64 = 5;
    // PropertyFloat: CooldownDuration 167, CriticalMultiplier 136 (crushing blow), CriticalFrequency 147 (biting strike), IgnoreArmor 155,
    // ResistanceModifier 157, AbsorbMagicDamage 159, HealkitMod 100, ItemEfficiency 87, ManaStoneDestroyChance 137.
    private const uint CooldownFloat = 167, CrushingFloat = 136, BitingFloat = 147, ArmorCleaveFloat = 155, ResistCleaveFloat = 157;
    private const uint AbsorbFloat = 159, HealkitModFloat = 100, ItemEfficiencyFloat = 87, DestroyChanceFloat = 137;
    // PropertyBool: Dyable 100, Retained 91, Ivoryable 99, IsSellable 69, Locked 3, UnlimitedUse 63, RareUsesTimer 108, AutowieldLeft 130.
    private const uint DyableBool = 100, RetainedBool = 91, IvoryableBool = 99, IsSellableBool = 69, LockedBool = 3, UnlimitedUseBool = 63;
    private const uint RareUsesTimerBool = 108, AutowieldLeftBool = 130, AllowedWielderBool = 85, AllowedActivatorBool = 94;
    // PropertyString: AppraisalPortalDestination 38, TinkerName 39, ImbuerName 40, CraftsmanName 25. PropertyDataId: ProcSpell 55.
    private const uint DestinationString = 38, TinkererString = 39, ImbuerString = 40, CraftsmanString = 25, ProcSpellDid = 55;

    /// <summary>The imbue ints the server sends; their bits are ORed together.</summary>
    private static readonly uint[] ImbueIntIds = { ImbuedEffectInt, 303, 304, 305, 306 };

    /// <summary>One line of the section. See the class summary for what a label and a null mean.</summary>
    private sealed record OtherRule(string? Label, Func<AppraisalResponse, string?> Text);

    /// <summary>The rules, in the order the section shows them (see the class summary).</summary>
    private static readonly OtherRule[] Rules =
    {
        // ---- Market's order: the properties its rule table has.
        new("Material", Material),
        new("Set", r => IntOf(r, EquipmentSetInt) is int setId ? SetName(setId) : null),
        new(null, Tinkered),
        new("Imbued Effect", ImbueText),
        new(null, r => StringOf(r, ImbuerString) is string imbuer ? Invariant($"Imbued by {imbuer}.") : null),
        new("Gems", Gems),
        new(null, r => BoolOf(r, DyableBool) == true ? "Dyeable" : null),
        new(null, r => Bond(IntOf(r, BondedInt))),
        new(null, r => IntOf(r, AttunedInt) is 1 or 2 ? "Attuned" : null),
        new(null, r => BoolOf(r, IsSellableBool) == false ? "This item cannot be sold." : null),

        // ---- Retail's remaining lines, in BuildReport order: tinkering, set and ratings, special properties, level, usage, boosts,
        // locks, mana stone, remaining uses, creator, rare, then the description's lifespan and portal restrictions.
        new(null, r => StringOf(r, TinkererString) is string tinkerer ? Invariant($"Last tinkered by {tinkerer}.") : null),
        new(null, r => IntOf(r, NumItemsInMaterialInt) is int salvaged && salvaged > 0 ? Invariant($"Salvaged from {salvaged} items.") : null),
        new("Ratings", Ratings),
        new(null, r => IntOf(r, VitalityInt) is int vitality && vitality > 0 ? Invariant($"This item adds {vitality} Vitality.") : null),
        new(null, r => IntOf(r, UniqueInt) is int carry && carry > 0 ? Invariant($"You can only carry {carry} of these items.") : null),
        new("Cooldown When Used", r => FloatOf(r, CooldownFloat) is double seconds && seconds > 0 ? FormatCooldown(seconds) : null),
        new(null, r => IntOf(r, CleavingInt) is int cleave && cleave > 1 ? Invariant($"Cleave: {cleave} enemies in front arc.") : null),
        new("Properties", PropertiesRemaining),
        new(null, r => ImbuedMask(r) != 0 ? "This item cannot be further imbued." : null),
        new(null, r => BoolOf(r, AutowieldLeftBool) == true ? "This item is tethered to the left side." : null),
        new(null, r => IntOf(r, CloakWeaveProcInt) == 2 ? "This cloak has a chance to reduce an incoming attack by 200 damage." : null),
        new("Item Level", r => ItemXpOf(r) is { } xp ? xp.Level.ToString(CultureInfo.InvariantCulture) + " / " + xp.Maximum.ToString(CultureInfo.InvariantCulture) : null),
        new("Item XP", r => ItemXpOf(r) is { } xp ? xp.Experience.ToString("N0", CultureInfo.InvariantCulture) + " / " + xp.Next.ToString("N0", CultureInfo.InvariantCulture) : null),
        new(null, Level),
        new("Destination", r => StringOf(r, DestinationString)),
        new(null, Booster),
        new("Bonus to Healing Skill", r => FloatOf(r, HealkitModFloat) != null ? IntOf(r, BoostValueInt)?.ToString(CultureInfo.InvariantCulture) : null),
        new("Restoration Bonus", r => FloatOf(r, HealkitModFloat) is double kit ? Percent(kit) : null),
        new(null, Pages),
        new(null, r => BoolOf(r, LockedBool) switch { true => "Locked", false => "Unlocked", _ => null }),
        new(null, LockDifficulty),
        new(null, r => BoolOf(r, LockedBool) == true && IntOf(r, ResistLockpickInt) is null ? "You can't tell how hard the lock is to pick." : null),
        new("Bonus to Lockpick Skill", r => BoolOf(r, LockedBool) is null && IntOf(r, ResistLockpickInt) is int bonus && bonus != 0
            ? bonus.ToString("+0;-0;0", CultureInfo.InvariantCulture)
            : null),
        new("Stored Mana", r => NoSpellBook(r) && IntOf(r, StoredManaInt) is int mana ? mana.ToString(CultureInfo.InvariantCulture) : null),
        new("Efficiency", r => NoSpellBook(r) && FloatOf(r, ItemEfficiencyFloat) is double efficiency ? Percent(efficiency) : null),
        new("Chance of Destruction", r => NoSpellBook(r) && FloatOf(r, DestroyChanceFloat) is double destroy ? Percent(destroy) : null),
        new(null, r => IntOf(r, NumKeysInt) is int keys ? Invariant($"Contains {keys} {(keys == 1 ? "key" : "keys")}.") : null),
        new("Number of uses remaining", Uses),
        new(null, Creator),
        new(null, r => IntOf(r, RareIdInt) is int rare && rare > 0 ? Invariant($"Rare #{rare}") : null),
        new(null, r => BoolOf(r, RareUsesTimerBool) == true
            ? "This rare item has a timer restriction of 3 minutes. You will not be able to use another rare item with a timer within 3 minutes of using this one."
            : null),
        new(null, Lifespan),
        new(null, Flag(PortalBitmaskInt, 0x2, "Player Killers may not use this portal.")),
        new(null, Flag(PortalBitmaskInt, 0x4, "Lite Player Killers may not use this portal.")),
        new(null, Flag(PortalBitmaskInt, 0x8, "Non-Player Killers may not use this portal.")),
        new(null, Flag(PortalBitmaskInt, 0x10, "This portal cannot be summoned.")),
        new(null, Flag(PortalBitmaskInt, 0x20, "This portal cannot be recalled nor linked to.")),
    };

    /// <summary>The Other section's rows: each rule that gives text, in rule order.</summary>
    public static IEnumerable<AppraisalRow> Rows(AppraisalResponse response)
    {
        foreach (var rule in Rules)
        {
            if (rule.Text(response) is string text) yield return new AppraisalRow(rule.Label, text);
        }
    }

    private static int? IntOf(AppraisalResponse response, uint property) => response.Ints.TryGetValue(property, out var value) ? value : null;

    private static double? FloatOf(AppraisalResponse response, uint property) => response.Floats.TryGetValue(property, out var value) ? value : null;

    private static bool? BoolOf(AppraisalResponse response, uint property) => response.Bools.TryGetValue(property, out var value) ? value : null;

    private static string? StringOf(AppraisalResponse response, uint property) =>
        response.Strings.TryGetValue(property, out var value) && value.Length > 0 ? value : null;

    private static bool NoSpellBook(AppraisalResponse response) => response.SpellBook.Count == 0;

    /// <summary>A rule that gives its sentence when the int has the bit set.</summary>
    private static Func<AppraisalResponse, string?> Flag(uint property, int bit, string sentence) =>
        response => IntOf(response, property) is int value && (value & bit) != 0 ? sentence : null;

    /// <summary>The material's name, from the material table. Nothing for no material or a value the table does not have.</summary>
    private static string? Material(AppraisalResponse response) => MaterialName(IntOf(response, MaterialTypeInt));

    private static string? MaterialName(int? value) =>
        value is int v && v > 0 && v < AppraisalNameTables.Materials.Length && AppraisalNameTables.Materials[v].Length > 0
            ? AppraisalNameTables.Materials[v]
            : null;

    /// <summary>"3 Smoky Quartz": the gem count and the gem's material, when the server sent both.</summary>
    private static string? Gems(AppraisalResponse response) =>
        IntOf(response, GemCountInt) is int count && count > 0 && MaterialName(IntOf(response, GemTypeInt)) is string gem
            ? Invariant($"{count} {gem}")
            : null;

    /// <summary>"This item has been tinkered 2 times." Shown when the count is above zero.</summary>
    private static string? Tinkered(AppraisalResponse response) => IntOf(response, NumTimesTinkeredInt) is int times && times > 0
        ? Invariant($"This item has been tinkered {times} {(times == 1 ? "time" : "times")}.")
        : null;

    /// <summary>
    /// The imbue names, in retail's order, joined. Retail names the imbue bits inside its Properties line; the Market table has them as
    /// their own line, so this line takes the Market label and retail's names.
    /// </summary>
    private static string? ImbueText(AppraisalResponse response)
    {
        uint imbued = ImbuedMask(response);
        var names = ImbueNames.Where(name => (imbued & name.Flag) != 0).Select(name => name.Name).ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

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
            .Select(rating => Invariant($"{rating.Label} {response.Ints[rating.Property]}"))
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// The Properties line for what the Market table does not have: Bael'Zharon's Hate and other slayers, multi-strike, magic absorbing,
    /// unenchantable, retained, the strike and cleave effects, cast on strike and ivoryable. Retail's order.
    /// </summary>
    private static string? PropertiesRemaining(AppraisalResponse response)
    {
        var list = new List<string>();
        if (IntOf(response, SlayerInt) is int slayer && slayer == 31) list.Add("Bael'Zharon's Hate");
        else if (IntOf(response, SlayerInt) is int creature && creature > 0 && creature < AppraisalNameTables.Creatures.Length)
            list.Add(Invariant($"{AppraisalNameTables.Creatures[creature]} slayer"));
        if (((IntOf(response, AttackTypeInt) ?? 0) & 0x79E0) != 0) list.Add("Multi-Strike");
        if (FloatOf(response, AbsorbFloat) != null) list.Add("Magic Absorbing");
        if (IntOf(response, ResistMagicInt) is int magic && magic >= 9_999) list.Add("Unenchantable");
        if (BoolOf(response, RetainedBool) == true) list.Add("Retained");
        if (FloatOf(response, CrushingFloat) != null) list.Add("Crushing Blow");
        if (FloatOf(response, BitingFloat) != null) list.Add("Biting Strike");
        if (FloatOf(response, ArmorCleaveFloat) != null) list.Add("Armor Cleaving");
        if (FloatOf(response, ResistCleaveFloat) != null && IntOf(response, ResistanceTypeInt) is int type)
            list.Add(Invariant($"Resistance Cleaving: {DamageName(type)}"));
        if (response.Dids.ContainsKey(ProcSpellDid)) list.Add("Cast on Strike");
        if (BoolOf(response, IvoryableBool) == true) list.Add("Ivoryable");
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    /// <summary>The damage type a resistance cleaving names, from retail's damage type bits.</summary>
    private static string DamageName(int type) => type switch
    {
        1 => "Slashing", 2 => "Piercing", 4 => "Bludgeoning", 8 => "Cold", 16 => "Fire", 32 => "Acid", 64 => "Electric", 128 => "Health",
        256 => "Stamina", 512 => "Mana", 1024 => "Nether",
        _ => Invariant($"type {type}"),
    };

    /// <summary>Retail's wording for the bonded state, or nothing when the item is not bonded.</summary>
    private static string? Bond(int? status) => status switch
    {
        -2 => "Destroyed on Death",
        -1 => "Dropped on Death",
        1 => "Bonded",
        _ => null,
    };

    /// <summary>"Restricted to characters of Level 50 or greater." The level restriction as retail words it.</summary>
    private static string? Level(AppraisalResponse response)
    {
        int minimum = IntOf(response, MinLevelInt) ?? 0;
        int maximum = IntOf(response, MaxLevelInt) ?? 0;
        if (minimum > 0 && maximum > 0)
            return minimum == maximum
                ? Invariant($"Restricted to characters of Level {minimum}.")
                : Invariant($"Restricted to characters of Levels {minimum} to {maximum}.");
        if (minimum > 0) return Invariant($"Restricted to characters of Level {minimum} or greater.");
        return maximum > 0 ? Invariant($"Restricted to characters of Level {maximum} or below.") : null;
    }

    /// <summary>The item's level and experience, from the curve in its style. Retail's ShowItemLevel.</summary>
    private sealed record ItemXp(int Level, int Maximum, long Experience, long Next);

    private static ItemXp? ItemXpOf(AppraisalResponse response)
    {
        if (!response.Int64s.TryGetValue(ItemBaseXpInt64, out long baseExperience) || baseExperience <= 0
            || IntOf(response, ItemMaxLevelInt) is not int maximum || maximum <= 0
            || IntOf(response, ItemXpStyleInt) is not int style || style <= 0)
            return null;

        long experience = Math.Max(0, response.Int64s.TryGetValue(ItemTotalXpInt64, out long total) ? total : 0);
        int currentLevel = TotalXpToLevel(experience, baseExperience, maximum, style);
        int displayed = Math.Min(currentLevel + 1, maximum);
        return new ItemXp(displayed, maximum, experience, LevelToTotalXp(displayed, baseExperience, maximum, style));
    }

    /// <summary>The total experience a level takes, in the item's curve style (1 linear, 2 doubling, 3 rising by level).</summary>
    private static long LevelToTotalXp(int level, long baseExperience, int maximumLevel, int style)
    {
        int capped = Math.Min(Math.Max(level, 0), maximumLevel);
        switch (style)
        {
            case 1: return baseExperience * capped;
            case 2:
            {
                long total = 0, cost = baseExperience;
                for (int i = 0; i < capped; i++)
                {
                    total += cost;
                    cost *= 2L;
                }
                return total;
            }
            case 3: return baseExperience * capped * (capped + 1L) / 2L;
            default: return 0L;
        }
    }

    /// <summary>The level a total experience reaches, in the item's curve style.</summary>
    private static int TotalXpToLevel(long totalExperience, long baseExperience, int maximumLevel, int style)
    {
        if (totalExperience <= 0 || baseExperience <= 0 || maximumLevel <= 0) return 0;
        if (style == 1) return (int)Math.Min(maximumLevel, totalExperience / baseExperience);

        long remaining = totalExperience, nextCost = baseExperience;
        int level = 0;
        while (level < maximumLevel && remaining >= nextCost)
        {
            remaining -= nextCost;
            level++;
            if (style == 2) nextCost *= 2L;
            else if (style == 3) nextCost = baseExperience * (level + 1L);
            else return 0;
        }
        return level;
    }

    /// <summary>The pages a book has and how many are full. Retail's ShowCapacityAndLock.</summary>
    private static string? Pages(AppraisalResponse response) =>
        IntOf(response, MaxPagesInt) is int pages && pages > 0
            ? Invariant($"{IntOf(response, PagesUsedInt) ?? 0} of {pages} pages full.")
            : null;

    /// <summary>
    /// The use line for a booster (Health, Stamina or Mana). A healing kit has the healing modifier and gets its own lines, so it has none
    /// of this.
    /// </summary>
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

    /// <summary>"Restores 40 Health when used." A negative amount depletes.</summary>
    private static string Boost(int amount, string resource, string when) =>
        Invariant($"{(amount >= 0 ? "Restores" : "Depletes")} {Math.Abs((long)amount)} {resource} when {when}.");

    /// <summary>"The lock looks quite difficult to pick (Resistance 30)." Only for a locked item the server sent a chance for.</summary>
    private static string? LockDifficulty(AppraisalResponse response)
    {
        if (BoolOf(response, LockedBool) != true || IntOf(response, ResistLockpickInt) is not int resistance) return null;
        if (IntOf(response, LockpickChanceInt) is not int chance) return null;
        string? difficulty = LockpickWords(chance);
        return difficulty == null ? null : Invariant($"The lock looks {difficulty} to pick (Resistance {resistance}).");
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

    /// <summary>"Unlimited" when the item has no limit, otherwise the count the server sent.</summary>
    private static string? Uses(AppraisalResponse response) =>
        BoolOf(response, UnlimitedUseBool) == true ? "Unlimited" : IntOf(response, StructureInt)?.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Created by Ann." Hidden when the item is bound to a wielder or an activator, as retail hides it.</summary>
    private static string? Creator(AppraisalResponse response) =>
        StringOf(response, CraftsmanString) is string creator && BoolOf(response, AllowedWielderBool) != true
        && BoolOf(response, AllowedActivatorBool) != true
            ? Invariant($"Created by {creator}.")
            : null;

    /// <summary>
    /// The lifespan: the remaining time and whether it is disintegrating. Retail shows it only when the creation timestamp is sent too.
    /// </summary>
    private static string? Lifespan(AppraisalResponse response)
    {
        if (!response.Ints.ContainsKey(LifespanInt) || !response.Ints.ContainsKey(CreationTimestampInt)
            || IntOf(response, RemainingLifespanInt) is not int remaining)
            return null;
        return remaining < 0 ? "This item is in the act of disintegrating." : "This item expires in " + FormatExpiry(remaining);
    }

    private static readonly (int Size, string Unit)[] ExpiryUnits = { (31_536_000, "years"), (86_400, "days"), (3_600, "hours"), (60, "minutes") };

    internal static readonly (int Size, string Unit)[] CooldownUnits = { (2_592_000, "mo"), (86_400, "d"), (3_600, "h"), (60, "m") };

    /// <summary>"2 days, 3 hours, 4 seconds." as retail words the time left.</summary>
    private static string FormatExpiry(int remainingSeconds) => Breakdown(remainingSeconds, ExpiryUnits, compact: false);

    /// <summary>"1h 2m 3s": the cooldown in retail's short form.</summary>
    private static string FormatCooldown(double seconds) => Breakdown((int)Math.Truncate(seconds), CooldownUnits, compact: true);

    /// <summary>
    /// Splits a count of seconds into units, largest first, leaving out the units that are zero, with the seconds last. Retail's two
    /// wordings differ only in the layout: compact ("1h 2m 3s") or long ("1 hours, 2 minutes, 3 seconds.").
    /// </summary>
    internal static string Breakdown(int seconds, (int Size, string Unit)[] units, bool compact)
    {
        int remaining = Math.Max(0, seconds);
        var parts = new List<string>();
        foreach (var (size, unit) in units)
        {
            int count = remaining / size;
            if (count > 0) parts.Add(compact ? Invariant($"{count}{unit}") : Invariant($"{count} {unit}"));
            remaining %= size;
        }
        parts.Add(compact ? Invariant($"{remaining}s") : Invariant($"{remaining} seconds"));
        return compact ? string.Join(" ", parts) : string.Join(", ", parts) + ".";
    }

    /// <summary>A fraction shown as a percentage with no decimals: 0.5 gives "50%", as retail does.</summary>
    private static string Percent(double fraction) => Invariant($"{fraction * 100:0}%");

    /// <summary>The set's name in retail's words (retail's names, from ItemAppraisalTextFormatter), or null for an unknown set.</summary>
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
}
