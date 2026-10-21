using ACE.Server.Entity;
using ACE.Server.Realms;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The place name channel action: the LegACEy client's radar names a dungeon on its plaque from this. A realm the player is in names
    /// its place; otherwise the shared list does.
    /// </summary>
    public static class PlaceChannelActions
    {
        /// <summary>
        /// Request: u32 cell id (0xLLLLCCCC). Reply: a string, the place's name, or empty when it has none. The client's PlaceProtocol reads it.
        /// </summary>
        public const string Name = "place.name";

        public static void Register() => ServerChannel.Register(Name, context =>
        {
            if (context.Request.Body.Length != 4)
            {
                context.Fail(ChannelStatus.BadRequest, "A place name is asked for one cell id.");
                return;
            }

            uint cell;
            using (var reader = context.Body())
                cell = reader.ReadUInt32();

            var landblock = (ushort)(cell >> 16);
            // The realm's name is for the landblock the player is in: a copy of a dungeon shares its landblock ids with the base world.
            var here = context.Player.CurrentLandblock;
            var realm = here != null && here.Id.Landblock == landblock ? RealmManager.Get(RealmManager.RealmOf(here.Instance)) : null;
            context.Reply(ServerChannel.Text(NameFor(landblock, realm)));
        });

        /// <summary>
        /// The name of a place: the realm's name when the player is in a realm that has one (the base world has none), else the shared
        /// list's name when the landblock has exactly one. Empty when neither gives one.
        /// </summary>
        internal static string NameFor(ushort landblock, RealmDefinition realm)
        {
            if (realm != null && realm.Id != RealmDefinition.BaseWorld.Id && !string.IsNullOrEmpty(realm.Name)) return realm.Name;
            var names = LandblockNames.Get(landblock);
            return names.Count == 1 ? names[0] : string.Empty;
        }
    }
}
