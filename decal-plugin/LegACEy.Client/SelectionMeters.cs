using System;
using System.Buffers.Binary;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The selection plaque's health and mana, from the server's game events. Retail asks for both itself when the selection changes
/// (QueryHealth, QueryItemMana), and the answers arrive as UpdateHealth (0x01C0: object id, health fraction) and QueryItemManaResponse
/// (0x0264: object id, mana fraction, success). Only the latest answer for each is kept.
/// </summary>
internal sealed class SelectionMeters
{
    private const uint UpdateHealth = 0x01C0, QueryItemManaResponse = 0x0264;
    private (uint Id, float Fraction) _health, _mana;

    /// <summary>Reads one server message. Any message but these two events is ignored.</summary>
    public void Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        var health = GameChannelTransport.GameEventPayload(messageType, raw, UpdateHealth);
        if (health is { Length: >= 8 }) _health = (BinaryPrimitives.ReadUInt32LittleEndian(health), BinaryPrimitives.ReadSingleLittleEndian(health.AsSpan(4)));
        var mana = GameChannelTransport.GameEventPayload(messageType, raw, QueryItemManaResponse);
        if (mana is { Length: >= 8 }) _mana = (BinaryPrimitives.ReadUInt32LittleEndian(mana), BinaryPrimitives.ReadSingleLittleEndian(mana.AsSpan(4)));
    }

    /// <summary>The health fraction last read for this creature, or null when none has come.</summary>
    public float? Health(uint objectId) => _health.Id == objectId ? _health.Fraction : null;

    /// <summary>The mana fraction last read for this item, or null when none has come.</summary>
    public float? Mana(uint objectId) => _mana.Id == objectId ? _mana.Fraction : null;
}
