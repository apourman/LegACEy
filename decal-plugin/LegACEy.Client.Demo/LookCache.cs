using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// How each object looks, read from the game messages every server sends the retail client: CreateObject and UpdateObject carry an
/// object's setup, ObjDesc, scale and holder; ObjDescEvent changes its ObjDesc; ParentEvent and PickupEvent wield and unwield it. So the
/// paperdoll needs no LegACEy server for anything the client has been sent. Not thread-safe: fed and read on the render thread.
/// </summary>
public sealed class LookCache
{
    public const int ObjDescEvent = 0xF625, CreateObject = 0xF745, DeleteObject = 0xF747, ParentEvent = 0xF749, PickupEvent = 0xF74A,
        UpdateObject = 0xF7DB;

    // PhysicsDesc flags, in the order their fields follow (ACE's PhysicsDescriptionFlag).
    private const uint CSetup = 0x1, MTable = 0x2, Parent = 0x20, Children = 0x40, ObjScale = 0x80, STable = 0x800, PeTable = 0x1000,
        Position = 0x8000, Movement = 0x10000, AnimationFrame = 0x20000;

    private sealed record Entry(uint SetupId, float Scale, CharacterAppearance ObjDesc);

    private readonly Dictionary<uint, Entry> _looks = new();
    // A held item's holder and holding location (ACE's ParentLocation).
    private readonly Dictionary<uint, (uint Holder, uint Location)> _held = new();

    /// <summary>Raised with an object's id when its look, or what it holds, changes.</summary>
    public event Action<uint>? Changed;

    /// <summary>Whether a message type is one this reads, so the host passes only these along.</summary>
    public static bool Reads(int messageType) => messageType is ObjDescEvent or CreateObject or DeleteObject or ParentEvent or PickupEvent or UpdateObject;

    /// <summary>
    /// The object's look with what it holds, as paperdoll.look sends it, or null when the client has not been sent it.
    /// </summary>
    public CharacterAppearance? Look(uint objectId)
    {
        if (!_looks.TryGetValue(objectId, out var entry) || entry.SetupId == 0) return null;
        var held = new List<HeldItem>();
        foreach (var pair in _held)
            if (pair.Value.Holder == objectId && pair.Value.Location != 0 && _looks.TryGetValue(pair.Key, out var look) && look.SetupId != 0)
                held.Add(new HeldItem(pair.Value.Location, look.Scale, WithSetup(look)));
        var own = WithSetup(entry);
        return held.Count == 0 ? own : new CharacterAppearance(own.SetupId, own.PaletteId, own.SubPalettes, own.TextureChanges, own.PartChanges, held);
    }

    public void Clear()
    {
        _looks.Clear();
        _held.Clear();
    }

    /// <summary>
    /// Forgets every object <paramref name="keep"/> refuses, and what it held; an item held by a kept object stays with it. ACE sends no
    /// DeleteObject for objects left behind on a teleport or out of sight; the client ages them out itself, so the host asks it which it
    /// still has. Returns how many it forgot.
    /// </summary>
    public int Prune(Func<uint, bool> keep)
    {
        var forgotten = 0;
        foreach (var id in new List<uint>(_looks.Keys))
        {
            if (keep(id) || (_held.TryGetValue(id, out var held) && keep(held.Holder))) continue;
            _looks.Remove(id);
            _held.Remove(id);
            DropHeldBy(id);
            forgotten++;
        }
        return forgotten;
    }

