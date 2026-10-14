using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The selection plaque's health and mana, from the server's game events. Retail asks for both itself when the selection changes
/// (QueryHealth, QueryItemMana), and the answers arrive as UpdateHealth (0x01C0: object id, health fraction) and QueryItemManaResponse
/// (0x0264: object id, mana fraction, success). The latest answer is kept for each object, so an answer for another object (a fellowship
/// member's, or a plugin's query) leaves the selected one's alone.
/// </summary>
internal sealed class SelectionMeters
{
    private const uint UpdateHealth = 0x01C0, QueryItemManaResponse = 0x0264;
    private readonly Dictionary<uint, float> _health = new();
    private readonly Dictionary<uint, float> _mana = new();

    /// <summary>Reads one server message. Any message but these two events is ignored.</summary>
    public void Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        var health = GameChannelTransport.GameEventPayload(messageType, raw, UpdateHealth);
        if (health is { Length: >= 8 }) _health[BinaryPrimitives.ReadUInt32LittleEndian(health)] = BinaryPrimitives.ReadSingleLittleEndian(health.AsSpan(4));
        var mana = GameChannelTransport.GameEventPayload(messageType, raw, QueryItemManaResponse);
        if (mana is { Length: >= 12 })
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(mana);
            // Success is 0 for an item with no mana, and the fraction the server sends then is not a reading.
            if (BinaryPrimitives.ReadUInt32LittleEndian(mana.AsSpan(8)) != 0) _mana[id] = BinaryPrimitives.ReadSingleLittleEndian(mana.AsSpan(4));
            else _mana.Remove(id);
        }
    }

    /// <summary>The health fraction last read for this creature, or null when none has come.</summary>
    public float? Health(uint objectId) => _health.TryGetValue(objectId, out var fraction) ? fraction : null;

    /// <summary>The mana fraction last read for this item, or null when none has come or the item has no mana.</summary>
    public float? Mana(uint objectId) => _mana.TryGetValue(objectId, out var fraction) ? fraction : null;

    /// <summary>Forgets the answers for every object but <paramref name="selected"/>: a selection reads only its own, so the answers stay few.</summary>
    public void KeepOnly(uint selected)
    {
        foreach (var id in _health.Keys.Where(id => id != selected).ToArray()) _health.Remove(id);
        foreach (var id in _mana.Keys.Where(id => id != selected).ToArray()) _mana.Remove(id);
    }
}
