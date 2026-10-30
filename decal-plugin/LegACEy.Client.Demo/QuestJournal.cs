using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LegACEy.Client.Demo;

/// <summary>
/// A journal page as retail's gmJournalUI keeps it: a label, a title and notes; a recorded location (map coordinates, north and east, negatives
/// south and west); and a timer of days, hours and minutes, which once started runs to <see cref="TimerEnd"/> (Unix seconds).
/// </summary>
public sealed class JournalPage
{
    public string Label { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int Days { get; set; }
    public int Hours { get; set; }
    public int Minutes { get; set; }
    public bool HasLocation { get; set; }
    public double North { get; set; }
    public double East { get; set; }
    public bool TimerRunning { get; set; }
    public double TimerEnd { get; set; }

    /// <summary>Retail's location line: "None", or the recorded coordinates.</summary>
    public string LocationText => HasLocation ? QuestState.Coordinates(North, East) : "None";

    /// <summary>Retail's timer text: None when not started, the time left while it runs, Ready once it has run out.</summary>
    public string TimerText(double now)
    {
        if (!TimerRunning) return "None";
        var left = TimerEnd - now;
        return left >= 1 ? QuestState.Duration(left) : "Ready";
    }
}

/// <summary>
/// The character's journal: its pages, in retail's file format. Retail keeps the journal in "Documents\Asheron's Call\Journal-server-character.txt",
/// loads it once at login and writes its own copy back each time its panel shows or hides, which would undo any change made beside it. So the
/// Dereth journal keeps its own file in the same format, seeded once from retail's. Timers here end at Unix seconds; a seeded page's timer
/// is not carried over, since retail's clock is not known.
/// </summary>
public sealed class QuestJournal
{
    private readonly List<JournalPage> _pages = new() { new JournalPage() };

    public IReadOnlyList<JournalPage> Pages => _pages;

    /// <summary>Set by every change; the owner saves and clears it.</summary>
    public bool Dirty { get; set; }

    /// <summary>A new empty page at the end. Returns its index.</summary>
    public int Add()
    {
        _pages.Add(new JournalPage());
        Dirty = true;
        return _pages.Count - 1;
    }

    /// <summary>Removes a page; the last page left is emptied instead, as the journal always has one.</summary>
    public void Delete(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        if (_pages.Count == 1) _pages[0] = new JournalPage();
        else _pages.RemoveAt(index);
        Dirty = true;
    }

    /// <summary>Reads the journal from <paramref name="path"/>, or when it is missing from <paramref name="seed"/> (retail's file, timers left out).</summary>
    public static QuestJournal Load(string path, string? seed, Action<string>? log = null)
    {
        try
        {
            if (File.Exists(path)) return Parse(File.ReadAllText(path, Latin1), carryTimers: true);
            if (seed != null && File.Exists(seed))
            {
                var journal = Parse(File.ReadAllText(seed, Latin1), carryTimers: false);
                journal.Dirty = true;
                return journal;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"The journal could not be read: {exception.Message}");
        }
        return new QuestJournal();
    }

    /// <summary>Writes the journal, through a temporary file so a failed write leaves the old one. False when it could not be written.</summary>
    public bool Save(string path, Action<string>? log = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Write(), Latin1);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            Dirty = false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"The journal could not be saved: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Retail's format: each page opens with &lt;NEWP&gt;, then one "&lt;TAG&gt; value" line per field. Notes keep their line breaks, so a line
    /// that opens with no known tag continues the field before it.
    /// </summary>
    public static QuestJournal Parse(string text, bool carryTimers = true)
    {
        var journal = new QuestJournal();
        journal._pages.Clear();
        JournalPage? page = null;
        string? field = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var tag = line.Length >= 6 && line[0] == '<' && line[5] == '>' ? line.Substring(1, 4) : null;
            if (tag == "NEWP")
            {
                journal._pages.Add(page = new JournalPage());
                field = null;
                continue;
            }
            if (page == null) continue;
            if (tag == null || !Tags.Contains(tag))
            {
                if (field == "NOTE") page.Notes += "\n" + line;
                continue;
            }
            field = tag;
            var value = line.Length > 7 ? line.Substring(7) : string.Empty;
            switch (tag)
            {
                case "LABE": page.Label = value.Trim(); break;
                case "TITL": page.Title = value.Trim(); break;
                case "NOTE": page.Notes = value; break;
                case "DAYS": page.Days = Number(value); break;
                case "HOUR": page.Hours = Number(value); break;
                case "MINU": page.Minutes = Number(value); break;
                case "LOC?": page.HasLocation = Flag(value); break;
                case "LOCX": page.East = Real(value); break;
                case "LOCY": page.North = Real(value); break;
                case "TIM?": page.TimerRunning = carryTimers && Flag(value); break;
                case "TIME": page.TimerEnd = carryTimers ? Real(value) : 0; break;
            }
        }
        // Retail's file ends each page with a blank line, which the notes do not keep.
        foreach (var each in journal._pages) each.Notes = each.Notes.TrimEnd('\n').Trim(' ');
        if (journal._pages.Count == 0) journal._pages.Add(new JournalPage());
        return journal;
    }

    public string Write()
    {
        var text = new StringBuilder();
        foreach (var page in _pages)
        {
            text.Append("<NEWP>\r\n");
            text.Append("<LABE> ").Append(page.Label).Append("\r\n");
            text.Append("<TITL> ").Append(page.Title).Append("\r\n");
            text.Append("<NOTE> ").Append(page.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n");
            text.Append("<DAYS> ").Append(page.Days.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append("<HOUR> ").Append(page.Hours.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append("<MINU> ").Append(page.Minutes.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append("<LOC?> ").Append(page.HasLocation ? "TRUE" : "FALSE").Append("\r\n");
            text.Append("<LOCX> ").Append(page.East.ToString("F6", CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append("<LOCY> ").Append(page.North.ToString("F6", CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append("<TIM?> ").Append(page.TimerRunning ? "TRUE" : "FALSE").Append("\r\n");
            text.Append("<TIME> ").Append(page.TimerEnd.ToString("F6", CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append("\r\n");
        }
        return text.ToString();
    }

    private static readonly HashSet<string> Tags = new() { "LABE", "TITL", "NOTE", "DAYS", "HOUR", "MINU", "LOC?", "LOCX", "LOCY", "TIM?", "TIME" };
    // Retail writes the client's code page; Latin-1 keeps every byte as it was.
    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    private static int Number(string value) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static double Real(string value) => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static bool Flag(string value) => value.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Retail's journal file for a character, where the Dereth journal seeds from.</summary>
    public static string RetailPath(string server, string character) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Asheron's Call", $"Journal-{server}-{character}.txt");

    /// <summary>The Dereth journal's own file for a character, in the plugin's folder.</summary>
    public static string OwnPath(string pluginDirectory, string server, string character) =>
        Path.Combine(pluginDirectory, "journals", $"Journal-{SafeFileName(server)}-{SafeFileName(character)}.txt");

    private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
