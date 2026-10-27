using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>The look cache reads the game messages as ACE writes them (WorldObject_Networking's ModelData and PhysicsDesc).</summary>
public sealed class LookCacheTests
{
    private const uint Player = 0x50000001, Sword = 0x80000010;

    [Fact]
    public void A_created_object_s_look_is_its_setup_and_ObjDesc_with_the_file_types_put_back()
    {
        var cache = new LookCache();
        cache.Receive(LookCache.CreateObject, Created(Player, setup: 0x02000001, moving: true, objDesc: w =>
        {
            ObjDescHeader(w, palettes: 1, textures: 1, parts: 1);
            Packed(w, 0x7E);              // base palette 0x0400007E
            Packed(w, 0x1A2B);            // sub-palette 0x04001A2B
            w.Write((byte)3); w.Write((byte)2);
            w.Write((byte)9);             // texture on part 9
            Packed(w, 0x10);
            Packed(w, 0x12345);           // more than 15 bits: four bytes
            w.Write((byte)4);             // part 4
            Packed(w, 0x5000);
        }));

        var look = cache.Look(Player)!;
        Assert.Equal(0x02000001u, look.SetupId);
        Assert.Equal(0x0400007Eu, look.PaletteId);
        var palette = Assert.Single(look.SubPalettes);
        Assert.Equal((0x04001A2Bu, 3, 2), (palette.PaletteId, palette.Offset, palette.Length));
        var texture = Assert.Single(look.TextureChanges);
        Assert.Equal((9, 0x05000010u, 0x05012345u), (texture.Part, texture.OldTexture, texture.NewTexture));
        var part = Assert.Single(look.PartChanges);
        Assert.Equal((4, 0x01005000u), (part.Part, part.Model));
        Assert.Null(cache.Look(Sword));
    }

    [Fact]
    public void What_a_creature_holds_follows_wielding_and_unwielding_and_each_change_is_raised()
    {
        var cache = new LookCache();
        var changed = new List<uint>();
        cache.Changed += changed.Add;
        cache.Receive(LookCache.CreateObject, Created(Player, setup: 0x02000001));
        cache.Receive(LookCache.CreateObject, Created(Sword, setup: 0x02000A00, parent: (Player, 1), scale: 0.8f));

        var held = Assert.Single(cache.Look(Player)!.Held);
        Assert.Equal((1u, 0.8f, 0x02000A00u), (held.Location, held.Scale, held.Look.SetupId));
        Assert.Contains(Player, changed);

        changed.Clear();
        cache.Receive(LookCache.PickupEvent, Message(LookCache.PickupEvent, w => { w.Write(Sword); w.Write(0); }));
        Assert.Empty(cache.Look(Player)!.Held);
        Assert.Equal(new[] { Player }, changed);

        cache.Receive(LookCache.ParentEvent, Message(LookCache.ParentEvent, w => { w.Write(Player); w.Write(Sword); w.Write(2); w.Write(0); w.Write(0); }));
        Assert.Equal(2u, Assert.Single(cache.Look(Player)!.Held).Location);
    }

    [Fact]
    public void An_ObjDesc_event_changes_the_look_and_keeps_the_setup()
    {
        var cache = new LookCache();
        cache.Receive(LookCache.CreateObject, Created(Player, setup: 0x02000001));
        var changed = new List<uint>();
        cache.Changed += changed.Add;

        cache.Receive(LookCache.ObjDescEvent, Message(LookCache.ObjDescEvent, w =>
        {
            w.Write(Player);
            ObjDescHeader(w, palettes: 0, textures: 0, parts: 1);
            w.Write((byte)7);
            Packed(w, 0x22);
            Align(w);
        }, prefixType: false));

        var look = cache.Look(Player)!;
        Assert.Equal(0x02000001u, look.SetupId);
        Assert.Equal(0x01000022u, Assert.Single(look.PartChanges).Model);
        Assert.Equal(new[] { Player }, changed);
    }

    [Fact]
    public void A_truncated_message_changes_nothing()
    {
        var cache = new LookCache();
        cache.Receive(LookCache.CreateObject, Created(Player, setup: 0x02000001));
        cache.Receive(LookCache.CreateObject, Created(Sword, setup: 0x02000A00, parent: (Player, 1)));
        var update = Created(Sword, setup: 0x02000B00, parent: (Player, 2), scale: 2f);
        cache.Receive(LookCache.UpdateObject, update.AsSpan(0, update.Length - 22));

        var held = Assert.Single(cache.Look(Player)!.Held);
        Assert.Equal((1u, 0x02000A00u), (held.Location, held.Look.SetupId));
    }

    [Fact]
    public void An_object_without_a_setup_has_no_look_and_a_deleted_holder_holds_nothing()
    {
        var cache = new LookCache();
        cache.Receive(LookCache.CreateObject, Created(Player, setup: 0));
        Assert.Null(cache.Look(Player));

        cache.Receive(LookCache.CreateObject, Created(Player, setup: 0x02000001));
        cache.Receive(LookCache.CreateObject, Created(Sword, setup: 0x02000A00, parent: (Player, 1)));
        cache.Receive(LookCache.DeleteObject, Message(LookCache.DeleteObject, w => { w.Write(Player); w.Write(0); }));
        cache.Receive(LookCache.CreateObject, Created(Player, setup: 0x02000001));
        Assert.Empty(cache.Look(Player)!.Held);
    }

    [Fact]
    public void Pruning_forgets_objects_the_client_let_go_and_what_they_held()
    {
        const uint Shield = 0x80000011, Drudge = 0x80000020;
        var cache = new LookCache();
        cache.Receive(LookCache.CreateObject, Created(Player, setup: 0x02000001));
        cache.Receive(LookCache.CreateObject, Created(Sword, setup: 0x02000A00, parent: (Player, 1)));
        cache.Receive(LookCache.CreateObject, Created(Drudge, setup: 0x02000B00));
        cache.Receive(LookCache.CreateObject, Created(Shield, setup: 0x02000C00, parent: (Drudge, 3)));

        cache.Prune(id => id is Player or Sword or Shield);

        Assert.Null(cache.Look(Drudge));
        Assert.Single(cache.Look(Player)!.Held);
        // Seen again, the drudge does not hold the shield it held before.
        cache.Receive(LookCache.CreateObject, Created(Drudge, setup: 0x02000B00));
        Assert.Empty(cache.Look(Drudge)!.Held);
    }

    /// <summary>A CreateObject as ACE writes it, with only the parts the cache reads (no weenie description after them).</summary>
    internal static byte[] Created(uint id, uint setup, bool moving = false, (uint Holder, uint Location)? parent = null, float? scale = null,
        Action<BinaryWriter>? objDesc = null) => Message(LookCache.CreateObject, w =>
    {
        w.Write(id);
        if (objDesc != null) objDesc(w);
        else ObjDescHeader(w, 0, 0, 0);
        Align(w);
        uint flags = 0x1 | 0x2 | 0x8000 | (moving ? 0x10000u : 0x20000u) | (parent != null ? 0x20u : 0) | (scale != null ? 0x80u : 0);
        w.Write(flags);
        w.Write(0x408u); // physics state
        if (moving)
        {
            w.Write(8u);
            w.Write(new byte[8]);
            w.Write(1u);
        }
        else w.Write(0x65u);
        w.Write(new byte[32]); // position
        w.Write(0x09000001u);  // motion table
        w.Write(setup);
        if (parent is { } p)
        {
            w.Write(p.Holder);
            w.Write(p.Location);
        }
        if (scale is { } s) w.Write(s);
        w.Write(new byte[18]); // timestamps
    });

    private static byte[] Message(int type, Action<BinaryWriter> write, bool prefixType = true)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        if (prefixType) w.Write((uint)type);
        write(w);
        return stream.ToArray();
    }

    private static void ObjDescHeader(BinaryWriter w, int palettes, int textures, int parts)
    {
        w.Write((byte)0x11);
        w.Write((byte)palettes);
        w.Write((byte)textures);
        w.Write((byte)parts);
    }

    // ACE's WritePackedDword, after WritePackedDwordOfKnownType has taken the file type off.
    private static void Packed(BinaryWriter w, uint value)
    {
        if (value <= 32767) w.Write((ushort)value);
        else w.Write((value << 16) | ((value >> 16) | 0x8000));
    }

    private static void Align(BinaryWriter w)
    {
        while (w.BaseStream.Position % 4 != 0) w.Write((byte)0);
    }
}
