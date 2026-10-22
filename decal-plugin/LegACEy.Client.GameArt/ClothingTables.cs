using System;
using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.GameArt;

/// <summary>A clothing table's effect on one body setup: for each part it changes, the model it puts there and its texture swaps.</summary>
public sealed class ClothingEffect
{
    public ClothingEffect(uint table, IReadOnlyDictionary<int, ClothingPart> parts)
    {
        Table = table;
        Parts = parts;
    }

    /// <summary>The clothing table's id (0x10…).</summary>
    public uint Table { get; }
    public IReadOnlyDictionary<int, ClothingPart> Parts { get; }
}

public sealed class ClothingPart
{
    public ClothingPart(uint model, IReadOnlyList<TextureChange> textures)
    {
        Model = model;
        Textures = textures;
    }

    public uint Model { get; }
    public IReadOnlyList<TextureChange> Textures { get; }
}

/// <summary>
/// The clothing tables in portal.dat, which ACE builds a character's look from: each worn item's table changes the parts it covers,
/// a later item's over an earlier one's. The look the client is sent merges them all, so which item drew a part is never said;
/// <see cref="PartsDrawn"/> works it out from the tables whose changes the look still shows.
/// </summary>
public sealed class ClothingTables
{
    private const uint First = 0x10000000, Last = 0x10FFFFFF;
    private readonly IReadOnlyList<uint> _baseParts;

    private ClothingTables(IReadOnlyList<uint> baseParts, IReadOnlyList<ClothingEffect> effects)
    {
        _baseParts = baseParts;
        Effects = effects;
    }

    /// <summary>Every clothing table's effect on the setup; tables without one are left out.</summary>
    public IReadOnlyList<ClothingEffect> Effects { get; }

    /// <summary>The clothing tables' effects on a body setup, and its own part models. A few thousand small reads.</summary>
    public static ClothingTables Read(PortalDat dat, uint setupId)
    {
        var effects = new List<ClothingEffect>();
        foreach (var id in dat.FileIds(First, Last))
        {
            var data = dat.ReadFile(id);
            if (data == null) continue;
            try
            {
                if (ReadEffect(new BinaryReader(new MemoryStream(data, writable: false)), setupId) is { } parts)
                    effects.Add(new ClothingEffect(id, parts));
            }
            catch (EndOfStreamException) { } // a table this reader can't follow is skipped
        }
        return new ClothingTables(DatModels.ReadSetup(dat, setupId)?.Parts ?? Array.Empty<uint>(), effects);
    }

    // u32 id, then a packed table (u16 count, u16 buckets) of u32 setup -> (u32 count of (u32 part, u32 model, u32 count of (u32 old, u32 new)))
    private static Dictionary<int, ClothingPart>? ReadEffect(BinaryReader reader, uint setupId)
    {
        reader.ReadUInt32();
        int setups = reader.ReadUInt16();
        reader.ReadUInt16();
        for (var s = 0; s < setups; s++)
        {
            var setup = reader.ReadUInt32();
            var parts = new Dictionary<int, ClothingPart>();
            for (int p = 0, count = reader.ReadInt32(); p < count; p++)
            {
                var part = (int)reader.ReadUInt32();
                var model = reader.ReadUInt32();
                var textures = new TextureChange[reader.ReadInt32()];
                for (var t = 0; t < textures.Length; t++)
                    textures[t] = new TextureChange(part, reader.ReadUInt32(), reader.ReadUInt32());
                parts[part] = new ClothingPart(model, textures);
            }
            if (setup == setupId) return parts;
        }
        return null;
    }

    /// <summary>
    /// The parts each worn item draws in the look. <paramref name="claims"/> holds, for each worn item, the parts its coverage names,
    /// in the order the look draws the items, under to over. Every table whose changes the look shows is the item's whose claim it
    /// shows on most, the later on a tie; of its tables, an item takes those that show
    /// on the most of its claim and then explain the most of the look. Its parts are all those its tables show, past its claim: knee-high
    /// boots, named for the feet, draw the shins too, and shoes drawing the same feet would leave those shins unexplained. An item none of
    /// whose tables show, as a shirt under a coat, draws nothing.
    /// </summary>
    public IReadOnlyList<IReadOnlyCollection<int>> PartsDrawn(CharacterAppearance look, IReadOnlyList<IReadOnlyCollection<int>> claims)
    {
        // Where nothing is worn, the look shows the setup's own part models.
        var baseParts = _baseParts;
        var models = new Dictionary<int, uint>();
        foreach (var change in look.PartChanges) models[change.Part] = change.Model;
        // One swap per part and original texture, the later over the earlier, as the model is built.
        var swaps = new Dictionary<(int Part, uint Old), uint>();
        foreach (var change in look.TextureChanges) swaps[(change.Part, change.OldTexture)] = change.NewTexture;

        bool Shows(ClothingPart drawn, int part)
        {
            var shown = models.TryGetValue(part, out var model) ? model : part < baseParts.Count ? baseParts[part] : 0;
            if (drawn.Model != shown) return false;
            var own = part >= baseParts.Count || drawn.Model != baseParts[part];
            foreach (var texture in drawn.Textures)
            {
                if (!swaps.TryGetValue((part, texture.OldTexture), out var now) || now != texture.NewTexture) return false;
                own |= texture.NewTexture != texture.OldTexture;
            }
            // A table that leaves a part as the body has it shows nothing of its own there.
            return own;
        }

        var claimed = new List<HashSet<int>>();
        foreach (var claim in claims) claimed.Add(new HashSet<int>(claim));
        var best = new (int On, int Shown)[claims.Count];
        var parts = new SortedSet<int>[claims.Count];
        for (var i = 0; i < parts.Length; i++) parts[i] = new SortedSet<int>();
        foreach (var effect in Effects)
        {
            var shown = new List<int>();
            foreach (var pair in effect.Parts)
                if (Shows(pair.Value, pair.Key)) shown.Add(pair.Key);
            if (shown.Count == 0) continue;
            var owner = -1;
            var most = 0;
            for (var i = 0; i < claimed.Count; i++)
            {
                var on = 0;
                foreach (var part in shown)
                    if (claimed[i].Contains(part)) on++;
                // A tie goes to the later claim, the item drawn over the other: a coat over a shirt worn on the same parts.
                if (on > 0 && on >= most) (owner, most) = (i, on);
            }
            if (owner < 0) continue;
            var score = (most, shown.Count);
            if (score.most < best[owner].On || score.most == best[owner].On && score.Count < best[owner].Shown) continue;
            if (score.most > best[owner].On || score.Count > best[owner].Shown)
            {
                best[owner] = (score.most, score.Count);
                parts[owner].Clear();
            }
            parts[owner].UnionWith(shown);
        }
        return parts;
    }
}
