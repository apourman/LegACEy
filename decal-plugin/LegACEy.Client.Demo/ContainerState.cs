using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// The opened chest, corpse or ground container and what it holds, as the server tells the client. Opening one sends ViewContents (a game
/// event: the container, then each item it holds and whether that item is a pack) for it and for each pack in it, and a CreateObject for each
/// item (ACE's Container.SendInventory); closing it sends CloseGroundContainer. Items then move with InventoryPutObjInContainer and leave
/// with the other inventory messages. Retail's own container panel shows the same messages. The state keeps only objects a container holds, and
/// the containers' own names and sizes, and forgets a container's items once it closes. Fed and read on the game thread.
/// </summary>
public sealed class ContainerState
{
    private const int GameEventMessage = 0xF7B0, CreateObject = 0xF745, UpdateObject = 0xF7DB, DeleteObject = 0xF747, RemoveObject = 0x0024,
        UpdatePublicInt = 0x02CE;
    private const uint ViewContentsEvent = 0x0196, CloseGroundContainerEvent = 0x0052, PutInContainerEvent = 0x0022, WieldEvent = 0x0023,
        PutIn3DEvent = 0x019A;
    // ACE's ContainerType: a pack in the container (and a focus, which takes a pack slot) is not an item cell.
    private const uint NonContainer = 0;
    private const uint StackSizeProperty = 12;

    // PhysicsDescriptionFlag, in the order their fields follow.
    private const uint CSetup = 0x1, MTable = 0x2, Velocity = 0x4, Acceleration = 0x8, Omega = 0x10, Parent = 0x20, Children = 0x40,
        ObjScale = 0x80, Friction = 0x100, Elasticity = 0x200, STable = 0x800, PeTable = 0x1000, DefaultScript = 0x2000,
        DefaultScriptIntensity = 0x4000, Position = 0x8000, Movement = 0x10000, AnimationFrame = 0x20000, Translucency = 0x40000;

    // WeenieHeaderFlag and the second header's flags.
    private const uint PluralName = 0x1, ItemsCapacity = 0x2, ContainersCapacity = 0x4, Value = 0x8, Usable = 0x10, UseRadius = 0x20,
        Monarch = 0x40, UiEffectsFlag = 0x80, AmmoType = 0x100, CombatUse = 0x200, StructureFlag = 0x400, MaxStructureFlag = 0x800,
        StackSizeFlag = 0x1000, MaxStackSizeFlag = 0x2000, ContainerFlag = 0x4000, Wielder = 0x8000, ValidLocationsFlag = 0x10000,
        CurrentlyWielded = 0x20000, Priority = 0x40000, TargetType = 0x80000, RadarBlipColor = 0x100000, Burden = 0x200000, Spell = 0x400000,
        RadarBehavior = 0x800000, Workmanship = 0x1000000, HouseOwner = 0x2000000, HouseRestrictions = 0x4000000, PScript = 0x8000000,
        HookType = 0x10000000, HookItemTypes = 0x20000000, IconOverlay = 0x40000000;
    private const uint IncludesSecondHeader = 0x04000000, IconUnderlay = 0x1;

    /// <summary>One object the state knows: what a cell shows and where the object sits.</summary>
    private sealed record Known(uint Id, string Name, ItemVisual Visual, int StackCount, int StackMax, int Wcid, uint ValidLocations,
        double? PartFull, uint Container, int ItemSlots, int PackSlots);

    private readonly Dictionary<uint, Known> _known = new();
    // Each viewed container's items and packs, in the server's order: an item's index here is its cell.
    private readonly Dictionary<uint, List<(uint Id, uint Type)>> _views = new();
    private uint _opened;

    /// <summary>Raised when what the window shows may have changed.</summary>
    public event Action? Changed;

    /// <summary>The container the player has open: a chest, a corpse or something on the ground. Zero when none is.</summary>
    public uint Opened => _opened;

    public bool IsOpen => _opened != 0;

    /// <summary>Forgets everything: logoff, or the windows failing.</summary>
    public void Clear()
    {
        _known.Clear();
        _views.Clear();
        _opened = 0;
        Changed?.Invoke();
    }

    /// <summary>The container's name; empty when the client was not sent its object.</summary>
    public string NameOf(uint id) => _known.TryGetValue(id, out var known) ? known.Name : string.Empty;

    /// <summary>The container's icon layers; null when the client was not sent its object.</summary>
    public ItemVisual? VisualOf(uint id) => _known.TryGetValue(id, out var known) ? known.Visual : null;

    /// <summary>How many items the container holds at most; zero when the server did not say.</summary>
    public int CapacityOf(uint id) => _known.TryGetValue(id, out var known) ? known.ItemSlots : 0;

    /// <summary>The items the container holds in the server's order, one per cell. An item the client has not been sent yet is left out.</summary>
    public IReadOnlyList<InventoryItem> ItemsOf(uint id) => Entries(id, packs: false);

    /// <summary>The packs (and foci) the container holds, in the server's order.</summary>
    public IReadOnlyList<InventoryItem> PacksOf(uint id) => Entries(id, packs: true);

