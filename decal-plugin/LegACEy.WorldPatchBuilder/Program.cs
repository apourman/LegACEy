using DatReaderWriter;
using DatReaderWriter.Lib.IO.DatBTree;
using DatReaderWriter.Options;
using LegACEy.Updater;

// build <retail> <edited> <out> <url folder>: writes <out>/world-<edition>.patch, every record of <edited>'s dats that differs from
//   <retail>'s (their edition records too), and <out>/world-manifest.json pointing at <url folder>/world-<edition>.patch. The world
//   version is the edited cell dat's edition, which the server announces; it must be above retail's.
// check <retail> <edited> <patch>: applies the patch to a copy of <retail> with the updater's own apply, then compares every patched
//   record with <edited>. A developer check; it copies the patched dats to a temporary folder.
switch (args)
{
    case ["build", var retail, var edited, var output, var url]:
        return Build(retail, edited, output, url);
    case ["check", var retail, var edited, var patch]:
        return Check(retail, edited, patch);
    default:
        Console.Error.WriteLine("usage: build <retail> <edited> <out> <url folder> | check <retail> <edited> <patch>");
        return 2;
}

static int Build(string retail, string edited, string output, string url)
{
    const string Cell = "client_cell_1.dat";
    int version, retailEdition;
    using (var before = Open(Path.Combine(retail, Cell))) retailEdition = before.Iteration.CurrentIteration;
    using (var after = Open(Path.Combine(edited, Cell))) version = after.Iteration.CurrentIteration;
    if (version <= retailEdition)
    {
        Console.Error.WriteLine($"{Cell} is at edition {version}, retail's is {retailEdition}: raise the edited cell dat's edition, which is the world version.");
        return 1;
    }
    var records = new List<WorldPatchRecord>();
    foreach (var path in Directory.GetFiles(edited, "client_*.dat").Order())
    {
        var name = Path.GetFileName(path);
        if (SameBytes(path, Path.Combine(retail, name)))
        {
            Console.WriteLine($"{name}: unchanged");
            continue;
        }
        using var before = Open(Path.Combine(retail, name));
        using var after = Open(path);
        int changed = 0, added = 0;
        var kept = new HashSet<uint>();
        foreach (var file in after.Tree)
        {
            kept.Add(file.Id);
            if (!after.TryGetFileBytes(file.Id, out var bytes) || bytes == null) throw new IOException($"{name}: can't read {file.Id:X8}");
            var old = before.TryGetFileBytes(file.Id, out var retailBytes) ? retailBytes : null;
            if (old != null && old.AsSpan().SequenceEqual(bytes)) continue;
            if (old == null) added++;
            else changed++;
            records.Add(new WorldPatchRecord(name, file.Id, file.Iteration, file.Flags.HasFlag(DatBTreeFileFlags.IsCompressed), bytes));
        }
        var removed = before.Tree.Count(file => !kept.Contains(file.Id));
        Console.WriteLine($"{name}: {changed} changed, {added} new" + (removed > 0 ? $", {removed} removed in the edit and kept by the patch" : ""));
    }

    Directory.CreateDirectory(output);
    var patchName = $"world-{version}.patch";
    var patchPath = Path.Combine(output, patchName);
    using (var stream = File.Create(patchPath)) new WorldPatch(version, records).Write(stream);
    var size = new FileInfo(patchPath).Length;
    var sha = WorldFiles.Sha256(patchPath);
    File.WriteAllText(Path.Combine(output, "world-manifest.json"),
        $$"""{ "version": {{version}}, "patch": "{{url.TrimEnd('/')}}/{{patchName}}", "size": {{size}}, "sha256": "{{sha}}" }""" + "\n");
    Console.WriteLine($"{patchPath}: {records.Count} records, {size} bytes, sha256 {sha}");
    return 0;
}

static int Check(string retail, string edited, string patchPath)
{
    WorldPatch patch;
    using (var stream = File.OpenRead(patchPath)) patch = WorldPatch.Read(stream);
    var game = Path.Combine(Path.GetTempPath(), "legacey-world-check-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(game);
        foreach (var dat in patch.Records.Select(record => record.Dat).Distinct()) File.Copy(Path.Combine(retail, dat), Path.Combine(game, dat));
        var stage = WorldFiles.Stage(game, patch.Version);
        Directory.CreateDirectory(stage);
        File.Copy(patchPath, Path.Combine(stage, WorldFiles.PatchFile));
        File.WriteAllText(Path.Combine(stage, WorldFiles.ReadyFile), WorldFiles.Sha256(patchPath));
        var result = WorldApply.Apply(game, game, stage);
        if (result != ApplyResult.Applied || WorldFiles.ReadVersion(game) != patch.Version)
        {
            Console.Error.WriteLine($"apply: {result}, world version {WorldFiles.ReadVersion(game)}; log:\n{File.ReadAllText(Path.Combine(game, "updater.log"))}");
            return 1;
        }
        var different = 0;
        foreach (var dat in patch.Records.GroupBy(record => record.Dat))
        {
            using var patched = Open(Path.Combine(game, dat.Key));
            using var expected = Open(Path.Combine(edited, dat.Key));
            foreach (var record in dat)
            {
                if (patched.TryGetFileBytes(record.Id, out var got) && expected.TryGetFileBytes(record.Id, out var want) && got.AsSpan().SequenceEqual(want))
                    continue;
                different++;
                Console.Error.WriteLine($"{dat.Key} {record.Id:X8} differs after the patch");
            }
        }
        int edition;
        using (var cell = Open(Path.Combine(game, "client_cell_1.dat"))) edition = cell.Iteration.CurrentIteration;
        if (edition != patch.Version)
        {
            different++;
            Console.Error.WriteLine($"The patched cell dat is at edition {edition}, not the world version {patch.Version}");
        }
        Console.WriteLine(different == 0 ? $"All {patch.Records.Count} patched records match the edited dats; the cell dat is at edition {edition}." : $"{different} records differ.");
        return different == 0 ? 0 : 1;
    }
    finally { Directory.Delete(game, true); }
}

static DatDatabase Open(string path) => new(options =>
{
    options.FilePath = path;
    options.AccessType = DatAccessType.Read;
});

static bool SameBytes(string a, string b)
{
    using var left = File.OpenRead(a);
    using var right = File.OpenRead(b);
    if (left.Length != right.Length) return false;
    var x = new byte[1 << 20];
    var y = new byte[1 << 20];
    int read;
    while ((read = left.ReadAtLeast(x, x.Length, throwOnEndOfStream: false)) > 0)
        if (right.ReadAtLeast(y, read, throwOnEndOfStream: false) != read || !x.AsSpan(0, read).SequenceEqual(y.AsSpan(0, read))) return false;
    return true;
}
