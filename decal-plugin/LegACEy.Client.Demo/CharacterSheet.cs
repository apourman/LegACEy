using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>How far a skill is advanced, as ACE's SkillAdvancementClass numbers it.</summary>
public enum SkillTraining { None = 0, Untrained = 1, Trained = 2, Specialized = 3 }

/// <summary>What has been bought of an attribute, vital or skill: its ranks, its starting value, and the experience spent on it.</summary>
public readonly record struct Advancement(uint Ranks, uint Start, uint Experience);

/// <summary>A skill as the server describes it: its advancement and its training.</summary>
public readonly record struct SkillAdvancement(Advancement Advancement, SkillTraining Training);

/// <summary>
/// The player's character as the server reports it: what each attribute, vital and skill has been advanced, the experience and skill
/// credits, the level, and the titles. Read from the login's PlayerDescription and kept current by the private updates and title events
/// that follow (ACE's GameEventPlayerDescription, GameMessagePrivateUpdate*, GameEventCharacterTitle and GameEventUpdateTitle). The values
/// a player sees, with buffs and augmentations, are Decal's; this holds what the raise and train actions need.
/// </summary>
public sealed class CharacterSheet
{
    public const uint PlayerDescriptionEvent = 0x0013, CharacterTitleEvent = 0x0029, UpdateTitleEvent = 0x002B;
    private const int GameEventMessage = 0xF7B0;
    private const int UpdateInt = 0x02CD, UpdateInt64 = 0x02CF, UpdateSkill = 0x02DD, UpdateAttribute = 0x02E3, UpdateVital = 0x02E7;
    // ACE's PropertyInt and PropertyInt64 ids the window shows.
    private const uint SkillCreditsProperty = 24, LevelProperty = 25, PlayerKillerProperty = 134;
    private const uint TotalExperienceProperty = 1, UnassignedExperienceProperty = 2;

    private readonly Dictionary<uint, Advancement> _attributes = new();
    private readonly Dictionary<uint, Advancement> _vitals = new();
    private readonly Dictionary<uint, SkillAdvancement> _skills = new();
    private readonly List<uint> _titles = new();
    // Ranks a raise sent from this client bought that the server has not confirmed yet, by kind and id: Decal's values lag by them.
    private readonly Dictionary<(StatKind, uint), uint> _unconfirmed = new();

    /// <summary>Whether a PlayerDescription has been read this session.</summary>
    public bool Known { get; private set; }
    public int Level { get; private set; }
    public int SkillCredits { get; private set; }
    public long TotalExperience { get; private set; }
    public long UnassignedExperience { get; private set; }
    /// <summary>ACE's PlayerKillerStatus bits (2 non-player killer, 4 player killer, 0x40 player killer lite).</summary>
    public int PlayerKillerStatus { get; private set; }
    /// <summary>The title shown under the name, 0 for none.</summary>
    public uint DisplayTitle { get; private set; }
    public IReadOnlyList<uint> Titles => _titles;

    /// <summary>By attribute id, 1 Strength to 6 Self.</summary>
    public IReadOnlyDictionary<uint, Advancement> Attributes => _attributes;
    /// <summary>By ACE's vital id: 1 max health, 3 max stamina, 5 max mana.</summary>
    public IReadOnlyDictionary<uint, Advancement> Vitals => _vitals;
    /// <summary>By ACE's skill id.</summary>
    public IReadOnlyDictionary<uint, SkillAdvancement> Skills => _skills;

    /// <summary>Raised after a message changed the sheet.</summary>
    public event Action? Changed;

    /// <summary>The ranks raises sent from this client bought that the server has not confirmed yet.</summary>
    public uint Unconfirmed(StatKind kind, uint id) => _unconfirmed.TryGetValue((kind, id), out var ranks) ? ranks : 0;

    /// <summary>
    /// A raise this client just sent, applied at once as the server will apply it, so the next click costs the next rank, as in retail:
    /// <paramref name="experience"/> leaves the unassigned pool and buys every rank of <paramref name="costs"/> it reaches. The server's
    /// update for the row replaces it.
    /// </summary>
    public void Spend(StatKind kind, uint id, uint experience, uint[] costs)
    {
        var rows = kind switch { StatKind.Attribute => _attributes, StatKind.Vital => _vitals, _ => null };
        Advancement before;
        if (rows != null) rows.TryGetValue(id, out before);
        else before = _skills.TryGetValue(id, out var skill) ? skill.Advancement : default;
        var spent = before.Experience + experience;
        var ranks = before.Ranks;
        while (ranks + 1 < costs.Length && costs[ranks + 1] <= spent) ranks++;
        var after = before with { Ranks = ranks, Experience = spent };
        if (rows != null) rows[id] = after;
        else _skills[id] = _skills[id] with { Advancement = after };
        _unconfirmed[(kind, id)] = Unconfirmed(kind, id) + ranks - before.Ranks;
        UnassignedExperience -= experience;
        Changed?.Invoke();
    }