    private IReadOnlyList<InventoryItem> Entries(uint id, bool packs)
    {
        if (!_views.TryGetValue(id, out var view)) return Array.Empty<InventoryItem>();
        var list = new List<InventoryItem>();
        var index = 0;
        foreach (var (itemId, type) in view.Where(entry => (entry.Type != NonContainer) == packs))
        {
            if (_known.TryGetValue(itemId, out var known))
                list.Add(new InventoryItem(itemId, known.Name, id, index, known.Visual, known.StackCount, known.StackMax, known.Wcid, known.ValidLocations, known.PartFull));
            index++;
        }
        return list;
    }

    /// <summary>Reads a server message, if it is one the state follows. A message cut short is ignored.</summary>
    public void Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        if (messageType is not (GameEventMessage or CreateObject or UpdateObject or DeleteObject or RemoveObject or UpdatePublicInt)) return;
        if (raw.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == (uint)messageType) raw = raw.Slice(4);
        try
        {
            if (Read(messageType, new MessageReader(raw))) Changed?.Invoke();
        }
        catch (ArgumentOutOfRangeException) { }
    }

    private bool Read(int messageType, MessageReader reader)
    {
        switch (messageType)
        {
            case CreateObject or UpdateObject:
                return ReadObject(ref reader);
            case DeleteObject or RemoveObject:
                return Leave(reader.UInt32(), forget: true);
            case UpdatePublicInt:
            {
                reader.Skip(1); // the sequence
                var id = reader.UInt32();
                if (reader.UInt32() != StackSizeProperty || !_known.TryGetValue(id, out var known)) return false;
                _known[id] = known with { StackCount = Math.Max(1, reader.Int32()) };
                return true;
            }
        }
        reader.Skip(8); // the player and the event sequence
        switch (reader.UInt32())
        {
            case ViewContentsEvent:
                return View(ref reader);
            case CloseGroundContainerEvent:
                return Close(reader.UInt32());
            case PutInContainerEvent:
                return PutIn(ref reader);
            case WieldEvent or PutIn3DEvent:
                return Leave(reader.UInt32(), forget: false);
            default:
                return false;
        }
    }

    // ViewContents: the container, how many it holds, then each one's id and ContainerType.
    private bool View(ref MessageReader reader)
    {
        var container = reader.UInt32();
        var view = new List<(uint, uint)>();
        for (int i = 0, count = (int)reader.UInt32(); i < count; i++) view.Add((reader.UInt32(), reader.UInt32()));
        // A pack of the container being viewed is a view of its own; any other view is a container the player opened.
        if (!_views.Values.Any(other => other.Any(entry => entry.Id == container)))
        {
            if (_opened != container) Forget(_opened);
            _opened = container;
        }
        _views[container] = view;
        return true;
    }

    private bool Close(uint container)
    {
        if (container != _opened) return false;
        Forget(container);
        _opened = 0;
        return true;
    }

    // Drops a closed container's view and the packs' views, and the objects they held, so a long session keeps no corpse's loot.
    private void Forget(uint container)
    {
        if (container == 0 || !_views.TryGetValue(container, out var view)) return;
        foreach (var (id, type) in view)
        {
            if (type != NonContainer && _views.TryGetValue(id, out var pack))
                foreach (var held in pack) _known.Remove(held.Id);
            _views.Remove(id);
            _known.Remove(id);
        }
        _views.Remove(container);
    }

    // InventoryPutObjInContainer: the item, its new container, the place in it and its ContainerType.
    private bool PutIn(ref MessageReader reader)
    {
        var item = reader.UInt32();
        var container = reader.UInt32();
        var place = (int)reader.UInt32();
        var type = reader.UInt32();
        var changed = Leave(item, forget: false);
        if (_known.TryGetValue(item, out var known)) _known[item] = known with { Container = container };
        if (!_views.TryGetValue(container, out var view)) return changed;
        view.Insert(Math.Max(0, Math.Min(place, view.Count)), (item, type));
        return true;
    }

    // The item is no longer in the container it was viewed in: taken, wielded, dropped or gone.
    private bool Leave(uint item, bool forget)
    {
        var changed = false;
        foreach (var view in _views.Values) changed |= view.RemoveAll(entry => entry.Id == item) > 0;
        if (forget) changed |= _known.Remove(item);
        return changed;
    }

    /// <summary>
    /// CreateObject or UpdateObject: the object's model and physics are skipped for its public weenie description. An object that is in a
    /// container, or is one itself (a chest, a corpse, a pack), is kept; the world's other objects are not.
    /// </summary>
    private bool ReadObject(ref MessageReader reader)
    {
        var id = reader.UInt32();
        SkipModel(ref reader);
        SkipPhysics(ref reader);
        var flags = reader.UInt32();
        var name = reader.String();
        var wcid = (int)reader.Packed(0);
        var icon = reader.Packed(0x06000000);
        var itemType = reader.UInt32();
        var description = reader.UInt32();
        reader.Align();
        var second = (description & IncludesSecondHeader) != 0 ? reader.UInt32() : 0;
        if ((flags & PluralName) != 0) reader.String();
        int itemSlots = 0, packSlots = 0, stack = 1, stackMax = 0, structure = 0, maxStructure = 0;
        uint uiEffects = 0, validLocations = 0, container = 0;
        if ((flags & ItemsCapacity) != 0) itemSlots = reader.Byte();
        if ((flags & ContainersCapacity) != 0) packSlots = reader.Byte();
        if ((flags & AmmoType) != 0) reader.Skip(2);
        if ((flags & Value) != 0) reader.Skip(4);
        if ((flags & Usable) != 0) reader.Skip(4);
        if ((flags & UseRadius) != 0) reader.Skip(4);
        if ((flags & TargetType) != 0) reader.Skip(4);
        if ((flags & UiEffectsFlag) != 0) uiEffects = reader.UInt32();
        if ((flags & CombatUse) != 0) reader.Skip(1);
        if ((flags & StructureFlag) != 0) structure = reader.UInt16();
        if ((flags & MaxStructureFlag) != 0) maxStructure = reader.UInt16();
        if ((flags & StackSizeFlag) != 0) stack = reader.UInt16();
        if ((flags & MaxStackSizeFlag) != 0) stackMax = reader.UInt16();
        if ((flags & ContainerFlag) != 0) container = reader.UInt32();
        if ((flags & Wielder) != 0) reader.Skip(4);
        if ((flags & ValidLocationsFlag) != 0) validLocations = reader.UInt32();
        if ((flags & CurrentlyWielded) != 0) reader.Skip(4);
        if ((flags & Priority) != 0) reader.Skip(4);
        if ((flags & RadarBlipColor) != 0) reader.Skip(1);
        if ((flags & RadarBehavior) != 0) reader.Skip(1);
        if ((flags & PScript) != 0) reader.Skip(2);
        if ((flags & Workmanship) != 0) reader.Skip(4);
        if ((flags & Burden) != 0) reader.Skip(2);
        if ((flags & Spell) != 0) reader.Skip(2);
        // The overlay and underlay come after fields only a house or a hook has, which a held item or a chest has none of.
        uint overlay = 0, underlay = 0;
        if ((flags & (HouseOwner | HouseRestrictions)) == 0)
        {
            if ((flags & HookItemTypes) != 0) reader.Skip(4);
            if ((flags & Monarch) != 0) reader.Skip(4);
            if ((flags & HookType) != 0) reader.Skip(2);
            if ((flags & IconOverlay) != 0) overlay = reader.Packed(0x06000000);
            if ((second & IconUnderlay) != 0) underlay = reader.Packed(0x06000000);
        }
        if (container == 0 && itemSlots == 0 && packSlots == 0)
            return _known.Remove(id);
        var visual = new ItemVisual(icon, underlay, overlay, uiEffects, ItemIcon.PlateFor(itemType));
        var partFull = maxStructure > 0 && structure < maxStructure ? (double)Math.Max(0, structure) / maxStructure : (double?)null;
        var known = new Known(id, name, visual, Math.Max(1, stack), stackMax, wcid, validLocations, partFull, container, itemSlots, packSlots);
        if (_known.TryGetValue(id, out var before) && before == known) return false;
        _known[id] = known;
        return true;
    }

    // ModelData: 0x11, the three counts, then the palettes, texture changes and part changes as packed ids, aligned to four.
    private static void SkipModel(ref MessageReader reader)
    {
        reader.Skip(1);
        int palettes = reader.Byte(), textures = reader.Byte(), parts = reader.Byte();
        if (palettes > 0) reader.Packed(0);
        for (var i = 0; i < palettes; i++)
        {
            reader.Packed(0);
            reader.Skip(2);
        }
        for (var i = 0; i < textures; i++)
        {
            reader.Skip(1);
            reader.Packed(0);
            reader.Packed(0);
        }
        for (var i = 0; i < parts; i++)
        {
            reader.Skip(1);
            reader.Packed(0);
        }
        reader.Align();
    }

    // PhysicsDesc: the flags and state, what the flags name in ACE's order, then nine timestamps, aligned to four.
    private static void SkipPhysics(ref MessageReader reader)
    {
        var flags = reader.UInt32();
        reader.Skip(4); // the physics state
        if ((flags & Movement) != 0)
        {
            var length = (int)reader.UInt32();
            reader.Skip(length > 0 ? length + 4 : 0); // the movement, then whether it is autonomous
        }
        else if ((flags & AnimationFrame) != 0) reader.Skip(4);
        if ((flags & Position) != 0) reader.Skip(32);
        foreach (var (flag, size) in new[] { (MTable, 4), (STable, 4), (PeTable, 4), (CSetup, 4), (Parent, 8) })
            if ((flags & flag) != 0) reader.Skip(size);
        if ((flags & Children) != 0) reader.Skip((int)reader.UInt32() * 8);
        foreach (var (flag, size) in new[] { (ObjScale, 4), (Friction, 4), (Elasticity, 4), (Translucency, 4), (Velocity, 12), (Acceleration, 12), (Omega, 12),
                     (DefaultScript, 4), (DefaultScriptIntensity, 4) })
            if ((flags & flag) != 0) reader.Skip(size);
        reader.Skip(18);
        reader.Align();
    }
}
