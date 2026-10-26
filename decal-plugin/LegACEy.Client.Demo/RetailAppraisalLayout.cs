using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>A line of the retail layout: a label and its value, or text across the window when there is no label. The tone colours the value.</summary>
public sealed record RetailAppraisalLine(string? Label, string Value, AppraisalTone Tone = AppraisalTone.Normal);

/// <summary>
/// A run of lines with the blank lines above it (none above the first group). <see cref="Column"/> starts the values in one column after
/// the labels, as retail lines up the armour levels, instead of at the window's right edge.
/// </summary>
public sealed record RetailAppraisalGroup(IReadOnlyList<RetailAppraisalLine> Lines, int BlankLinesBefore = 1, bool Column = false);

/// <summary>
/// The retail layout of an appraisal (backlog 082): the same content as the sections layout, without headings, in groups split by blank
/// lines, as retail's examination panel writes it. An item is plain "Label: value" lines in one colour, with use text and the description
/// as paragraphs. A creature is labels with their values at the right, attributes then vitals in one run, the vitals as
/// "413/413 (100 %)" text, and its tones kept, so only a buffed or debuffed stat is coloured.
/// </summary>
public static class RetailAppraisalLayout
{
    public static IReadOnlyList<RetailAppraisalGroup> Groups(AppraisalContent content) =>
        content.Creature != null ? CreatureGroups(content) : ItemGroups(content);

    private static IReadOnlyList<RetailAppraisalGroup> ItemGroups(AppraisalContent content)
    {
        var groups = new List<IEnumerable<RetailAppraisalLine>> { content.Figures.Select(figure => Text($"{figure.Label}: {figure.Value}")) };
        groups.AddRange(content.Sections.Select(ItemLines));
        groups.Add(content.Description.Length > 0 ? new[] { Text(content.Description) } : Array.Empty<RetailAppraisalLine>());
        return groups.Select(lines => lines.ToList()).Where(lines => lines.Count > 0).Select(lines => new RetailAppraisalGroup(lines)).ToList();
    }

    // The section headings are AppraisalContent's: the retail layout drops them, but names the spell lines as retail does, and leaves out
    // each spell's description (retail's panel lists the spells by name).
    private static IEnumerable<RetailAppraisalLine> ItemLines(AppraisalSection section) => section.Heading switch
    {
        "Spells" => section.Rows.Select(row => Text("Spells: " + row.Text)),
        "Enchantments" => new[] { Text("Enchantments: " + string.Join(", ", section.Rows.Select(row => row.Label))) },
        _ => section.Rows.Where(row => row is not AppraisalSpellRow).SelectMany(ItemLines),
    };

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
            new RetailAppraisalGroup(Rows("Armor").Select(CreatureLine).ToList(), BlankLinesBefore: 2, Column: true),
            new RetailAppraisalGroup(Rows("Ratings").Select(CreatureLine).ToList()),
            new RetailAppraisalGroup(Rows("Allegiance").Select(CreatureLine).ToList()),
            // The header box shows the PK status.
            new RetailAppraisalGroup(Rows("Character").Where(row => row.Label != "PK Status").Select(CreatureLine).ToList()),
        };
        return groups.Where(group => group.Lines.Count > 0).ToList();
    }

    private static RetailAppraisalLine CreatureLine(AppraisalRow row) => row is AppraisalMeterRow meter
        ? new RetailAppraisalLine(meter.Name, string.Format(CultureInfo.InvariantCulture, "{0}/{1} ({2:0} %)", meter.Current, meter.Maximum,
            meter.Maximum > 0 ? Math.Round(100.0 * meter.Current / meter.Maximum) : 0), meter.Tone)
        : new RetailAppraisalLine(row.Label, row.Text, row.Tone);

    private static RetailAppraisalLine Text(string text) => new(null, text);
}
