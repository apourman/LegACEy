using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>Armour protections by damage type, as the server's ArmorProfile writes them.</summary>
public sealed record ArmorProfile(float Slashing, float Piercing, float Bludgeoning, float Cold, float Fire, float Acid, float Nether, float Lightning);

/// <summary>A highlight and colour mask pair as the server sends it: which values a buff or debuff changed, and whether each went up (green) or down (red).</summary>
public sealed record HighlightMask(ushort Highlight, ushort Color);

/// <summary>
/// The creature profile. <see cref="Attributes"/> are Strength, Endurance, Quickness, Coordination, Focus, Self, Stamina, Mana, maximum
/// Stamina and maximum Mana, sent only when <see cref="Flags"/> has the attributes bit (0x8). <see cref="AttributeMask"/> is sent when
/// the buff bit (0x1) is set, and is null otherwise.
/// </summary>
public sealed record CreatureProfile(uint Flags, uint Health, uint HealthMax, IReadOnlyList<uint> Attributes, HighlightMask? AttributeMask);

/// <summary>The weapon profile: damage type and range, speed, skill, and the bonuses, all with the server's enchantments applied.</summary>
public sealed record WeaponProfile(uint DamageType, uint WeaponTime, uint WeaponSkill, uint Damage, double DamageVariance, double DamageMod,
    double WeaponLength, double MaxVelocity, double WeaponOffense, uint MaxVelocityEstimated);

/// <summary>What the hook accepts: the hook flags, the wield locations and the ammunition type.</summary>
public sealed record HookProfile(uint Flags, uint ValidLocations, uint AmmoType);

/// <summary>The armour level of each body part, as the character panel shows it.</summary>
public sealed record ArmorLevels(uint Head, uint Chest, uint Abdomen, uint UpperArm, uint LowerArm, uint Hand, uint UpperLeg, uint LowerLeg, uint Foot);

/// <summary>
/// An object's appraisal: game event 0x00C9 (IdentifyObjectResponse) read as the server's AppraiseInfo writes it, in the same order, so
/// every part is consumed and the next one reads aligned. Property tables are keyed by the server's property ids (PropertyInt,
/// PropertyString, ...); the formatting is in <see cref="AppraisalContent"/>.
/// </summary>
public sealed class AppraisalResponse
{
    /// <summary>The game event type of IdentifyObjectResponse.</summary>
    public const uint IdentifyEvent = 0x00C9;

    // IdentifyResponseFlags: which parts follow the success flag.
    private const uint IntTable = 0x0001, BoolTable = 0x0002, FloatTable = 0x0004, StringTable = 0x0008, SpellBookPart = 0x0010,
        WeaponPart = 0x0020, HookPart = 0x0040, ArmorPart = 0x0080, CreaturePart = 0x0100, ArmorEnchantment = 0x0200,
        ResistEnchantment = 0x0400, WeaponEnchantment = 0x0800, DidTable = 0x1000, Int64Table = 0x2000, ArmorLevelsPart = 0x4000;

    // CreatureProfileFlags.
    private const uint BuffsDebuffs = 0x1, ShowAttributes = 0x8;

    public uint ObjectId { get; private set; }
    public uint Flags { get; private set; }
    public bool Success { get; private set; }
    public IReadOnlyDictionary<uint, int> Ints { get; private set; } = new Dictionary<uint, int>();
    public IReadOnlyDictionary<uint, long> Int64s { get; private set; } = new Dictionary<uint, long>();
    public IReadOnlyDictionary<uint, bool> Bools { get; private set; } = new Dictionary<uint, bool>();
    public IReadOnlyDictionary<uint, double> Floats { get; private set; } = new Dictionary<uint, double>();
    public IReadOnlyDictionary<uint, string> Strings { get; private set; } = new Dictionary<uint, string>();
    public IReadOnlyDictionary<uint, uint> Dids { get; private set; } = new Dictionary<uint, uint>();
    /// <summary>The spells the object lists, in the server's order, each a spell id (an enchantment's id carries the server's high bit).</summary>
    public IReadOnlyList<uint> SpellBook { get; private set; } = new List<uint>();
    public ArmorProfile? Armor { get; private set; }
    public CreatureProfile? Creature { get; private set; }
    public WeaponProfile? Weapon { get; private set; }
    public HookProfile? Hook { get; private set; }
    public HighlightMask? ArmorMask { get; private set; }
    public HighlightMask? WeaponMask { get; private set; }
    public HighlightMask? ResistMask { get; private set; }
    public ArmorLevels? Levels { get; private set; }

