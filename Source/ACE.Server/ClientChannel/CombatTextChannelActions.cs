using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// Floating combat text: every hit on a creature or player (melee, missile, war spell or a damage-over-time tick) is pushed to each
    /// LegACEy client that can see the target, so anyone nearby sees the number over it, not only the fighters.
    /// </summary>
    public static class CombatTextChannelActions
    {
        /// <summary>Push: u32 target guid, u32 damage, u32 damage type flags, u8 flags (1 critical, 2 periodic). The client's CombatTextProtocol reads it.</summary>
        public const string Hit = "combat.hit";

        public const byte Critical = 1, Periodic = 2;

        /// <summary>Tells everyone who can see the target about damage it just took. Nothing for no damage.</summary>
        public static void Damaged(Creature target, uint amount, DamageType damageType, bool critical, bool periodic)
        {
            if (amount == 0 || target?.PhysicsObj == null) return;

            var body = ChannelWire.Body(w =>
            {
                w.Write((uint)target.Guid.Full);
                w.Write(amount);
                w.Write((uint)damageType);
                w.Write((byte)((critical ? Critical : 0) | (periodic ? Periodic : 0)));
            });
            if (target is Player self) ServerChannel.Push(self, Hit, body);
            foreach (var player in target.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
                if (player != target) ServerChannel.Push(player, Hit, body);
        }
    }
}
