using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>A member of the player's allegiance as the server describes them: the experience they have passed up, and whether they are online.</summary>
public readonly record struct AllegianceMember(uint Id, string Name, uint Level, uint Rank, uint Produced, bool Online);

/// <summary>A fellowship member with their level and vitals, current and maximum.</summary>
public readonly record struct Fellow(uint Id, string Name, uint Level, uint Health, uint MaxHealth, uint Stamina, uint MaxStamina, uint Mana, uint MaxMana);

public readonly record struct Friend(uint Id, string Name, bool Online);

/// <summary>A squelched character, or a squelched account, which the server lists by one of its characters.</summary>
public readonly record struct Squelch(uint Id, string Name, bool Account);

/// <summary>
/// The player's allegiance, fellowship, friends and squelches as the server reports them, for the social window. Read from the game events
/// ACE sends at login and after each change: AllegianceUpdate and AllegianceLoginNotification, the fellowship's full update, fellow update,
/// quit, dismiss and disband, FriendsListUpdate and SetSquelchDB.
/// </summary>
public sealed class SocialState
{
    private const int GameEventMessage = 0xF7B0;
    // ACE's GameEventType.
    internal const uint AllegianceUpdateEvent = 0x0020, FriendsEvent = 0x0021, FellowQuitEvent = 0x00A3, FellowDismissEvent = 0x00A4,
        SquelchEvent = 0x01F4, AllegianceLoginEvent = 0x027A, FellowshipEvent = 0x02BE, DisbandEvent = 0x02BF, FellowEvent = 0x02C0;
    // ACE's FriendsUpdateTypeFlag (0 is the full list) and AllegianceIndex bits.
    private const uint FriendRemoved = 2;
    private const uint LoggedIn = 0x1, HasAllegianceAge = 0x4, HasPackedLevel = 0x8;

    private readonly List<AllegianceMember> _vassals = new();
    private readonly List<Fellow> _fellows = new();
    private readonly List<Friend> _friends = new();
    private readonly List<Squelch> _squelches = new();

    /// <summary>The player's own object id, from the game events' header.</summary>
    public uint Player { get; private set; }

    public uint Rank { get; private set; }
    public uint Followers { get; private set; }
    public AllegianceMember? Monarch { get; private set; }
    /// <summary>Null when the player is the monarch or in no allegiance; the monarch when sworn straight to them.</summary>
    public AllegianceMember? Patron { get; private set; }
    public IReadOnlyList<AllegianceMember> Vassals => _vassals;

    /// <summary>The fellowship's name, or null outside a fellowship.</summary>
    public string? Fellowship { get; private set; }
    public uint Leader { get; private set; }
    public bool Open { get; private set; }
    public bool ShareExperience { get; private set; }
    public bool EvenShare { get; private set; }
    /// <summary>In the order the server sent them, the leader not moved first.</summary>
    public IReadOnlyList<Fellow> Fellows => _fellows;

    public IReadOnlyList<Friend> Friends => _friends;
    public IReadOnlyList<Squelch> Squelches => _squelches;

    public event Action? Changed;

    /// <summary>Logoff: the next character starts afresh.</summary>
    public void Clear()
    {
        Player = Rank = Followers = Leader = 0;
        Monarch = Patron = null;
        _vassals.Clear();
        LeaveFellowship();
        _friends.Clear();
        _squelches.Clear();
        Changed?.Invoke();
    }