    /// <summary>Reads the payload of a 0x00C9 event: the object's id, then the appraisal. Throws <see cref="EndOfStreamException"/> when the payload is short.</summary>
    public static AppraisalResponse Read(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        var objectId = reader.ReadUInt32();
        var flags = reader.ReadUInt32();
        var success = reader.ReadUInt32() != 0;
        var response = new AppraisalResponse { ObjectId = objectId, Flags = flags, Success = success };

        // Each part is read in the server's order, whether or not the caller formats it.
        if (Has(flags, IntTable)) response.Ints = ReadTable(reader, r => r.ReadInt32());
        if (Has(flags, Int64Table)) response.Int64s = ReadTable(reader, r => r.ReadInt64());
        if (Has(flags, BoolTable)) response.Bools = ReadTable(reader, r => r.ReadUInt32() != 0);
        if (Has(flags, FloatTable)) response.Floats = ReadTable(reader, r => r.ReadDouble());
        if (Has(flags, StringTable)) response.Strings = ReadTable(reader, ReadString16L);
        if (Has(flags, DidTable)) response.Dids = ReadTable(reader, r => r.ReadUInt32());
        if (Has(flags, SpellBookPart)) response.SpellBook = ReadList(reader);
        if (Has(flags, ArmorPart)) response.Armor = new ArmorProfile(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (Has(flags, CreaturePart)) response.Creature = ReadCreature(reader);
        if (Has(flags, WeaponPart)) response.Weapon = new WeaponProfile(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
            reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadUInt32());
        if (Has(flags, HookPart)) response.Hook = new HookProfile(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        if (Has(flags, ArmorEnchantment)) response.ArmorMask = ReadMask(reader);
        if (Has(flags, WeaponEnchantment)) response.WeaponMask = ReadMask(reader);
        if (Has(flags, ResistEnchantment)) response.ResistMask = ReadMask(reader);
        if (Has(flags, ArmorLevelsPart)) response.Levels = new ArmorLevels(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        return response;
    }

    private static bool Has(uint flags, uint part) => (flags & part) != 0;

    /// <summary>A property table: a 16-bit count, a 16-bit bucket count (unused here), then each key with its value.</summary>
    private static Dictionary<uint, T> ReadTable<T>(BinaryReader reader, Func<BinaryReader, T> value)
    {
        var count = reader.ReadUInt16();
        reader.ReadUInt16();
        var table = new Dictionary<uint, T>(count);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadUInt32();
            table[key] = value(reader);
        }
        return table;
    }

    /// <summary>
    /// A 16-bit length, the bytes, then padding so the whole string is a multiple of four bytes. The server writes Windows-1252; each byte
    /// is read as its Windows-1252 character, the table the spell table reads with.
    /// </summary>
    private static string ReadString16L(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var bytes = ReadExactly(reader, length);
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = Windows1252.ToChar(bytes[i]);
        var padding = (4 - (2 + length) % 4) % 4;
        ReadExactly(reader, padding);
        return new string(chars);
    }

    /// <summary>The bytes asked for. A payload that ends early is malformed, as a short read of a number is.</summary>
    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length < count) throw new EndOfStreamException($"The appraisal ends {count - bytes.Length} bytes early.");
        return bytes;
    }

    /// <summary>A 32-bit count, then each spell id. A count that is negative or needs more bytes than the payload has is malformed.</summary>
    private static List<uint> ReadList(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (count < 0 || count > remaining / 4) throw new InvalidDataException($"The spell book count {count} does not fit the appraisal.");
        var list = new List<uint>(count);
        for (var i = 0; i < count; i++) list.Add(reader.ReadUInt32());
        return list;
    }

    /// <summary>A highlight mask then a colour mask, each 16 bits.</summary>
    private static HighlightMask ReadMask(BinaryReader reader) => new(reader.ReadUInt16(), reader.ReadUInt16());

    private static CreatureProfile ReadCreature(BinaryReader reader)
    {
        var flags = reader.ReadUInt32();
        var health = reader.ReadUInt32();
        var healthMax = reader.ReadUInt32();
        var attributes = new List<uint>();
        if ((flags & ShowAttributes) != 0)
            for (var i = 0; i < 10; i++) attributes.Add(reader.ReadUInt32());
        var mask = (flags & BuffsDebuffs) != 0 ? ReadMask(reader) : null;
        return new CreatureProfile(flags, health, healthMax, attributes, mask);
    }

}
