using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LegACEy.Client.Demo;

/// <summary>
/// The chat tabs each window of each character has, in a small text file beside the plugin: one row per server, character and window,
/// holding that window's tab list. A window with no row, or a damaged one, has the six default tabs when it is the main window; a torn-off
/// window with no row is not restored. Text is base64 encoded, so names can hold any punctuation. A save never writes over rows it could
/// not read, and the file is replaced in one step, so a failed save leaves the old file as it was.
/// </summary>
public sealed class FileRetailChatTabStore
{
    private const string AllChannels = "*";
    private readonly string _path;
    private readonly Action<string> _log;
    private readonly object _sync = new();

    public FileRetailChatTabStore(string path, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A chat tabs file path is required.", nameof(path));
        _path = path;
        _log = log ?? (_ => { });
    }

    /// <summary>The window's tabs, or null when none are saved, the saved ones are damaged, or the file could not be read.</summary>
    public IReadOnlyList<RetailChatTab>? Load(string server, string character, string windowId)
    {
        lock (_sync)
        {
            if (!TryRows(out var rows)) return null;
            return rows.TryGetValue(Key(server, character, windowId), out var payload) ? Parse(payload, windowId) : null;
        }
    }

    /// <summary>The ids of the windows that have a row for the character, in id order.</summary>
    public IReadOnlyList<string> Windows(string server, string character)
    {
        lock (_sync)
        {
            if (!TryRows(out var rows)) return Array.Empty<string>();
            var prefix = CharacterKey(server, character) + "|";
            return rows.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => Decode(key.Substring(prefix.Length)))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>
    /// Saves the window's tabs and keeps every other row. When the file could not be read, nothing is written, so the rows that could not
    /// be read are not lost; the tabs stay in memory for this session.
    /// </summary>
    public void Save(string server, string character, string windowId, IReadOnlyList<RetailChatTab> tabs) =>
        Write(server, character, windowId, Base64(string.Join("\n", tabs.Select(FormatTab))));

    /// <summary>Removes the window's row: a torn-off window that merged back into the main window has none.</summary>
    public void Remove(string server, string character, string windowId) => Write(server, character, windowId, null);

    private void Write(string server, string character, string windowId, string? payload)
    {
        lock (_sync)
        {
            if (!TryRows(out var rows))
            {
                _log("Chat tabs not saved: the chat tabs file could not be read, so writing it would lose other rows.");
                return;
            }
            var key = Key(server, character, windowId);
            if (payload == null) rows.Remove(key);
            else rows[key] = payload;
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(temp, rows.Select(pair => pair.Key + "|" + pair.Value));
                // Replace the old file in one step where it exists; a first save just moves the temporary file into place.
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log($"Chat tabs not saved to {_path}: {exception.Message}");
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    /// <summary>The rows in the file, by key; false when the file exists but could not be read. A missing file has no rows.</summary>
    private bool TryRows(out Dictionary<string, string> rows)
    {
        rows = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return true;
        try
        {
            foreach (var line in File.ReadAllLines(_path))
            {
                var fields = line.Split('|');
                if (fields.Length == 4) rows[fields[0] + "|" + fields[1] + "|" + fields[2]] = fields[3];
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"Chat tabs file {_path} could not be read: {exception.Message}");
            return false;
        }
    }

    private void TryDelete(string temp)
    {
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"Could not remove the temporary chat tabs file {temp}: {exception.Message}");
        }
    }

    /// <summary>A tab as name, kinds, target and channels: the target is empty for none, the channels are "*" for every channel.</summary>
    private static string FormatTab(RetailChatTab tab) =>
        Base64(tab.Name) + "|" + ((int)tab.Kinds).ToString(CultureInfo.InvariantCulture) + "|" + Base64(tab.TargetId ?? string.Empty) + "|" +
        (tab.Channels == null ? AllChannels : Base64(string.Join(",", tab.Channels)));

    /// <summary>
    /// The tab list, or null for the defaults when any part is damaged: no tabs, a blank name, a target that is neither none nor a talk
    /// target, or, for the main window, a first tab that is not the All tab. A damaged row never reaches the window.
    /// </summary>
    private static IReadOnlyList<RetailChatTab>? Parse(string payload, string windowId)
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
                if (name.Trim().Length == 0) return null;
                var target = Decode(fields[2]);
                if (target.Length > 0 && target != RetailChatTab.LastTellId && !RetailTalkTargets.All.Any(talk => talk.Id == target)) return null;
                IReadOnlyList<string>? channels = fields[3] == AllChannels
                    ? null
                    : Decode(fields[3]).Split(',').Where(channel => channel.Length > 0).ToArray();
                tabs.Add(new RetailChatTab(name, (RetailChatKind)kinds, target.Length == 0 ? null : target, channels));
            }
            if (tabs.Count == 0) return null;
            var all = RetailChatTab.Defaults[0];
            if (windowId == RetailChatWindows.MainId && (tabs[0].Name != all.Name || tabs[0].TargetId != all.TargetId)) return null;
            return tabs;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string CharacterKey(string server, string character) =>
        Base64(server ?? string.Empty) + "|" + Base64(character ?? string.Empty);

    private static string Key(string server, string character, string windowId) => CharacterKey(server, character) + "|" + Base64(windowId);

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
