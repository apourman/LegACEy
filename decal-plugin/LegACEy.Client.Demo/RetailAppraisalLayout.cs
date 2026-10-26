using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>A line of the retail layout: a label and its value, or text across the window when there is no label. The tone colours the value.</summary>
public sealed record RetailAppraisalLine(string? Label, string Value, AppraisalTone Tone = AppraisalTone.Normal);

/// <summary>A run of lines with the blank lines above it (none above the first group).</summary>
public sealed record RetailAppraisalGroup(IReadOnlyList<RetailAppraisalLine> Lines, int BlankLinesBefore = 1);

/// <summary>
/// The retail layout of an appraisal (backlog 082): the same content as the sections layout, without headings, in groups split by blank
/// lines, as retail's examination panel writes it. An item is plain "Label: value" lines in one colour, with use text and the description
/// as paragraphs. A creature is labels with their values at the right, attributes then vitals in one run, the vitals as
/// "413/413 (100 %)" text (stamina and mana without the percentage), and its tones kept, so only a buffed or debuffed stat is coloured.
/// </summary>
public static class RetailAppraisalLayout
{
    public static IReadOnlyList<RetailAppraisalGroup> Groups(AppraisalContent content) =>
        content.Creature != null ? CreatureGroups(content) : ItemGroups(content);

    // The section headings are AppraisalContent's: the retail layout drops them, but words some groups as retail's examination text does
    // (OpenAC's ItemAppraisalTextFormatter, MIT).
    private static IReadOnlyList<RetailAppraisalGroup> ItemGroups(AppraisalContent content)
    {
        var groups = new List<IEnumerable<RetailAppraisalLine>> { content.Figures.Select(figure => Text($"{figure.Label}: {figure.Value}")) };
        // What a piece covers follows the weapon's and armour's lines.
        var covers = content.Covers.Length > 0 ? Text("Covers " + content.Covers) : null;
        foreach (var section in content.Sections)
        {
            if (covers != null && section.Heading is not ("Tinkering" or "Set & Ratings" or "Combat" or "Armor"))
            {
                groups.Add(new[] { covers });
                covers = null;
            }
            switch (section.Heading)
            {
                case "Armor" when covers != null:
                    groups.Add(section.Rows.SelectMany(ItemLines).Append(covers));
                    covers = null;
                    break;
                case "Spells":
                    groups.Add(section.Rows.Select(row => Text("Spells: " + row.Text)));
                    break;
                case "Properties":
                    groups.Add(PropertyLines(section.Rows, content.RetailProperties ?? Array.Empty<string>()));
                    break;
                // Spellcraft and mana, then each spell under retail's heading.
                case "Magic":
                    groups.Add(section.Rows.Where(row => row is not AppraisalSpellRow).SelectMany(ItemLines));
                    groups.Add(SpellLines("Spell Descriptions:", section.Rows));
                    break;
                case "Enchantments":
                    groups.Add(SpellLines("Enchantments:", section.Rows));
                    break;
                default:
                    groups.Add(section.Rows.SelectMany(ItemLines));
                    break;
            }
        }
        if (covers != null) groups.Add(new[] { covers });
        groups.Add(content.Description.Length > 0 ? new[] { Text(content.Description) } : Array.Empty<RetailAppraisalLine>());
        return groups.Select(lines => lines.ToList()).Where(lines => lines.Count > 0).Select(lines => new RetailAppraisalGroup(lines)).ToList();
    }

    /// <summary>
    /// The properties group with retail's one Properties line in place of the rows it gathers (the imbues, the properties, Dyeable, the
    /// bond and Attuned, which the sections layout lists one by one).
    /// </summary>
    private static IEnumerable<RetailAppraisalLine> PropertyLines(IReadOnlyList<AppraisalRow> rows, IReadOnlyList<string> words)
    {
        var written = false;
        foreach (var row in rows)
        {
            if (row.Label is "Imbued Effect" or "Properties" || (row.Label == null && words.Contains(row.Text)))
            {
                if (!written && words.Count > 0) yield return Text("Properties: " + string.Join(", ", words));
                written = true;
                continue;
            }
            foreach (var line in ItemLines(row)) yield return line;
        }
    }

    /// <summary>"Spell Descriptions:" or "Enchantments:", then "~ Name: description" for each spell, as retail lists them.</summary>
    private static IEnumerable<RetailAppraisalLine> SpellLines(string heading, IEnumerable<AppraisalRow> rows)
    {
        var spells = rows.OfType<AppraisalSpellRow>().Select(spell => Text($"~ {spell.Name}: {spell.Description}")).ToList();
        return spells.Count == 0 ? spells : spells.Prepend(Text(heading));
    }

    private static IEnumerable<RetailAppraisalLine> ItemLines(AppraisalRow row)
    {
        switch (row)
        {
            case AppraisalMeterRow meter:
                yield return Text($"{meter.Name}: {meter.Text}");
                if (meter.Note != null) yield return Text($"{meter.Name} Cost: {meter.Note}");
                yield break;
            case { Label: null }:
                yield return Text(row.Text);
                yield break;
            case { Label: "Wield" or "Activation" }:
                yield return Text($"{row.Label} requires {row.Text}");
                yield break;
            default:
                yield return Text($"{row.Label}: {row.Text}");
                yield break;
        }
    }

    private static IReadOnlyList<RetailAppraisalGroup> CreatureGroups(AppraisalContent content)
    {
        IEnumerable<AppraisalRow> Rows(string heading) => content.Sections.FirstOrDefault(section => section.Heading == heading)?.Rows ?? Array.Empty<AppraisalRow>();
        var groups = new[]
        {
            new RetailAppraisalGroup(Rows("Attributes").Concat(Rows("Vitals")).Select(CreatureLine).ToList()),
            new RetailAppraisalGroup(Rows("Armor").Select(CreatureLine).ToList(), BlankLinesBefore: 2),
            new RetailAppraisalGroup(Rows("Ratings").Select(CreatureLine).ToList()),
            new RetailAppraisalGroup(Rows("Allegiance").Select(CreatureLine).ToList()),
            // The header box shows the PK status.
            new RetailAppraisalGroup(Rows("Character").Where(row => row.Label != "PK Status").Select(CreatureLine).ToList()),
        };
        return groups.Where(group => group.Lines.Count > 0).ToList();
    }

    // Retail gives health its percentage, and stamina and mana only current over maximum.
    private static RetailAppraisalLine CreatureLine(AppraisalRow row) => row is AppraisalMeterRow meter
        ? new RetailAppraisalLine(meter.Name, meter.Fill == AppraisalMeterFill.Health
            ? string.Format(CultureInfo.InvariantCulture, "{0}/{1} ({2:0} %)", meter.Current, meter.Maximum,
                meter.Maximum > 0 ? Math.Round(100.0 * meter.Current / meter.Maximum) : 0)
            : string.Format(CultureInfo.InvariantCulture, "{0}/{1}", meter.Current, meter.Maximum), meter.Tone)
        : new RetailAppraisalLine(row.Label, row.Text, row.Tone);

    private static RetailAppraisalLine Text(string text) => new(null, text);
}
