using System.Collections;
using System.Collections.Generic;

namespace ACE.Server.Realms
{
    /// <summary>
    /// Hands out short instance ids 1..65535 per realm, in order and wrapping, and never hands out an id that is still held.
    /// </summary>
    public sealed class ShortInstanceAllocator
    {
        public const ushort First = 1;
        public const ushort Last = ushort.MaxValue;

        private sealed class Pool
        {
            public readonly BitArray Held = new BitArray(Last + 1);
            public int Next = First;
        }

        private readonly object gate = new object();
        private readonly Dictionary<ushort, Pool> pools = new Dictionary<ushort, Pool>();

        /// <summary>The next free id in the realm, or null when all 65535 are held</summary>
        public ushort? Allocate(ushort realmId)
        {
            lock (gate)
            {
                if (!pools.TryGetValue(realmId, out var pool))
                    pools[realmId] = pool = new Pool();

                for (var tries = 0; tries < Last; tries++)
                {
                    var candidate = pool.Next;
                    pool.Next = candidate == Last ? First : candidate + 1;

                    if (!pool.Held[candidate])
                    {
                        pool.Held[candidate] = true;
                        return (ushort)candidate;
                    }
                }

                return null;
            }
        }

        /// <summary>Makes an id available again. The caller releases it only once nothing is loaded under it.</summary>
        public void Release(ushort realmId, ushort shortInstance)
        {
            lock (gate)
            {
                if (pools.TryGetValue(realmId, out var pool))
                    pool.Held[shortInstance] = false;
            }
        }
    }
}
