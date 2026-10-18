namespace LegACEy.Client.Demo;

/// <summary>Body of the floating combat text push; it must match ACE.Server.ClientChannel.CombatTextChannelActions.</summary>
public static class CombatTextProtocol
{
    public const string Hit = "combat.hit";

    /// <summary>A hit: the object hit, the damage, its damage type flags (ACE's DamageType), and whether it was critical or a damage-over-time tick.</summary>
    public static (uint Target, uint Amount, uint DamageType, bool Critical, bool Periodic) ReadHit(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var target = reader.ReadUInt32();
        var amount = reader.ReadUInt32();
        var type = reader.ReadUInt32();
        var flags = reader.ReadByte();
        return (target, amount, type, (flags & 1) != 0, (flags & 2) != 0);
    }
}
