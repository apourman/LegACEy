using System;
using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

public enum StatKind { Attribute, Vital, Skill }

/// <summary>A value as the player sees it: <see cref="Base"/> without buffs, <see cref="Buffed"/> with them.</summary>
public readonly record struct StatValue(int Base, int Buffed);

/// <summary>The character window's groups, in the order it lists them.</summary>
public enum CharacterGroup { Attributes, Vitals, Specialized, Trained, Untrained, Unusable }

/// <summary>
/// One row of the character window. <see cref="NextCost"/> and <see cref="TenCost"/> are the experience the next rank and the next ten
/// ranks cost (fewer at the top), null when the row cannot be raised with experience or is at its last rank. <see cref="TrainCost"/> is
/// the credits training it costs, 0 when it cannot be trained.
/// </summary>
public sealed record CharacterRow(StatKind Kind, uint Id, string Name, CharacterGroup Group, uint IconId, StatValue Value, int Current,
    uint? NextCost, uint? TenCost, int TrainCost, string Description, string Formula)
{
    public bool AtMax => NextCost == null && Group is not (CharacterGroup.Untrained or CharacterGroup.Unusable);
}

/// <summary>Builds the character window's rows from the server's sheet, the portal's tables and the values the game reports.</summary>
public static class CharacterRows
{
    private static readonly string[] AttributeNames = { "", "Strength", "Endurance", "Quickness", "Coordination", "Focus", "Self" };
    // Retail's order: Strength, Endurance, Coordination, Quickness, Focus, Self.
    private static readonly uint[] AttributeOrder = { 1, 2, 4, 3, 5, 6 };
    private static readonly (uint Id, string Name)[] VitalOrder = { (1, "Health"), (3, "Stamina"), (5, "Mana") };

    public static IReadOnlyList<CharacterRow> Attributes(CharacterSheet sheet, CharacterTables tables, Func<StatKind, uint, StatValue?> value,
        Func<uint, int> current)
    {
        var rows = new List<CharacterRow>();
        foreach (var id in AttributeOrder)
        {
            var advancement = sheet.Attributes.TryGetValue(id, out var a) ? a : default;
            var shown = Ahead(value(StatKind.Attribute, id) ?? new StatValue((int)(advancement.Start + advancement.Ranks), (int)(advancement.Start + advancement.Ranks)),
                sheet.Unconfirmed(StatKind.Attribute, id));
            var (next, ten) = Costs(tables.Experience.Attribute, advancement);
            rows.Add(new CharacterRow(StatKind.Attribute, id, AttributeNames[id], CharacterGroup.Attributes, Icon(tables.AttributeIcons, id), shown, 0,
                next, ten, 0, string.Empty, string.Empty));
        }
        foreach (var (id, name) in VitalOrder)
        {
            var advancement = sheet.Vitals.TryGetValue(id, out var a) ? a : default;
            var shown = Ahead(value(StatKind.Vital, id) ?? new StatValue((int)advancement.Ranks, (int)advancement.Ranks), sheet.Unconfirmed(StatKind.Vital, id));
            var (next, ten) = Costs(tables.Experience.Vital, advancement);
            rows.Add(new CharacterRow(StatKind.Vital, id, name, CharacterGroup.Vitals, Icon(tables.VitalIcons, id), shown, current(id),
                next, ten, 0, string.Empty, string.Empty));
        }
        return rows;
    }

