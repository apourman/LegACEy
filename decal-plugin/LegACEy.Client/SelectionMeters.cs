using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The selection plaque's health and mana, from the server's game events. Retail asks for both itself when the selection changes
/// (QueryHealth, QueryItemMana), and the answers arrive as UpdateHealth (0x01C0: object id, health fraction) and QueryItemManaResponse
/// (0x0264: object id, mana fraction, success). The latest answer is kept for each object, so an answer for another object (a fellowship
/// member's, or a plugin's query) leaves the selected one's alone. With a selection source, only the selected object's answers are kept.
/// </summary>
internal sealed class SelectionMeters
{
    private const uint UpdateHealth = 0x01C0, QueryItemManaResponse = 0x0264;
    // The selected object's id, read when an answer arrives; null keeps every answer (for a reader with no selection of its own).
    private readonly Func<uint>? _selected;
    private readonly Dictionary<uint, float> _health = new();
    private readonly Dictionary<uint, float> _mana = new();

    public SelectionMeters(Func<uint>? selected = null) => _selected = selected;

    /// <summary>Reads one server message. Any message but these two events is ignored.</summary>
    public void Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        var health = GameChannelTransport.GameEventPayload(messageType, raw, UpdateHealth);
        if (health is { Length: >= 8 }) Store(_health, BinaryPrimitives.ReadUInt32LittleEndian(health), BinaryPrimitives.ReadSingleLittleEndian(health.AsSpan(4)));
        var mana = GameChannelTransport.GameEventPayload(messageType, raw, QueryItemManaResponse);
        if (mana is { Length: >= 12 })
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(mana);
            // Success is 0 for an item with no mana, and the fraction the server sends then is not a reading.
            if (BinaryPrimitives.ReadUInt32LittleEndian(mana.AsSpan(8)) != 0) Store(_mana, id, BinaryPrimitives.ReadSingleLittleEndian(mana.AsSpan(4)));
            else if (Wanted(id)) _mana.Remove(id);
        }
    }

    /// <summary>The health fraction last read for this creature, or null when none has come.</summary>
    public float? Health(uint objectId) => _health.TryGetValue(objectId, out var fraction) ? fraction : null;

    /// <summary>The mana fraction last read for this item, or null when none has come or the item has no mana.</summary>
    public float? Mana(uint objectId) => _mana.TryGetValue(objectId, out var fraction) ? fraction : null;

    /// <summary>
    /// Forgets the answers for every object but <paramref name="selected"/>. Allocation-free: a map is cleared, and only the selected
    /// object's entry, when there is one, is written back.
    /// </summary>
    public void KeepOnly(uint selected)
    {
        Trim(_health, selected);
        Trim(_mana, selected);
    }

    private void Store(Dictionary<uint, float> answers, uint id, float fraction)
    {
        if (!Wanted(id)) return;
        // Only the selected object's answer is kept, so a map holds at most that one entry when a selection source is given.
        if (_selected != null) KeepOnly(id);
        answers[id] = fraction;
    }

    private bool Wanted(uint id) => _selected == null || id == _selected();

    private static void Trim(Dictionary<uint, float> answers, uint keep)
    {
        if (answers.Count == 0 || (answers.Count == 1 && answers.ContainsKey(keep))) return;
        if (answers.TryGetValue(keep, out var fraction))
        {
            answers.Clear();
            answers[keep] = fraction;
        }
        else answers.Clear();
    }
}
