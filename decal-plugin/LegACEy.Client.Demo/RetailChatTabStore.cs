using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LegACEy.Client.Demo;

/// <summary>
/// The chat tabs each character has, in a small text file beside the plugin: one row per server and character, holding the tab list. A
/// character with no row, or a damaged one, has the six default tabs. Text is base64 encoded, so names can hold any punctuation.
/// </summary>
public sealed class FileRetailChatTabStore
{
    private const string AllChannels = "*";
    private readonly string _path;
    private readonly object _sync = new();

    public FileRetailChatTabStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A chat tabs file path is required.", nameof(path));
        _path = path;
    }

    /// <summary>The character's tabs, or null when none are saved or the saved ones are damaged: the caller then uses the defaults.</summary>
    public IReadOnlyList<RetailChatTab>? Load(string server, string character)
    {
        lock (_sync) return Rows().TryGetValue(Key(server, character), out var payload) ? Parse(payload) : null;
    }

    public void Save(string server, string character, IReadOnlyList<RetailChatTab> tabs)
    {
        lock (_sync)
        {
            var rows = Rows();
            rows[Key(server, character)] = Base64(string.Join("\n", tabs.Select(FormatTab)));
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_path, rows.Select(pair => pair.Key + "|" + pair.Value));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, string> Rows()
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return rows;
        try
        {
            foreach (var line in File.ReadAllLines(_path))
            {
                var fields = line.Split('|');
                if (fields.Length == 3) rows[fields[0] + "|" + fields[1]] = fields[2];
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return rows;
    }

    /// <summary>A tab as name, kinds, target and channels: the target is empty for none, the channels are "*" for every channel.</summary>
    private static string FormatTab(RetailChatTab tab) =>
        Base64(tab.Name) + "|" + ((int)tab.Kinds).ToString(CultureInfo.InvariantCulture) + "|" + Base64(tab.TargetId ?? string.Empty) + "|" +
        (tab.Channels == null ? AllChannels : Base64(string.Join(",", tab.Channels)));

    private static IReadOnlyList<RetailChatTab>? Parse(string payload)
    {
        try
        {
            var tabs = new List<RetailChatTab>();
            foreach (var line in Decode(payload).Split('\n'))
            {
                var fields = line.Split('|');
                if (fields.Length != 4 || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kinds) || kinds == 0)
                    return null;
                var name = Decode(fields[0]);
                if (name.Length == 0) return null;
                var target = Decode(fields[2]);
                IReadOnlyList<string>? channels = fields[3] == AllChannels
                    ? null
                    : Decode(fields[3]).Split(',').Where(channel => channel.Length > 0).ToArray();
                tabs.Add(new RetailChatTab(name, (RetailChatKind)kinds, target.Length == 0 ? null : target, channels));
            }
            return tabs.Count == 0 ? null : tabs;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Key(string server, string character) => Base64(server ?? string.Empty) + "|" + Base64(character ?? string.Empty);

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
