using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Realms
{
    /// <summary>
    /// Who may go where. Callers ask here; changing a rule means changing one method.
    /// </summary>
    public static class RealmPolicy
    {
        /// <summary>
        /// The player's home realm: PropertyInt.HomeRealm if that realm is loaded, else the base world.
        /// </summary>
        public static ushort HomeRealmOf(Player player) => HomeRealmOf(player.GetProperty(PropertyInt.HomeRealm), RealmManager.Exists);

        /// <summary>
        /// The home realm a stored value names. Null, 0, or a realm that isn't loaded is the base world.
        /// </summary>
        public static ushort HomeRealmOf(long? storedHomeRealm, Func<ushort, bool> realmExists) =>
            storedHomeRealm is long realm && IsLoaded(realm, realmExists) ? (ushort)realm : (ushort)0;

        /// <summary>
        /// A fixed position (lifestone, marketplace, arena) in the player's home realm
        /// </summary>
        public static Position InHomeRealm(Player player, Position position) => InRealm(position, HomeRealmOf(player));

        /// <summary>
        /// The same spot in a realm's primary copy. The base world is a null Instance, as a saved position has it.
        /// </summary>
        public static Position InRealm(Position position, ushort realmId) =>
            new Position(position) { Instance = Position.InstanceOrNull(Position.InstanceIDFromVars(realmId, 0, false)) };

        /// <summary>
        /// The position a character logs in at: the saved spot in the home realm. It keeps the saved copy when that is in
        /// the home realm.
        /// An ephemeral copy is gone once its session ends, so its character goes to the template's exit in the home realm,
        /// or to the same spot in the home realm when the template is not loaded. A missing realm also goes to the base
        /// world at the same spot. reason says why, for the caller to log.
        /// </summary>
        public static Position LoginPosition(Position saved, long? homeRealm, Func<ushort, bool> realmExists, Func<ushort, Position> templateExit, out string reason)
        {
            var instance = saved.Instance ?? 0;
            Position.ParseInstanceID(instance, out _, out var savedRealm, out _);

            if (saved.IsEphemeral)
            {
                var exit = templateExit(savedRealm);
                reason = exit != null ? $"its ephemeral copy 0x{instance:X8} is gone, so it returns to the template exit"
                    : $"its ephemeral copy 0x{instance:X8} has no loaded template";
                return InRealm(exit ?? saved, HomeRealmOf(homeRealm, realmExists));
            }

            reason = !IsLoaded(savedRealm, realmExists) ? $"its saved realm {savedRealm} is not loaded"
                : homeRealm is long home && !IsLoaded(home, realmExists) ? $"its home realm {home} is not loaded"
                : null;

            if (reason != null)
                return BaseWorldAt(saved);

            // A character lives in its home realm: one saved in another realm (an admin's visit, or a home realm changed
            // since) comes back at the same spot in the home realm.
            var homeRealmId = HomeRealmOf(homeRealm, realmExists);
            if (savedRealm != homeRealmId)
            {
                reason = $"its home realm is {homeRealmId}, not its saved realm {savedRealm}";
                return InRealm(saved, homeRealmId);
            }

            return new Position(saved);
        }

        private static Position BaseWorldAt(Position saved) => new Position(saved) { Instance = null };

        /// <summary>
        /// Where a portal, an NPC teleport or a /tele command sends a player. A destination that names its instance keeps it.
        /// Otherwise the player stays in their copy, except that an ephemeral copy sends them to the base world unless the
        /// destination is one of the copy's own landblocks.
        /// </summary>
        public static Position TeleportDestination(Position destination, uint? currentInstance) =>
            TeleportDestination(destination, currentInstance, realm => RealmManager.Get(realm)?.Landblocks);

        public static Position TeleportDestination(Position destination, uint? currentInstance, Func<ushort, IReadOnlyList<ushort>> templateLandblocks)
        {
            if (destination.Instance != null)
                return new Position(destination);

            var instance = currentInstance ?? 0;
            Position.ParseInstanceID(instance, out var isEphemeral, out var realmId, out _);
            var staysInCopy = !isEphemeral || (templateLandblocks(realmId)?.Contains(destination.LandblockId.Landblock) ?? false);

            return new Position(destination) { Instance = staysInCopy ? Position.InstanceOrNull(instance) : null };
        }

        /// <summary>
        /// A house is bought in the buyer's home realm, never in an ephemeral copy: the copy, and the house with it, is deleted when it ends.
        /// </summary>
        public static bool CanBuyHouseAt(Player player, Position house) => CanBuyHouseAt(house, HomeRealmOf(player));

        public static bool CanBuyHouseAt(Position house, ushort homeRealm) => !house.IsEphemeral && RealmManager.RealmOf(house.Instance) == homeRealm;

        /// <summary>
        /// Called when a character moves into another realm. The default binds nothing, so carried items travel with the character.
        /// </summary>
        public static void BindItemsOnRealmChange(Player player, ushort fromRealm, ushort toRealm)
        {
        }

        /// <summary>
        /// House limit scope, the one place that decides it. Stock ACE: one house per account, or per character with house_per_char.
        /// Every realm counts.
        /// </summary>
        public static bool HouseLimitIsPerCharacter => PropertyManager.GetBool("house_per_char").Item;

        /// <summary>
        /// The owned houses, from every realm, that count against the player's limit of one
        /// </summary>
        public static List<House> HousesCountedFor(IPlayer player) => HouseLimitIsPerCharacter
            ? HouseManager.GetCharacterHouses(player.Guid.ClientGUID)
            : HouseManager.GetAccountHouses(player.Account.AccountId);

        /// <summary>
        /// True if the player may not buy a house: stock ACE, the character owns one, or with one house per account any character on it does.
        /// A character holds one house id whatever realm it's in, so a house in any realm counts.
        /// </summary>
        public static bool HouseLimitReached(Player player) => player.GetHouseInstance() != null;

        /// <summary>
        /// The base world is always loaded. Any other realm must be an id in range that the realm list has.
        /// </summary>
        private static bool IsLoaded(long realmId, Func<ushort, bool> realmExists) =>
            realmId == 0 || (realmId > 0 && realmId <= RealmLoader.MaxRealmId && realmExists((ushort)realmId));

        /// <summary>Making a copy of a dungeon template (@instance create): admins only</summary>
        public static bool CanCreateInstance(Player player, RealmDefinition template) =>
            player.Session.AccessLevel >= AccessLevel.Admin;

        /// <summary>The home realm and ephemeral instances: anyone. Other realms: admins only.</summary>
        public static bool CanEnter(Player player, ushort realmId, bool ephemeral) =>
            Allows(player.Session.AccessLevel >= AccessLevel.Admin, realmId, HomeRealmOf(player), ephemeral);

        public static bool Allows(bool isAdmin, ushort realmId, ushort homeRealmId, bool ephemeral) =>
            ephemeral || realmId == homeRealmId || isAdmin;
    }
}
