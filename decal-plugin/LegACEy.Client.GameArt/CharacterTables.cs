using System;
using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.GameArt;

/// <summary>A skill as the portal DAT's skill table describes it: its name, icon, credit costs, and what it is trained from.</summary>
public sealed class SkillDefinition
{
    public SkillDefinition(uint id, string name, string description, uint iconId, int trainedCost, int specializedCost, uint minLevel,
        uint attribute1, uint attribute2, uint divisor, bool hasFormula)
    {
        Id = id;
        Name = name;
        Description = description;
        IconId = iconId;
        TrainedCost = trainedCost;
        SpecializedCost = specializedCost;
        MinLevel = minLevel;
        Attribute1 = attribute1;
        Attribute2 = attribute2;
        Divisor = divisor;
        HasFormula = hasFormula;
    }

    public uint Id { get; }
    public string Name { get; }
    public string Description { get; }
    public uint IconId { get; }
    /// <summary>The skill credits training costs; the train action must send exactly this.</summary>
    public int TrainedCost { get; }
    public int SpecializedCost { get; }
    /// <summary>1: usable untrained; 2: unusable until trained (retail's Unusable Skills group).</summary>
    public uint MinLevel { get; }
    /// <summary>The attributes the skill is trained from (ACE's attribute ids, 1 Strength to 6 Self; 0 none) and their divisor.</summary>
    public uint Attribute1 { get; }
    public uint Attribute2 { get; }
    public uint Divisor { get; }
    /// <summary>False for a skill that takes nothing from the attributes (Deception, Loyalty).</summary>
    public bool HasFormula { get; }
}

/// <summary>
/// The portal DAT's character tables the character window reads: the skill table (0x0E000004), the experience table (0x0E000018), and
/// the attribute and vital icon mappers (0x25000006, 0x25000007). Read as ACE.DatLoader's SkillTable, SkillBase, XpTable and DidMapper read them.
/// </summary>
public sealed class CharacterTables
{
    public const uint SkillTableId = 0x0E000004;
    public const uint XpTableId = 0x0E000018;
    public const uint AttributeIconMapper = 0x25000006;
    public const uint VitalIconMapper = 0x25000007;

    public static readonly CharacterTables None = new(new Dictionary<uint, SkillDefinition>(), new Xp(), new Dictionary<uint, uint>(), new Dictionary<uint, uint>());

    public CharacterTables(IReadOnlyDictionary<uint, SkillDefinition> skills, Xp experience, IReadOnlyDictionary<uint, uint> attributeIcons,
        IReadOnlyDictionary<uint, uint> vitalIcons)
    {
        Skills = skills;
        Experience = experience;
        AttributeIcons = attributeIcons;
        VitalIcons = vitalIcons;
    }

    /// <summary>The named skills by ACE's skill id. Retired skills (no name in the DAT) are left out.</summary>
    public IReadOnlyDictionary<uint, SkillDefinition> Skills { get; }
    public Xp Experience { get; }
    /// <summary>Icon by attribute id (1 Strength to 6 Self).</summary>
    public IReadOnlyDictionary<uint, uint> AttributeIcons { get; }
    /// <summary>Icon by vital id as the mapper keys it (1 max health ... 6 mana).</summary>
    public IReadOnlyDictionary<uint, uint> VitalIcons { get; }

    /// <summary>The experience table's lists: the total experience each rank costs from rank 0. Index n is rank n.</summary>
    public sealed class Xp
    {
        public uint[] Attribute { get; set; } = Array.Empty<uint>();
        public uint[] Vital { get; set; } = Array.Empty<uint>();
        public uint[] TrainedSkill { get; set; } = Array.Empty<uint>();
        public uint[] SpecializedSkill { get; set; } = Array.Empty<uint>();
        public ulong[] Level { get; set; } = Array.Empty<ulong>();
    }

    public static Dictionary<uint, SkillDefinition> ParseSkills(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the table's own id
        var count = reader.ReadUInt16();
        reader.ReadUInt16(); // the bucket size
        var skills = new Dictionary<uint, SkillDefinition>(count);
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadUInt32();
            var description = ReadString(reader);
            Align(reader);
            var name = ReadString(reader);
            Align(reader);
            var icon = reader.ReadUInt32();
            var trained = reader.ReadInt32();
            var specialized = reader.ReadInt32();
            reader.ReadUInt32(); // category
            reader.ReadUInt32(); // chargen use
            var minLevel = reader.ReadUInt32();
            reader.ReadUInt32(); // formula W
            var x = reader.ReadUInt32();
            reader.ReadUInt32(); // formula Y
            var divisor = reader.ReadUInt32();
            var attribute1 = reader.ReadUInt32();
            var attribute2 = reader.ReadUInt32();
            reader.BaseStream.Seek(3 * 8, SeekOrigin.Current); // upper and lower bound, learn mod
            if (name.Length == 0) continue;
            skills[id] = new SkillDefinition(id, name, description, icon, trained, specialized, minLevel, attribute1, attribute2, divisor, x != 0);
        }
        return skills;
    }

    public static Xp ParseXp(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the table's own id
        var attributes = reader.ReadInt32();
        var vitals = reader.ReadInt32();
        var trained = reader.ReadInt32();
        var specialized = reader.ReadInt32();
        var levels = reader.ReadInt32();
        // Each count is the last rank: the list holds one more entry, rank 0.
        uint[] List(int last)
        {
            var list = new uint[last + 1];
            for (var i = 0; i < list.Length; i++) list[i] = reader.ReadUInt32();
            return list;
        }
        var xp = new Xp { Attribute = List(attributes), Vital = List(vitals), TrainedSkill = List(trained), SpecializedSkill = List(specialized) };
        xp.Level = new ulong[levels + 1];
        for (var i = 0; i < xp.Level.Length; i++) xp.Level[i] = reader.ReadUInt64();
        return xp;
    }

    /// <summary>A DID mapper's enum-to-id list: its own id, a numbering byte, a compressed count of (enum, data id) pairs.</summary>
    public static Dictionary<uint, uint> ParseDidMapper(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.ReadUInt32(); // the mapper's own id
        reader.ReadByte(); // the numbering type
        var count = DatModels.ReadCompressed(reader);
        var ids = new Dictionary<uint, uint>((int)count);
        for (var i = 0; i < count; i++) ids[reader.ReadUInt32()] = reader.ReadUInt32();
        return ids;
    }

    /// <summary>A 16-bit length, then Windows-1252 bytes.</summary>
    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var bytes = reader.ReadBytes(length);
        if (bytes.Length < length) throw new EndOfStreamException("The skill table ends inside a string.");
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = Windows1252.ToChar(bytes[i]);
        return new string(chars);
    }

    private static void Align(BinaryReader reader)
    {
        var over = reader.BaseStream.Position % 4;
        if (over != 0) reader.BaseStream.Seek(4 - over, SeekOrigin.Current);
    }
}
