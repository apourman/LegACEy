namespace ACE.Entity
{
    public enum GuidType
    {
        Undef,
        Player,
        Static,
        Dynamic,
    }

    public struct ObjectGuid
    {
        public static readonly ObjectGuid Invalid = new ObjectGuid(0);

        /* These are not GUIDs
        public static uint WeenieMin { get; } = 0x00000001;
        public static uint WeenieMax { get; } = 0x000F423F; // 999,999 */

        // 0x01000001 and 0x422C91BC Only PCAP'd 9 GUID's found in this range 

        public static uint PlayerMin { get; } = 0x50000001;
        public static uint PlayerMax { get; } = 0x5FFFFFFF;

        // 0x60000000 No PCAP'd GUID's in this range

        // PY 16 has these ranges 0x70003000 - 0x7FADA053
        // They are organized by landblock where 0x7AABB000 is landblock AABB
        // These represent items that come from the World db
        public static uint StaticObjectMin { get; } = 0x70000000;
        public static uint StaticObjectMax { get; } = 0x7FFFFFFF;

        // These represent items are generated in the world. Some of them will be saved to the Shard db.
        public static uint DynamicMin { get; } = 0x80000000;
        public static uint DynamicMax { get; } = 0xFFFFFFFE; // Ends at E because uint.Max is reserved for "invalid"

        public static bool IsPlayer(uint guid) { return (guid >= PlayerMin && guid <= PlayerMax); }
        public static bool IsStatic(uint guid) { return (guid >= StaticObjectMin && guid <= StaticObjectMax); }
        public static bool IsDynamic(uint guid) { return (guid >= DynamicMin && guid <= DynamicMax); }

        // A stored 64-bit id. Only statics carry an instance in the upper 32 bits.
        public static bool IsPlayer(ulong id) { return id <= uint.MaxValue && IsPlayer((uint)id); }
        public static bool IsStatic(ulong id) { return IsStatic((uint)id); }
        public static bool IsDynamic(ulong id) { return id <= uint.MaxValue && IsDynamic((uint)id); }

        // The server identity, the only one the server keeps. For a static in a landblock copy the upper 32 bits hold the instance; otherwise it is the 32-bit guid.
        public ulong Full { get; }

        // The 32-bit guid the client sees. Wire writes and 32-bit storage use this.
        public uint ClientGUID => (uint)Full;
        public uint Low => ClientGUID & 0xFFFFFF;
        public uint High => ClientGUID >> 24;
        public GuidType Type { get; }

        // Landblock copy a static object belongs to. 0 is the base world. Always 0 for players and dynamics.
        public uint Instance => (uint)(Full >> 32);

        public ObjectGuid(ulong full)
        {
            var guid = (uint)full;

            if (IsPlayer(guid))
                Type = GuidType.Player;
            else if (IsStatic(guid))
                Type = GuidType.Static;
            else if (IsDynamic(guid))
                Type = GuidType.Dynamic;
            else
                Type = GuidType.Undef;

            Full = Type == GuidType.Static ? full : guid;
        }

        public ObjectGuid(uint guid, uint instance) : this(((ulong)instance << 32) | guid)
        {
        }

        public bool IsPlayer()
        {
            return Type == GuidType.Player;
        }

        public bool IsStatic()
        {
            return Type == GuidType.Static;
        }

        public bool IsDynamic()
        {
            return Type == GuidType.Dynamic;
        }

        public static bool operator ==(ObjectGuid g1, ObjectGuid g2)
        {
            return g1.Full == g2.Full;
        }

        public static bool operator !=(ObjectGuid g1, ObjectGuid g2)
        {
            return g1.Full != g2.Full;
        }

        public override bool Equals(object obj)
        {
            return obj is ObjectGuid guid && guid == this;
        }

        public override int GetHashCode()
        {
            return Full.GetHashCode();
        }

        // X8 of the client guid in the base world; X16 of the full guid for a static in a landblock copy, so two copies' statics print differently.
        public override string ToString()
        {
            return Instance == 0 ? ClientGUID.ToString("X8") : Full.ToString("X16");
        }
    }
}
