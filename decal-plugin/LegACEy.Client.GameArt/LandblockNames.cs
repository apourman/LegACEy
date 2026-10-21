using System;
using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.GameArt;

/// <summary>
/// The shared landblock list (Content/landblock-names.tsv, embedded): each landblock's place names, for the radar's plaque.
/// A landblock with several places returns all of them.
/// </summary>
public static class LandblockNames
{
    private static readonly Lazy<Dictionary<ushort, string[]>> Names = new(() => Parse(ReadEmbedded()));

    /// <summary>The names of a landblock (0xXXYY); empty when the list has none.</summary>
    public static IReadOnlyList<string> Get(ushort landblock) =>
        Names.Value.TryGetValue(landblock, out var names) ? names : Array.Empty<string>();

    private static string ReadEmbedded()
    {
        using var stream = typeof(LandblockNames).Assembly.GetManifestResourceStream("landblock-names.tsv")!;
        return new StreamReader(stream).ReadToEnd();
    }

    // One `XXYY<tab>name[|name...]` line per landblock.
    private static Dictionary<ushort, string[]> Parse(string text)
    {
        var names = new Dictionary<ushort, string[]>();
        foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            names[Convert.ToUInt16(parts[0], 16)] = parts[1].Split('|');
        }
        return names;
    }
}
