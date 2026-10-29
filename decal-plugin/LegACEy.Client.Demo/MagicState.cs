using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>
/// The player's spellbook as the server reports it, for the magic window: the spells, the spellbook's filters, the components to keep
/// (by weenie class) and the Infused augmentations, from the login's PlayerDescription (ACE's GameEventPlayerDescription), then each spell
/// learnt or deleted (MagicUpdateSpell 0x02C1, MagicRemoveSpell 0x01A8) and each augmentation gained (PrivateUpdatePropertyInt). Filters
/// and component levels the player changes are set here as they are sent, since the server does not echo them.
/// </summary>
public sealed class MagicState
{
    private const int GameEventMessage = 0xF7B0, UpdateInt = 0x02CD;
    internal const uint PlayerDescriptionEvent = 0x0013, UpdateSpellEvent = 0x02C1, RemoveSpellEvent = 0x01A8;

    /// <summary>ACE's SpellBookFilterOptions: the schools, then levels I to VIII (and an unused IX), then Void.</summary>
    public const uint CreatureFilter = 0x1, ItemFilter = 0x2, LifeFilter = 0x4, WarFilter = 0x8, VoidFilter = 0x2000;
    public static uint LevelFilter(uint level) => 0x10u << (int)(level - 1);

    // ACE's PropertyInt AugmentationInfused*Magic, by ACE's MagicSchool (1 War, 2 Life, 3 Item, 4 Creature, 5 Void).
    private static readonly Dictionary<uint, uint> InfusedProperties = new() { [297] = 1, [296] = 2, [295] = 3, [294] = 4, [328] = 5 };

    private readonly HashSet<uint> _spells = new();
    private readonly Dictionary<uint, int> _desired = new();
    private readonly HashSet<uint> _infused = new();

    public IReadOnlyCollection<uint> Spells => _spells;

    /// <summary>The filters the spellbook shows: a set bit shows that school or level. The server's value, or the one last sent.</summary>
    public uint Filters { get; private set; }

    /// <summary>How many of each component (by weenie class) the player asked to keep.</summary>
    public IReadOnlyDictionary<uint, int> Desired => _desired;

    /// <summary>The schools (ACE's MagicSchool) whose Infused augmentation lets the player cast with scarabs and prismatic tapers only.</summary>
    public IReadOnlyCollection<uint> Infused => _infused;

    public event Action? Changed;

    public void Clear()
    {
        _spells.Clear();
        _desired.Clear();
        _infused.Clear();
        Filters = 0;
        Changed?.Invoke();
    }

    public void SetFilters(uint filters)
    {
        Filters = filters;
        Changed?.Invoke();
    }

    public void SetDesired(uint wcid, int amount)
    {
        if (amount > 0) _desired[wcid] = amount;
        else _desired.Remove(wcid);
        Changed?.Invoke();
    }

