using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace LegACEy.Updater;

// Also compiled into LegACEy.Client.Tests, so it stays plain .NET that runs on net48 and net10.
public static class WorldApply
{
    /// <summary>
    /// Applies the world patch staged in <paramref name="stage"/> (<c>&lt;game&gt;\world-update\&lt;version&gt;\</c>) to the game folder's dats,
    /// once its <c>ready</c> file exists and the patch still matches the SHA-256 in it. Every record is written into a copy of its dat first;
    /// the copies are swapped in only once all of them are written, then the world version file is written. On any failure the original
    /// dats and the version file are put back and the stage is kept. Logs to <paramref name="install"/>'s <c>updater.log</c>.
    /// </summary>
    public static ApplyResult Apply(string install, string game, string stage)
    {
        var ready = Path.Combine(stage, WorldFiles.ReadyFile);
        var patchPath = Path.Combine(stage, WorldFiles.PatchFile);
        if (!File.Exists(ready) || !File.Exists(patchPath))
        {
            StageApply.Log(install, $"No ready world stage at {stage}");
            return ApplyResult.NotReady;
        }

        var rollback = Path.Combine(stage, "rollback");
        var copies = new List<string>();
        var undo = new Stack<Action>();
        int version;
        try
        {
            // Left by a run that was killed mid-swap: it holds the originals, so they go back before anything else.
            if (Directory.Exists(rollback))
            {
                foreach (var saved in Directory.GetFiles(rollback))
                {
                    var target = Path.Combine(game, Path.GetFileName(saved));
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(saved, target);
                }
                Directory.Delete(rollback, true);
                StageApply.Log(install, "Put back the originals a killed world update left in rollback");
            }
            if (!WorldFiles.Sha256(patchPath).Equals(File.ReadAllText(ready).Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the staged patch does not match its checksum");
            WorldPatch patch;
            using (var stream = File.OpenRead(patchPath)) patch = WorldPatch.Read(stream);
            version = patch.Version;
            var current = WorldFiles.ReadVersion(game);
            if (version <= current)
            {
                StageApply.Log(install, $"World version {current} already has world patch {version}; nothing to apply");
                Clean(install, stage);
                return ApplyResult.Applied;
            }

            var dats = patch.Records.GroupBy(record => record.Dat, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var dat in dats)
            {
                var copy = Path.Combine(stage, dat.Key);
                copies.Add(copy);
                File.Copy(Path.Combine(game, dat.Key), copy, overwrite: true);
                File.SetAttributes(copy, FileAttributes.Normal);
                Write(copy, dat);
            }

            Directory.CreateDirectory(rollback);
            foreach (var dat in dats) Swap(Path.Combine(stage, dat.Key), Path.Combine(game, dat.Key), rollback, undo);

            var versionPath = Path.Combine(game, WorldFiles.VersionFile);
            var savedVersion = Path.Combine(rollback, WorldFiles.VersionFile);
            if (File.Exists(versionPath))
            {
                File.Move(versionPath, savedVersion);
                undo.Push(() => { if (File.Exists(versionPath)) File.Delete(versionPath); File.Move(savedVersion, versionPath); });
            }
            else undo.Push(() => { if (File.Exists(versionPath)) File.Delete(versionPath); });
            File.WriteAllText(versionPath, WorldFiles.VersionJson(version));
        }
        catch (Exception error)
        {
            StageApply.Log(install, $"World update from {stage} failed, putting the dats back: {error}");
            try
            {
                while (undo.Count > 0) undo.Pop()();
                foreach (var copy in copies) if (File.Exists(copy)) File.Delete(copy);
                if (Directory.Exists(rollback)) Directory.Delete(rollback, true);
                File.WriteAllText(Path.Combine(stage, WorldFiles.FailedFile), error.Message);
                StageApply.Log(install, "World dats unchanged; the plugin won't try this patch again");
            }
            catch (Exception rollbackError)
            {
                // The moved-aside originals stay in rollback\ for a person to restore.
                StageApply.Log(install, $"Putting the world dats back failed, originals are in {rollback}: {rollbackError}");
            }
            return ApplyResult.Failed;
        }

        StageApply.Log(install, $"Applied world patch {version} from {stage}");
        Clean(install, stage);
        return ApplyResult.Applied;
    }

    /// <summary>Writes every record into the dat at <paramref name="path"/>; throws on the first that won't write.</summary>
    private static void Write(string path, IGrouping<string, WorldPatchRecord> records)
    {
        using var dat = new DatDatabase(options =>
        {
            options.FilePath = path;
            options.AccessType = DatAccessType.ReadWrite;
        });
        foreach (var record in records)
        {
            var written = record.Compressed
                ? dat.TryWriteCompressedBytes(record.Id, record.Data, record.Data.Length, record.Iteration)
                : dat.TryWriteFileBytes(record.Id, record.Data, record.Data.Length, record.Iteration);
            if (!written) throw new IOException($"could not write record {record.Id:X8} into {records.Key}");
        }
    }

    private static void Swap(string copy, string original, string rollback, Stack<Action> undo)
    {
        var saved = Path.Combine(rollback, Path.GetFileName(original));
        File.Move(original, saved);
        undo.Push(() => { if (File.Exists(original)) File.Delete(original); File.Move(saved, original); });
        File.Move(copy, original);
    }

    /// <summary>
    /// Deletes the applied stage: ready first, so a stage only partly deleted is never applied again. The running updater's own exe stays,
    /// for the plugin to remove with the folder.
    /// </summary>
    private static void Clean(string install, string stage)
    {
        try
        {
            File.Delete(Path.Combine(stage, WorldFiles.ReadyFile));
            var rollback = Path.Combine(stage, "rollback");
            if (Directory.Exists(rollback)) Directory.Delete(rollback, true);
            foreach (var file in Directory.GetFiles(stage))
            {
                try { File.Delete(file); }
                catch (Exception) { } // The running updater's own exe.
            }
        }
        catch (Exception error) { StageApply.Log(install, $"Could not delete the applied world stage: {error.Message}"); }
    }
}
