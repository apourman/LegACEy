using System;
using System.Collections.Generic;
using System.IO;

namespace LegACEy.Updater;

public enum ApplyResult { Applied = 0, Failed = 1, NotReady = 2 }

// Also compiled into LegACEy.Client.Tests, so it stays plain .NET that runs on net48 and net10.
public static class StageApply
{
    // stage is <install>\update\<version>\files, which counts only once <install>\update\<version>\ready exists.
    // Every folder the release ships (runtime\, Plugins\) replaces the installed one; every root file overwrites the
    // installed one. Anything else in the install, the player's files, stays. On any failure the install is put back.
    public static ApplyResult Apply(string install, string stage)
    {
        var version = Path.GetDirectoryName(stage)!;
        if (!File.Exists(Path.Combine(version, "ready")) || !Directory.Exists(stage))
        {
            Log(install, $"No ready stage at {stage}");
            return ApplyResult.NotReady;
        }

        var rollback = Path.Combine(version, "rollback");
        var undo = new Stack<Action>();
        try
        {
            // Left over by a run that was killed mid-apply. The originals it moved aside are lost; the stage is applied
            // again over whatever that run left.
            DeleteDirectory(rollback);
            Directory.CreateDirectory(rollback);
            foreach (var source in Directory.GetDirectories(stage))
            {
                var name = Path.GetFileName(source);
                var target = Path.Combine(install, name);
                var saved = Path.Combine(rollback, name);
                if (Directory.Exists(target))
                {
                    Directory.Move(target, saved);
                    undo.Push(() => { DeleteDirectory(target); Directory.Move(saved, target); });
                }
                else undo.Push(() => DeleteDirectory(target));
                CopyDirectory(source, target);
            }

            foreach (var source in Directory.GetFiles(stage))
            {
                var name = Path.GetFileName(source);
                var target = Path.Combine(install, name);
                var saved = Path.Combine(rollback, name);
                if (File.Exists(target))
                {
                    File.Move(target, saved);
                    undo.Push(() => { File.Delete(target); File.Move(saved, target); });
                }
                else undo.Push(() => { if (File.Exists(target)) File.Delete(target); });
                File.Copy(source, target);
            }
        }
        catch (Exception error)
        {
            Log(install, $"Update from {stage} failed, rolling back: {error}");
            try
            {
                while (undo.Count > 0) undo.Pop()();
                DeleteDirectory(rollback);
                Log(install, "Rolled back; the stage is kept for the next try");
            }
            catch (Exception rollbackError)
            {
                // The moved-aside originals stay in rollback\ for a person to restore.
                Log(install, $"Rollback failed, originals are in {rollback}: {rollbackError}");
            }
            return ApplyResult.Failed;
        }

        Log(install, $"Applied {stage}");
        try
        {
            // ready goes first, so a stage that is only partly deleted is never applied again. The version folder stays,
            // with the running updater in it, for the client to remove; the download and the stage go now.
            File.Delete(Path.Combine(version, "ready"));
            DeleteDirectory(stage);
            DeleteDirectory(rollback);
            foreach (var file in Directory.GetFiles(version))
            {
                try { File.Delete(file); }
                catch (Exception) { } // The running updater's own exe.
            }
        }
        catch (Exception error)
        {
            Log(install, $"Could not delete the applied stage: {error.Message}");
        }
        return ApplyResult.Applied;
    }

    /// <summary>
    /// The stage to apply: the highest-version ready stage under <c>&lt;install&gt;\update\</c>, never one older than <paramref name="stage"/>,
    /// so a release staged while this updater waited is applied at the same game exit.
    /// </summary>
    public static string Newest(string install, string stage)
    {
        if (!Version.TryParse(Path.GetFileName(Path.GetDirectoryName(stage)), out var newest)) return stage;
        var update = Path.Combine(install, "update");
        if (!Directory.Exists(update)) return stage;
        foreach (var folder in Directory.GetDirectories(update))
        {
            if (File.Exists(Path.Combine(folder, "ready")) && Version.TryParse(Path.GetFileName(folder), out var version) && version > newest)
            {
                newest = version;
                stage = Path.Combine(folder, "files");
            }
        }
        return stage;
    }

    public static void Log(string install, string message)
    {
        try { File.AppendAllText(Path.Combine(install, "updater.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch { }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var folder in Directory.GetDirectories(source)) CopyDirectory(folder, Path.Combine(target, Path.GetFileName(folder)));
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