    /// <summary>Reads a server message, if it is one the state follows. A message cut short is ignored.</summary>
    public void Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        if (messageType is not (GameEventMessage or UpdateInt)) return;
        if (raw.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == (uint)messageType) raw = raw.Slice(4);
        try
        {
            if (Read(messageType, new MessageReader(raw))) Changed?.Invoke();
        }
        catch (ArgumentOutOfRangeException) { }
    }

    private bool Read(int messageType, MessageReader reader)
    {
        if (messageType == UpdateInt)
        {
            reader.Skip(1); // the sequence
            return SetInt(reader.UInt32(), reader.Int32());
        }
        reader.Skip(8); // the player and the event sequence
        switch (reader.UInt32())
        {
            case PlayerDescriptionEvent:
                ReadDescription(reader);
                return true;
            case UpdateSpellEvent:
                return _spells.Add(reader.UInt16());
            case RemoveSpellEvent:
                return _spells.Remove(reader.UInt16());
            default:
                return false;
        }
    }

    private bool SetInt(uint property, int value)
    {
        if (!InfusedProperties.TryGetValue(property, out var school)) return false;
        if (value > 0) _infused.Add(school);
        else _infused.Remove(school);
        return true;
    }

    // The description's property tables and vectors (as CharacterSheet reads them), its enchantment lists and its option flags.
    private const uint IntTable = 0x01, BoolTable = 0x02, DoubleTable = 0x04, DataIdTable = 0x08, StringTable = 0x10, PositionTable = 0x20,
        InstanceIdTable = 0x40, Int64Table = 0x80;
    private const uint AttributeVector = 0x01, SkillVector = 0x02, SpellVector = 0x100, EnchantmentVector = 0x200;
    private const uint MultiplicativeEnchantments = 0x1, AdditiveEnchantments = 0x2, VitaeEnchantment = 0x4, CooldownEnchantments = 0x8;
    private const uint ShortcutOption = 0x1, DesiredCompsOption = 0x8, SpellbookFiltersOption = 0x20, SpellLists8Option = 0x400;

    /// <summary>Reads the whole description first, so one cut short changes nothing.</summary>
    private void ReadDescription(MessageReader reader)
    {
        var spells = new HashSet<uint>();
        var desired = new Dictionary<uint, int>();
        var infused = new HashSet<uint>();
        var filters = Filters;
        var tables = reader.UInt32();
        reader.Skip(4); // the weenie type
        if ((tables & IntTable) != 0)
            for (var count = reader.Header(); count > 0; count--)
            {
                var property = reader.UInt32();
                if (reader.Int32() > 0 && InfusedProperties.TryGetValue(property, out var school)) infused.Add(school);
            }
        if ((tables & Int64Table) != 0) reader.Skip(reader.Header() * 12);
        if ((tables & BoolTable) != 0) reader.Skip(reader.Header() * 8);
        if ((tables & DoubleTable) != 0) reader.Skip(reader.Header() * 12);
        if ((tables & StringTable) != 0)
            for (var count = reader.Header(); count > 0; count--)
            {
                reader.Skip(4);
                var length = reader.UInt16();
                reader.Skip(length + (4 - (2 + length) % 4) % 4);
            }
        if ((tables & DataIdTable) != 0) reader.Skip(reader.Header() * 8);
        if ((tables & InstanceIdTable) != 0) reader.Skip(reader.Header() * 8);
        if ((tables & PositionTable) != 0) reader.Skip(reader.Header() * (4 + 32));
        var vectors = reader.UInt32();
        reader.Skip(4); // whether it has health
        if ((vectors & AttributeVector) != 0)
        {
            var present = reader.UInt32();
            for (var bit = 0; bit < 6; bit++) if ((present & (1u << bit)) != 0) reader.Skip(12);
            for (var bit = 6; bit < 9; bit++) if ((present & (1u << bit)) != 0) reader.Skip(16);
        }
        if ((vectors & SkillVector) != 0) reader.Skip(reader.Header() * 32);
        if ((vectors & SpellVector) != 0)
            for (var count = reader.Header(); count > 0; count--)
            {
                spells.Add(reader.UInt32());
                reader.Skip(4); // ACE's 2f: the new spell configuration
            }
        if ((vectors & EnchantmentVector) != 0)
        {
            var lists = reader.UInt32();
            if ((lists & MultiplicativeEnchantments) != 0) SkipEnchantments(ref reader, (int)reader.UInt32());
            if ((lists & AdditiveEnchantments) != 0) SkipEnchantments(ref reader, (int)reader.UInt32());
            if ((lists & CooldownEnchantments) != 0) SkipEnchantments(ref reader, (int)reader.UInt32());
            if ((lists & VitaeEnchantment) != 0) SkipEnchantments(ref reader, 1);
        }
        var options = reader.UInt32();
        reader.Skip(4); // character options 1
        if ((options & ShortcutOption) != 0) reader.Skip((int)reader.UInt32() * 12);
        var spellLists = (options & SpellLists8Option) != 0 ? 8 : 1;
        for (var i = 0; i < spellLists; i++) reader.Skip((int)reader.UInt32() * 4);
        if ((options & DesiredCompsOption) != 0)
            for (var count = reader.Header(); count > 0; count--)
            {
                var wcid = reader.UInt32();
                var amount = reader.Int32();
                if (amount > 0) desired[wcid] = amount;
            }
        if ((options & SpellbookFiltersOption) != 0) filters = reader.UInt32();
        Replace(_spells, spells);
        Replace(_infused, infused);
        _desired.Clear();
        foreach (var pair in desired) _desired[pair.Key] = pair.Value;
        Filters = filters;
    }

    private static void Replace(HashSet<uint> target, HashSet<uint> source)
    {
        target.Clear();
        target.UnionWith(source);
    }

    /// <summary>
    /// Enchantments as ACE writes them: spell, layer, category and has-set (four u16), power, start and duration (f64), caster, degrade
    /// modifier and limit, last degraded (f64), stat mod type, key and value, then the set id when it has one.
    /// </summary>
    private static void SkipEnchantments(ref MessageReader reader, int count)
    {
        for (var i = 0; i < count; i++)
        {
            reader.Skip(6);
            var hasSet = reader.UInt16() != 0;
            reader.Skip(4 + 8 + 8 + 4 + 4 + 4 + 8 + 4 + 4 + 4 + (hasSet ? 4 : 0));
        }
    }
}