    /// <summary>Reads one server message, as Decal's ServerDispatch raises it. Anything malformed is left out.</summary>
    public void Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        if (!Reads(messageType)) return;
        // Decal's raw bytes may or may not start with the message type; an object id never equals it.
        if (raw.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == (uint)messageType) raw = raw.Slice(4);
        try { Read(messageType, raw); }
        catch (ArgumentOutOfRangeException) { }
    }

    private void Read(int messageType, ReadOnlySpan<byte> raw)
    {
        var reader = new Reader(raw);
        var id = reader.UInt32();
        switch (messageType)
        {
            case CreateObject or UpdateObject:
            {
                var objDesc = ReadObjDesc(ref reader);
                var flags = reader.UInt32();
                reader.Skip(4); // physics state
                if ((flags & Movement) != 0)
                {
                    var length = (int)reader.UInt32();
                    reader.Skip(length > 0 ? length + 4 : 0); // the movement, then whether it is autonomous
                }
                else if ((flags & AnimationFrame) != 0) reader.Skip(4);
                if ((flags & Position) != 0) reader.Skip(32);
                if ((flags & MTable) != 0) reader.Skip(4);
                if ((flags & STable) != 0) reader.Skip(4);
                if ((flags & PeTable) != 0) reader.Skip(4);
                var setup = (flags & CSetup) != 0 ? reader.UInt32() : 0;
                (uint Holder, uint Location)? parent = (flags & Parent) != 0 ? (reader.UInt32(), reader.UInt32()) : null;
                List<(uint Item, uint Location)>? children = null;
                if ((flags & Children) != 0)
                {
                    children = new List<(uint, uint)>();
                    for (int i = 0, count = (int)reader.UInt32(); i < count; i++) children.Add((reader.UInt32(), reader.UInt32()));
                }
                var scale = (flags & ObjScale) != 0 ? reader.Single() : 1f;
                // Read whole before anything changes, so a cut-off message changes nothing.
                var oldHolder = HolderOf(id);
                _held.Remove(id);
                if (parent is { } held) _held[id] = held;
                if (children != null)
                {
                    DropHeldBy(id);
                    foreach (var (item, location) in children) _held[item] = (id, location);
                }
                _looks[id] = new Entry(setup, scale, objDesc);
                Raise(id, oldHolder);
                break;
            }
            case ObjDescEvent:
                if (!_looks.TryGetValue(id, out var entry)) return;
                _looks[id] = entry with { ObjDesc = ReadObjDesc(ref reader) };
                Raise(id, 0);
                break;
            case ParentEvent:
            {
                var item = reader.UInt32();
                var oldHolder = HolderOf(item);
                _held[item] = (id, reader.UInt32());
                Raise(id, oldHolder);
                break;
            }
            case PickupEvent or DeleteObject:
            {
                var holder = HolderOf(id);
                _held.Remove(id);
                if (messageType == DeleteObject)
                {
                    _looks.Remove(id);
                    DropHeldBy(id);
                }
                if (holder != 0) Changed?.Invoke(holder);
                break;
            }
        }
    }

    private void DropHeldBy(uint holder)
    {
        foreach (var pair in new List<KeyValuePair<uint, (uint Holder, uint Location)>>(_held))
            if (pair.Value.Holder == holder) _held.Remove(pair.Key);
    }

    private uint HolderOf(uint item) => _held.TryGetValue(item, out var held) ? held.Holder : 0;

    // The object, its holder and its old holder, each once.
    private void Raise(uint id, uint oldHolder)
    {
        var holder = HolderOf(id);
        Changed?.Invoke(id);
        if (holder != 0 && holder != id) Changed?.Invoke(holder);
        if (oldHolder != 0 && oldHolder != id && oldHolder != holder) Changed?.Invoke(oldHolder);
    }

    private static CharacterAppearance WithSetup(Entry entry) =>
        new(entry.SetupId, entry.ObjDesc.PaletteId, entry.ObjDesc.SubPalettes, entry.ObjDesc.TextureChanges, entry.ObjDesc.PartChanges);

    /// <summary>The ObjDesc as ModelData sends it: counts as bytes, ids as packed dwords of known type, then aligned to 4.</summary>
    private static CharacterAppearance ReadObjDesc(ref Reader reader)
    {
        reader.Skip(1); // 0x11
        int palettes = reader.Byte(), textures = reader.Byte(), parts = reader.Byte();
        var palette = palettes > 0 ? reader.Packed(0x04000000) : 0;
        var subPalettes = new List<SubPalette>(palettes);
        for (var i = 0; i < palettes; i++) subPalettes.Add(new SubPalette(reader.Packed(0x04000000), reader.Byte(), reader.Byte()));
        var textureChanges = new List<TextureChange>(textures);
        for (var i = 0; i < textures; i++) textureChanges.Add(new TextureChange(reader.Byte(), reader.Packed(0x05000000), reader.Packed(0x05000000)));
        var partChanges = new List<PartChange>(parts);
        for (var i = 0; i < parts; i++) partChanges.Add(new PartChange(reader.Byte(), reader.Packed(0x01000000)));
        reader.Align();
        return new CharacterAppearance(0, palette, subPalettes, textureChanges, partChanges);
    }

    // Offsets count from just after the message type, which keeps the wire's 4-byte alignment.
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _at;

        public Reader(ReadOnlySpan<byte> bytes) { _bytes = bytes; _at = 0; }

        public byte Byte() => _bytes[_at++];
        public ushort UInt16() { var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.Slice(_at, 2)); _at += 2; return value; }
        public uint UInt32() { var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.Slice(_at, 4)); _at += 4; return value; }
        public float Single() => BitConverter.ToSingle(BitConverter.GetBytes(UInt32()), 0);
        public void Skip(int count) { _ = _bytes.Slice(_at, count); _at += count; }
        public void Align() => Skip((4 - _at % 4) % 4);

        /// <summary>A packed dword (two bytes, or four when the first has its top bit), with its file type put back when it was cut off.</summary>
        public uint Packed(uint type)
        {
            uint value = UInt16();
            if ((value & 0x8000) != 0) value = ((value & 0x7FFF) << 16) | UInt16();
            return value != 0 && (value & 0xFF000000) == 0 ? value | type : value;
        }
    }
}
