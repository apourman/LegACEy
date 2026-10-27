using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// What a tester's log needs when the game crashes. Each running client leaves a marker named by its pid, removed at a clean shutdown; a
/// marker whose client is gone means that client crashed or was killed, so the next client to start copies what Windows recorded about it
/// into the log. Retail's own faults kill the game before LegACEy can write anything, so this is the only way they reach the log.
/// </summary>
internal static class SessionRecord
{
    private static string Folder => Path.Combine(ClientUiRuntime.PluginDirectory, "sessions");
    private static string Marker => Path.Combine(Folder, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Leaves this client's marker and reports, off the calling thread, every client that ended without one being removed.</summary>
    public static void Open(Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var gone = Directory.GetFiles(Folder).Select(ClaimIfGone).OfType<(int Pid, DateTime Start)>().ToList();
            File.WriteAllText(Marker, Process.GetCurrentProcess().StartTime.ToString("O", CultureInfo.InvariantCulture));
            if (gone.Count > 0) Task.Run(() => Report(gone, log));
        }
        catch (Exception exception) { log($"Crash records are off: {exception.Message}"); }
    }

    /// <summary>Removes this client's marker: it closed cleanly.</summary>
    public static void Close()
    {
        try { File.Delete(Marker); }
        catch { } // A marker left behind reads as an unclean end on the next start; nothing better to do while shutting down.
    }

    /// <summary>Retail's graphics settings, as last saved to UserPreferences.ini, in one line: high texture detail makes retail crash far more.</summary>
    public static void LogGraphics(Action<string> log)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Asheron's Call", "UserPreferences.ini");
        try
        {
            var section = "";
            var settings = new List<string>();
            foreach (var line in File.ReadLines(path).Select(line => line.Trim()))
            {
                if (line.StartsWith('[')) section = line;
                else if (section is "[Render]" or "[Display]" && line.Contains('=')) settings.Add(line);
            }
            log($"Retail graphics (saved {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}): {string.Join(", ", settings)}.");
        }
        catch (Exception exception) { log($"Retail graphics unknown: {path} could not be read: {exception.Message}"); }
    }

    /// <summary>The marker's client when it is gone, removing the marker; null while it runs, or when another client claimed it first.</summary>
    private static (int Pid, DateTime Start)? ClaimIfGone(string marker)
    {
        try
        {
            if (!int.TryParse(Path.GetFileName(marker), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return null;
            string text;
            // Shared for delete, so a running client closing right now can still remove its own marker.
            using (var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                text = new StreamReader(stream).ReadToEnd();
            var start = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (Running(pid, start)) return null;
            // Only one of two clients starting together wins the rename, so only one reports it.
            var claimed = marker + ".claimed";
            File.Move(marker, claimed);
            File.Delete(claimed);
            return (pid, start);
        }
        catch { return null; }
    }

    private static bool Running(int pid, DateTime start)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return Math.Abs((process.StartTime - start).TotalSeconds) < 2;
        }
        catch { return false; }
    }

    private static void Report(List<(int Pid, DateTime Start)> gone, Action<string> log)
    {
        try
        {
            var events = new List<CrashEvent>();
            try { events = CrashEvent.Parse(ReadWindowsCrashes(gone.Min(client => client.Start))); }
            catch (Exception exception) { log($"Windows' crash records could not be read: {exception.Message}"); }
            foreach (var (pid, start) in gone.OrderBy(client => client.Start)) log(Describe(pid, start, events));
        }
        catch (Exception exception) { log($"Crash records could not be written: {exception.Message}"); }
    }

    /// <summary>One line for a client that is gone. A .NET record it is given is removed from <paramref name="events"/>, so no other client gets it too.</summary>
    internal static string Describe(int pid, DateTime start, List<CrashEvent> events)
    {
        var client = $"A client before this one (pid {pid}, started {start:yyyy-MM-dd HH:mm:ss})";
        var own = events.Where(e => e.Pid == pid && e.Time >= start).OrderBy(e => e.Time).ToList();
        if (own.Count > 0)
            return $"{client} crashed after {Minutes(own[0].Time - start)}: Windows recorded {string.Join("; ", own.Select(e => e.Summary))}.";
        // .NET's record has no pid: one after the start is likely this client's, unless several clients were running.
        if (events.Where(e => e.Pid == null && e.Time >= start).OrderBy(e => e.Time).FirstOrDefault() is { } first)
        {
            // .NET writes each crash twice (Framework and CoreCLR lines), a moment apart, and only one may carry the stack.
            bool Twin(CrashEvent e) => e.Pid == null && Math.Abs((e.Time - first.Time).TotalSeconds) < 2;
            var dotnet = events.Where(Twin).MaxBy(e => e.Summary.Length)!;
            events.RemoveAll(Twin);
            return $"{client} crashed after {Minutes(dotnet.Time - start)}, probably: .NET recorded {dotnet.Summary}.";
        }
        return $"{client} ended without closing cleanly, and Windows recorded no crash for it (killed, frozen and ended, or a power loss).";
    }

    private static string Minutes(TimeSpan span) => $"{span.TotalMinutes:0.0} min";

    private static string ReadWindowsCrashes(DateTime since)
    {
        var milliseconds = (long)Math.Max(0, (DateTime.Now - since).TotalMilliseconds) + 60_000;
        var query = "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime']) and TimeCreated[timediff(@SystemTime) <= "
            + milliseconds.ToString(CultureInfo.InvariantCulture) + "]]]";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"))
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "qe", "Application", "/q:" + query, "/f:text", "/rd:true", "/c:200" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(15_000)) { process.Kill(); throw new TimeoutException("wevtutil did not answer in 15 s"); }
        return text.Result;
    }
}

/// <summary>One acclient crash record from Windows' Application log: Application Error (with the pid) or .NET Runtime 1026 (without).</summary>
internal sealed record CrashEvent(DateTime Time, int? Pid, string Summary)
{
    /// <summary>The acclient records in <c>wevtutil qe /f:text</c> output; others are skipped.</summary>
    public static List<CrashEvent> Parse(string text)
    {
        var events = new List<CrashEvent>();
        foreach (var block in Regex.Split(text.Replace("\r", ""), @"^Event\[\d+\]", RegexOptions.Multiline))
        {
            if (!block.Contains("acclient.exe", StringComparison.OrdinalIgnoreCase)) continue;
            // wevtutil marks the local time with a Z, so it is read as local.
            if (Field(block, @"Date: (\S+?)Z?$") is not { } date
                || !DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)) continue;
            if (Field(block, @"Faulting process id: 0x([0-9A-Fa-f]+)") is { } pid)
                events.Add(new CrashEvent(time, int.Parse(pid, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    $"{Field(block, @"Exception code: (\S+)")} in {Field(block, @"Faulting module name: ([^,]+)")} at +{Field(block, @"Fault offset: (\S+)")}"));
            else if (Field(block, @"Exception Info: (.+)$") is { } info)
            {
                var stack = Regex.Matches(block, @"^\s+at (.+)$", RegexOptions.Multiline).Take(3).Select(m => m.Groups[1].Value.Trim());
                events.Add(new CrashEvent(time, null, string.Join(" ← ", stack.Prepend(info))));
            }
        }
        return events;
    }

    private static string? Field(string block, string pattern) =>
        Regex.Match(block, pattern, RegexOptions.Multiline) is { Success: true } match ? match.Groups[1].Value.Trim() : null;
}
