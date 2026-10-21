using System;
using System.Collections.Generic;
using System.IO;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The shared landblock list (Content/landblock-names.tsv, embedded): each landblock's place names. A landblock with several
    /// places returns all of them.
    /// </summary>
    public static class LandblockNames
    {
        private static readonly Lazy<Dictionary<ushort, string[]>> names = new Lazy<Dictionary<ushort, string[]>>(() => Parse(ReadEmbedded()));

        /// <summary>The names of a landblock (0xXXYY); empty when the list has none.</summary>
        public static IReadOnlyList<string> Get(ushort landblock)
        {
            return names.Value.TryGetValue(landblock, out var found) ? found : Array.Empty<string>();
        }

        private static string ReadEmbedded()
        {
            using var stream = typeof(LandblockNames).Assembly.GetManifestResourceStream("landblock-names.tsv");
            return new StreamReader(stream).ReadToEnd();
        }

        // One `XXYY<tab>name[|name...]` line per landblock.
        private static Dictionary<ushort, string[]> Parse(string text)
        {
            var result = new Dictionary<ushort, string[]>();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                result[Convert.ToUInt16(parts[0], 16)] = parts[1].Split('|');
            }
            return result;
        }
    }
}
