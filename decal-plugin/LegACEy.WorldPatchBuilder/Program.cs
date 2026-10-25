using DatReaderWriter;
using DatReaderWriter.Options;
using LegACEy.Client.DecalPlugin;

// build <retail> <edited> <out> <url folder>: writes <out>/world-<edition>.patch, every record of <edited>'s dats that differs from
//   <retail>'s (their edition records too), and <out>/world-manifest.json pointing at <url folder>/world-<edition>.patch. The world
//   version is the edited cell dat's edition, which the server announces; it must be above retail's.
// check <edited> <patch>: reads every record back through the index the client's dat overlay serves it with and compares it with
//   <edited>'s, and checks the patch carries the cell dat's edition record at the world version. A developer check.
switch (args)
{
    case ["build", var retail, var edited, var output, var url]:
        return Build(retail, edited, output, url);
    case ["check", var edited, var patch]:
        return Check(edited, patch);
    default:
        Console.Error.WriteLine("usage: build <retail> <edited> <out> <url folder> | check <edited> <patch>");
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
        int changed = 0, added = 0, textures = 0;
        long textureBytes = 0;
        var kept = new HashSet<uint>();
        foreach (var file in after.Tree)
        {
            kept.Add(file.Id);
            if (!after.TryGetFileBytes(file.Id, out var bytes) || bytes == null) throw new IOException($"{name}: can't read {file.Id:X8}");
            var old = before.TryGetFileBytes(file.Id, out var retailBytes) ? retailBytes : null;
            if (old != null && old.AsSpan().SequenceEqual(bytes)) continue;
            if (old == null) added++;
            else changed++;
            if (file.Id >> 24 == 0x06 && name != Cell) (textures, textureBytes) = (textures + 1, textureBytes + bytes.Length);
            if (old != null && WorldPatch.HeldAtSwitchOn(name, file.Id))
                Console.Error.WriteLine($"warning: {name} {file.Id:X8} ({bytes.Length} bytes): the game already holds it when a world switches on, so "
                    + (file.Id >> 24 is 0x22 or 0x25 or 0x26 or 0x27 ? "players may never see the new one: mappers are held from plugin start."
                        : "players see the new one only after a relaunch."));
            records.Add(new WorldPatchRecord(name, file.Id, file.Iteration, (ushort)file.Version, bytes));
        }
        var removed = before.Tree.Count(file => !kept.Contains(file.Id));
        Console.WriteLine($"{name}: {changed} changed, {added} new" + (removed > 0 ? $", {removed} removed in the edit and kept by the patch" : "")
            + (textures > 0 ? $"; {textures} textures, {textureBytes} bytes uncompressed" : ""));
    }

    Directory.CreateDirectory(output);
    var patchName = $"world-{version}.patch";
    var patchPath = Path.Combine(output, patchName);
    using (var stream = File.Create(patchPath)) new WorldPatch(version, records).Write(stream);
    var size = new FileInfo(patchPath).Length;
    var sha = WorldPatch.Sha256(patchPath);
    File.WriteAllText(Path.Combine(output, "world-manifest.json"),
        $$"""{ "version": {{version}}, "patch": "{{url.TrimEnd('/')}}/{{patchName}}", "size": {{size}}, "sha256": "{{sha}}" }""" + "\n");
    Console.WriteLine($"{patchPath}: {records.Count} records, {size} bytes, sha256 {sha}");
    return 0;
}

static int Check(string edited, string patchPath)
{
    const string Cell = "client_cell_1.dat";
    const uint Edition = 0xFFFF0001;
    using var patch = WorldPatchIndex.Open(patchPath);
    var different = 0;
    foreach (var dat in patch.Dats)
    {
        using var expected = Open(Path.Combine(edited, dat));
        foreach (var (id, entry) in patch.Records(dat)!)
        {
            if (expected.TryGetFileBytes(id, out var want) && patch.Read(entry).AsSpan().SequenceEqual(want)) continue;
            different++;
            Console.Error.WriteLine($"{dat} {id:X8} differs from the edited dat");
        }
    }
    int edition;
    using (var cell = Open(Path.Combine(edited, Cell))) edition = cell.Iteration.CurrentIteration;
    if (patch.Records(Cell)?.ContainsKey(Edition) != true || edition != patch.Version)
    {
        different++;
        Console.Error.WriteLine($"The patch doesn't carry {Cell}'s edition record {Edition:X8} at world {patch.Version} (the edited cell dat is at {edition}).");
    }
    Console.WriteLine(different == 0 ? $"All {patch.Count} records read back as the edited dats have them; the cell dat's edition is {edition}." : $"{different} records differ.");
    return different == 0 ? 0 : 1;
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