    /// <summary>Reads a server message, if it is one the state follows. A message cut short is ignored.</summary>
    public void Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        if (messageType != GameEventMessage) return;
        // The raw bytes may start with the message type (the hook's always do), as for the character sheet.
        if (raw.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == (uint)messageType) raw = raw.Slice(4);
        try
        {
            var reader = new MessageReader(raw);
            var player = reader.UInt32();
            reader.Skip(4); // the event sequence
            if (Read(reader.UInt32(), ref reader, player)) Changed?.Invoke();
        }
        catch (ArgumentOutOfRangeException) { }
    }

    private bool Read(uint type, ref MessageReader reader, uint player)
    {
        switch (type)
        {
            case AllegianceUpdateEvent: Player = player; ReadAllegiance(ref reader); return true;
            case AllegianceLoginEvent: return SetOnline(reader.UInt32(), reader.UInt32() != 0);
            case FellowshipEvent: Player = player; ReadFellowship(ref reader); return true;
            case FellowEvent:
                if (Fellowship == null) return false;
                Upsert(ReadFellow(ref reader));
                return true;
            case FellowQuitEvent:
            case FellowDismissEvent:
            {
                var gone = reader.UInt32();
                if (gone == player) LeaveFellowship();
                else _fellows.RemoveAll(fellow => fellow.Id == gone);
                return true;
            }
            case DisbandEvent: LeaveFellowship(); return true;
            case FriendsEvent: ReadFriends(ref reader); return true;
            case SquelchEvent: ReadSquelches(ref reader); return true;
            default: return false;
        }
    }

    /// <summary>
    /// The rank, member counts and hierarchy: the monarch, then a record per member with its parent. The player's own record names the
    /// patron; the records whose parent is the player are the vassals.
    /// </summary>
    private void ReadAllegiance(ref MessageReader reader)
    {
        Rank = reader.UInt32();
        reader.Skip(4); // the allegiance's member count
        Followers = reader.UInt32();
        var records = reader.UInt16();
        reader.Skip(2); // the hierarchy version
        reader.Skip(reader.Header() * 8); // officers
        for (var titles = reader.UInt32(); titles > 0; titles--) reader.String();
        reader.Skip(16); // broadcast times and counts
        reader.String(); // message of the day
        reader.String(); // who set it
        reader.Skip(4 + 32); // chat room, bind point
        reader.String(); // allegiance name
        reader.Skip(12); // name set time, locked, approved vassal
        Monarch = Patron = null;
        _vassals.Clear();
        if (records == 0) return;
        var monarch = ReadMember(ref reader);
        Monarch = monarch;
        var members = new List<(uint Parent, AllegianceMember Member)>();
        for (var i = 1; i < records; i++) members.Add((reader.UInt32(), ReadMember(ref reader)));
        var self = members.FirstOrDefault(entry => entry.Member.Id == Player);
        if (self.Member.Id != 0)
            Patron = self.Parent == monarch.Id ? monarch : members.FirstOrDefault(entry => entry.Member.Id == self.Parent).Member;
        _vassals.AddRange(members.Where(entry => entry.Parent == Player).Select(entry => entry.Member));
    }

    private static AllegianceMember ReadMember(ref MessageReader reader)
    {
        var id = reader.UInt32();
        reader.Skip(4); // experience gained while logged off
        var produced = reader.UInt32();
        var bits = reader.UInt32();
        reader.Skip(2); // gender, heritage
        uint rank = reader.UInt16();
        var level = (bits & HasPackedLevel) != 0 ? reader.UInt32() : 0;
        reader.Skip(4); // loyalty, leadership
        reader.Skip(8); // time online and allegiance age, or one 64-bit time online
        return new AllegianceMember(id, reader.String(), level, rank, produced, (bits & LoggedIn) != 0);
    }

    private bool SetOnline(uint id, bool online)
    {
        var changed = false;
        if (Monarch?.Id == id) { Monarch = Monarch.Value with { Online = online }; changed = true; }
        if (Patron?.Id == id) { Patron = Patron.Value with { Online = online }; changed = true; }
        for (var i = 0; i < _vassals.Count; i++)
            if (_vassals[i].Id == id)
            {
                _vassals[i] = _vassals[i] with { Online = online };
                changed = true;
            }
        return changed;
    }

    private void ReadFellowship(ref MessageReader reader)
    {
        _fellows.Clear();
        for (var count = reader.Header(); count > 0; count--) _fellows.Add(ReadFellow(ref reader, update: false));
        Fellowship = reader.String();
        Leader = reader.UInt32();
        ShareExperience = reader.UInt32() != 0;
        EvenShare = reader.UInt32() != 0;
        Open = reader.UInt32() != 0;
        // The lock and departed members follow; the window shows neither.
    }

    /// <summary>A fellow as the full update and the fellow update write them; the update adds its kind after the name.</summary>
    private static Fellow ReadFellow(ref MessageReader reader, bool update = true)
    {
        var id = reader.UInt32();
        reader.Skip(8); // cached experience and luminance
        var level = reader.UInt32();
        uint maxHealth = reader.UInt32(), maxStamina = reader.UInt32(), maxMana = reader.UInt32();
        uint health = reader.UInt32(), stamina = reader.UInt32(), mana = reader.UInt32();
        reader.Skip(4); // loot sharing
        var fellow = new Fellow(id, reader.String(), level, health, maxHealth, stamina, maxStamina, mana, maxMana);
        if (update) reader.Skip(4);
        return fellow;
    }

    private void Upsert(Fellow fellow)
    {
        var at = _fellows.FindIndex(known => known.Id == fellow.Id);
        if (at >= 0) _fellows[at] = fellow;
        else _fellows.Add(fellow);
    }

    private void LeaveFellowship()
    {
        Fellowship = null;
        Leader = 0;
        Open = ShareExperience = EvenShare = false;
        _fellows.Clear();
    }

    private void ReadFriends(ref MessageReader reader)
    {
        var read = new List<Friend>();
        for (var count = reader.UInt32(); count > 0; count--)
        {
            var id = reader.UInt32();
            var online = reader.UInt32() != 0;
            reader.Skip(4); // appear offline
            var name = reader.String();
            reader.Skip((int)reader.UInt32() * 4); // their friends
            reader.Skip((int)reader.UInt32() * 4); // who has them as a friend
            read.Add(new Friend(id, name, online));
        }
        var kind = reader.UInt32();
        if (kind == 0) _friends.Clear();
        foreach (var friend in read)
        {
            _friends.RemoveAll(known => known.Id == friend.Id);
            if (kind != FriendRemoved) _friends.Add(friend);
        }
    }

    /// <summary>The whole squelch list: an account table retail always leaves empty, the characters by id, then the global squelch.</summary>
    private void ReadSquelches(ref MessageReader reader)
    {
        for (var count = reader.Header(); count > 0; count--)
        {
            reader.String();
            reader.Skip(4);
        }
        _squelches.Clear();
        for (var count = reader.Header(); count > 0; count--)
        {
            var id = reader.UInt32();
            reader.Skip((int)reader.UInt32() * 4); // the channels
            var name = reader.String();
            _squelches.Add(new Squelch(id, name, reader.UInt32() != 0));
        }
    }
}