    /// <summary>The skills the portal names, grouped as retail groups them and sorted by name within each group.</summary>
    public static IReadOnlyList<CharacterRow> Skills(CharacterSheet sheet, CharacterTables tables, Func<StatKind, uint, StatValue?> value)
    {
        var rows = new List<CharacterRow>();
        foreach (var skill in tables.Skills.Values)
        {
            var known = sheet.Skills.TryGetValue(skill.Id, out var s);
            var training = known ? s.Training : SkillTraining.Untrained;
            var group = training switch
            {
                SkillTraining.Specialized => CharacterGroup.Specialized,
                SkillTraining.Trained => CharacterGroup.Trained,
                _ => skill.MinLevel >= 2 ? CharacterGroup.Unusable : CharacterGroup.Untrained
            };
            var shown = Ahead(value(StatKind.Skill, skill.Id) ?? new StatValue(0, 0), sheet.Unconfirmed(StatKind.Skill, skill.Id));
            uint? next = null, ten = null;
            if (training >= SkillTraining.Trained)
                (next, ten) = Costs(training == SkillTraining.Specialized ? tables.Experience.SpecializedSkill : tables.Experience.TrainedSkill, s.Advancement);
            var trainCost = training <= SkillTraining.Untrained && skill.TrainedCost > 0 ? skill.TrainedCost : 0;
            rows.Add(new CharacterRow(StatKind.Skill, skill.Id, skill.Name, group, skill.IconId, shown, 0, next, ten, trainCost, skill.Description,
                Formula(skill)));
        }
        return rows.OrderBy(row => row.Group).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The game's value with the ranks a raise bought that the game has not heard of yet.</summary>
    private static StatValue Ahead(StatValue value, uint ranks) => ranks == 0 ? value : new StatValue(value.Base + (int)ranks, value.Buffed + (int)ranks);

    /// <summary>The experience list a row's ranks are bought from, or null for a row experience cannot raise.</summary>
    public static uint[]? CostList(CharacterSheet sheet, CharacterTables tables, StatKind kind, uint id) => kind switch
    {
        StatKind.Attribute => tables.Experience.Attribute,
        StatKind.Vital => tables.Experience.Vital,
        _ => sheet.Skills.TryGetValue(id, out var skill) ? skill.Training switch
        {
            SkillTraining.Specialized => tables.Experience.SpecializedSkill,
            SkillTraining.Trained => tables.Experience.TrainedSkill,
            _ => null
        } : null
    };

    /// <summary>
    /// What the next rank and the next ten ranks cost: the table's total for that rank less the experience already spent. Null at the last
    /// rank. A list the portal did not give leaves the row unraisable.
    /// </summary>
    public static (uint? Next, uint? Ten) Costs(uint[] list, Advancement advancement)
    {
        var rank = (int)advancement.Ranks;
        if (rank + 1 >= list.Length) return (null, null);
        uint Cost(int target) => list[target] > advancement.Experience ? list[target] - advancement.Experience : 0;
        return (Cost(rank + 1), Cost(Math.Min(rank + 10, list.Length - 1)));
    }

    /// <summary>The experience still needed for the next level, and how far through this level the character is (0 to 1): none before the level is known, all past the end of the table.</summary>
    public static (long Needed, double Fraction) NextLevel(CharacterSheet sheet, CharacterTables tables)
    {
        var levels = tables.Experience.Level;
        var level = sheet.Level;
        if (level < 1) return (0, 0);
        if (level + 1 >= levels.Length) return (0, 1);
        var start = (long)levels[level];
        var end = (long)levels[level + 1];
        var needed = Math.Max(0, end - sheet.TotalExperience);
        return (needed, end > start ? Math.Max(0, Math.Min(1, (double)(sheet.TotalExperience - start) / (end - start))) : 1);
    }

    /// <summary>Retail's words for the player-killer status.</summary>
    public static string PlayerKillerText(int status) => status switch
    {
        0x04 => "Player Killer",
        0x40 => "Player Killer Lite",
        0x20 => "Free",
        0 => string.Empty,
        _ => "Non-Player Killer"
    };

    private static string Formula(SkillDefinition skill)
    {
        if (!skill.HasFormula || skill.Attribute1 is 0 or > 6) return string.Empty;
        var first = AttributeNames[skill.Attribute1];
        var sum = skill.Attribute2 is 0 or > 6 ? first
            : skill.Attribute2 == skill.Attribute1 ? $"{first} × 2"
            : $"{first} + {AttributeNames[skill.Attribute2]}";
        if (skill.Divisor <= 1) return sum;
        return skill.Attribute2 is 0 or > 6 ? $"{sum} / {skill.Divisor}" : $"({sum}) / {skill.Divisor}";
    }

    private static uint Icon(IReadOnlyDictionary<uint, uint> icons, uint id) => icons.TryGetValue(id, out var icon) ? icon : 0;
}