    /// <summary>A train this client just sent, applied at once: the skill is trained and the credits are spent.</summary>
    public void Train(uint skill, int credits)
    {
        _skills[skill] = _skills.TryGetValue(skill, out var known) ? known with { Training = SkillTraining.Trained } : new SkillAdvancement(default, SkillTraining.Trained);
        SkillCredits -= credits;
        Changed?.Invoke();
    }

    /// <summary>A display title this client just set, applied at once.</summary>
    public void SetDisplayTitle(uint title)
    {
        DisplayTitle = title;
        Changed?.Invoke();
    }

    /// <summary>Logoff: the next character's description starts afresh.</summary>
    public void Clear()
    {
        Known = false;
        _attributes.Clear();
        _vitals.Clear();
        _skills.Clear();
        _titles.Clear();
        _unconfirmed.Clear();
        Level = SkillCredits = PlayerKillerStatus = 0;
        TotalExperience = UnassignedExperience = 0;
        DisplayTitle = 0;
        Changed?.Invoke();
    }

    /// <summary>Reads a server message, if it is one the sheet follows. A message cut short is ignored.</summary>
    public void Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        if (messageType is not (GameEventMessage or UpdateInt or UpdateInt64 or UpdateSkill or UpdateAttribute or UpdateVital)) return;
        // Decal's raw bytes may or may not start with the message type; no first field of these messages equals it.
        if (raw.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == (uint)messageType) raw = raw.Slice(4);
        try
        {
            if (Read(messageType, new Reader(raw))) Changed?.Invoke();
        }
        catch (ArgumentOutOfRangeException) { }
    }

    private bool Read(int messageType, Reader reader)
    {
        switch (messageType)
        {
            case GameEventMessage:
                reader.Skip(8); // the character and the event sequence
                switch (reader.UInt32())
                {
                    case PlayerDescriptionEvent: ReadDescription(reader); return true;
                    case CharacterTitleEvent: ReadTitles(reader); return true;
                    case UpdateTitleEvent: ReadTitleUpdate(reader); return true;
                    default: return false;
                }
            case UpdateInt:
                reader.Skip(1); // the sequence
                return SetInt(reader.UInt32(), reader.Int32());
            case UpdateInt64:
                reader.Skip(1);
                return SetInt64(reader.UInt32(), reader.Int64());
            case UpdateAttribute:
            {
                reader.Skip(1);
                var id = reader.UInt32();
                _attributes[id] = new Advancement(reader.UInt32(), reader.UInt32(), reader.UInt32());
                _unconfirmed.Remove((StatKind.Attribute, id));
                return true;
            }
            case UpdateVital:
            {
                reader.Skip(1);
                var id = reader.UInt32();
                _vitals[id] = new Advancement(reader.UInt32(), reader.UInt32(), reader.UInt32());
                _unconfirmed.Remove((StatKind.Vital, id));
                return true;
            }
            case UpdateSkill:
                reader.Skip(1);
                ReadSkill(ref reader);
                return true;
        }
        return false;
    }

    private bool SetInt(uint property, int value)
    {
        switch (property)
        {
            case SkillCreditsProperty: SkillCredits = value; return true;
            case LevelProperty: Level = value; return true;
            case PlayerKillerProperty: PlayerKillerStatus = value; return true;
            default: return false;
        }
    }

    private bool SetInt64(uint property, long value)
    {
        switch (property)
        {
            case TotalExperienceProperty: TotalExperience = value; return true;
            case UnassignedExperienceProperty: UnassignedExperience = value; return true;
            default: return false;
        }
    }

    // The description's property tables, in the order ACE writes them, and its vector flags.
    private const uint IntTable = 0x01, BoolTable = 0x02, DoubleTable = 0x04, DataIdTable = 0x08, StringTable = 0x10, PositionTable = 0x20,
        InstanceIdTable = 0x40, Int64Table = 0x80;
    private const uint AttributeVector = 0x01, SkillVector = 0x02;

    private void ReadDescription(Reader reader)
    {
        _attributes.Clear();
        _vitals.Clear();
        _skills.Clear();
        _unconfirmed.Clear();
        var tables = reader.UInt32();
        reader.Skip(4); // the weenie type
        if ((tables & IntTable) != 0)
            for (var count = Header(ref reader); count > 0; count--) SetInt(reader.UInt32(), reader.Int32());
        if ((tables & Int64Table) != 0)
            for (var count = Header(ref reader); count > 0; count--) SetInt64(reader.UInt32(), reader.Int64());
        if ((tables & BoolTable) != 0) reader.Skip(Header(ref reader) * 8);
        if ((tables & DoubleTable) != 0) reader.Skip(Header(ref reader) * 12);
        if ((tables & StringTable) != 0)
            for (var count = Header(ref reader); count > 0; count--)
            {
                reader.Skip(4);
                // A 16-bit length, the bytes, then padding to a multiple of four with the length.
                var length = reader.UInt16();
                reader.Skip(length + (4 - (2 + length) % 4) % 4);
            }
        if ((tables & DataIdTable) != 0) reader.Skip(Header(ref reader) * 8);
        if ((tables & InstanceIdTable) != 0) reader.Skip(Header(ref reader) * 8);
        if ((tables & PositionTable) != 0) reader.Skip(Header(ref reader) * (4 + 32));
        var vectors = reader.UInt32();
        reader.Skip(4); // whether it has health
        if ((vectors & AttributeVector) != 0)
        {
            var present = reader.UInt32();
            // ACE's AttributeCache bits, in the order written: Strength, Endurance, Quickness, Coordination, Focus, Self, then the vitals.
            uint[] attributes = { 1, 2, 3, 4, 5, 6 };
            for (var i = 0; i < attributes.Length; i++)
                if ((present & (1u << i)) != 0) _attributes[attributes[i]] = new Advancement(reader.UInt32(), reader.UInt32(), reader.UInt32());
            uint[] vitals = { 1, 3, 5 };
            for (var i = 0; i < vitals.Length; i++)
                if ((present & (0x40u << i)) != 0)
                {
                    _vitals[vitals[i]] = new Advancement(reader.UInt32(), reader.UInt32(), reader.UInt32());
                    reader.Skip(4); // the current value
                }
        }
        if ((vectors & SkillVector) != 0)
            for (var count = Header(ref reader); count > 0; count--) ReadSkill(ref reader);
        Known = true;
    }

    /// <summary>A skill as the description and PrivateUpdateSkill write it, from its id.</summary>
    private void ReadSkill(ref Reader reader)
    {
        var id = reader.UInt32();
        var ranks = reader.UInt16();
        reader.Skip(2); // ACE's adjust-PP flag
        var training = (SkillTraining)reader.UInt32();
        var experience = reader.UInt32();
        var start = reader.UInt32();
        reader.Skip(4 + 8); // resistance at last check, last used
        _skills[id] = new SkillAdvancement(new Advancement(ranks, start, experience), training);
        _unconfirmed.Remove((StatKind.Skill, id));
    }

    private void ReadTitles(Reader reader)
    {
        reader.Skip(4);
        DisplayTitle = reader.UInt32();
        _titles.Clear();
        for (var count = reader.UInt32(); count > 0; count--) _titles.Add(reader.UInt32());
    }

    private void ReadTitleUpdate(Reader reader)
    {
        var title = reader.UInt32();
        if (!_titles.Contains(title)) _titles.Add(title);
        if (reader.UInt32() != 0) DisplayTitle = title;
    }

    /// <summary>A packed hash table's header: its count, then a bucket size the reader does not need.</summary>
    private static int Header(ref Reader reader)
    {
        var count = reader.UInt16();
        reader.Skip(2);
        return count;
    }

    /// <summary>A little-endian reader over a span that throws ArgumentOutOfRangeException past its end.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _at;

        public Reader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _at = 0;
        }

        public void Skip(int count)
        {
            if (count < 0 || _at + count > _bytes.Length) throw new ArgumentOutOfRangeException(nameof(count));
            _at += count;
        }

        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_at + count > _bytes.Length) throw new ArgumentOutOfRangeException(nameof(count));
            var taken = _bytes.Slice(_at, count);
            _at += count;
            return taken;
        }
    }
}
