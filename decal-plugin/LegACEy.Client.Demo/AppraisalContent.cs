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
    private const uint ItemTypeInt = 1, EncumbranceValInt = 5, ValueInt = 19, ItemWorkmanshipInt = 105;
    private const uint NameString = 1, UseString = 14, LongDescString = 16;
    private const uint IconDid = 8, IconOverlayDid = 50, IconUnderlayDid = 52;

    /// <summary>The summary figures, in the order they sit side by side. A figure the server did not send is left out.</summary>
    private static readonly (string Label, uint Property)[] FigureRules =
    {
        ("Value", ValueInt), ("Burden", EncumbranceValInt), ("Workmanship", ItemWorkmanshipInt)
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
    };

    /// <summary>Builds what the window shows from one appraisal.</summary>
    public static AppraisalContent From(AppraisalResponse response)
    {
        var figures = FigureRules
            .Where(rule => response.Ints.ContainsKey(rule.Property))
            .Select(rule => new AppraisalFigure(rule.Label, response.Ints[rule.Property].ToString(CultureInfo.InvariantCulture)))
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

    private static string TypeLineOf(AppraisalResponse response)
    {
        if (!response.Ints.TryGetValue(ItemTypeInt, out var type)) return string.Empty;
        return ItemTypeNames.FirstOrDefault(item => (type & item.Bit) != 0).Name ?? string.Empty;
    }

    private static IEnumerable<AppraisalRow> TextRows(IReadOnlyDictionary<uint, string> strings, uint property) =>
        strings.TryGetValue(property, out var text) && text.Length > 0 ? new[] { new AppraisalRow(null, text) } : Array.Empty<AppraisalRow>();

    private sealed record SectionRule(string Heading, Func<AppraisalResponse, IEnumerable<AppraisalRow>> Rows);
}
