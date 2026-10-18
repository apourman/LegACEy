using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Realms;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// The status a player had before a pvp realm made them PK, so leaving gives it back. Not saved: after a
        /// restart PkLevel stands in for it, which is exact except for PKLite.
        /// </summary>
        private PlayerKillerStatus? pkStatusBeforePvpRealm;

        public bool InPvpRealm => RealmManager.GetRule(RealmManager.RealmOf(Location.Instance), RealmRules.Pvp);

        /// <summary>
        /// At login, a character in an ephemeral copy returns to its template's exit, and one saved outside its home realm comes back at the same spot in the home realm. One whose saved realm or home realm isn't loaded goes to the base world at the same spot. The reason is logged.
        /// </summary>
        public void ResolveLoginRealm()
        {
            var login = RealmPolicy.LoginPosition(Location, GetProperty(PropertyInt.HomeRealm), RealmManager.Exists, realm => RealmManager.Get(realm)?.Exit, out var reason);

            if (reason == null)
                return;

            log.Warn($"[REALMS] {Name} logs in at {login.ToLOCString()} instead of instance 0x{(Location.Instance ?? 0):X8}: {reason}");

            Location = login;
        }

        /// <summary>
        /// Sets PK status for the realm the player is in. A pvp realm makes them PK. Outside a pvp realm, leaving
        /// restores the status they had before, unless a PK death respite is running (then they stay NPK).
        /// </summary>
        /// <param name="leftPvpRealm">the player was in a pvp realm before this move; without a saved status, PkLevel is the fallback</param>
        public void SyncPkStatusWithRealm(bool leftPvpRealm, bool broadcast)
        {
            if (InPvpRealm)
            {
                // a respite after a PK death stays NPK; PK_DeathTick makes the player PK when it ends
                if (MinimumTimeSincePk != null || PlayerKillerStatus == PlayerKillerStatus.PK)
                    return;

                pkStatusBeforePvpRealm ??= PlayerKillerStatus;
                SetRealmPkStatus(PlayerKillerStatus.PK, broadcast);
                return;
            }

            if (pkStatusBeforePvpRealm.HasValue)
            {
                var before = pkStatusBeforePvpRealm.Value;
                pkStatusBeforePvpRealm = null;

                // a respite keeps the NPK it set; the saved status is dropped, and PK_DeathTick gives PkLevel's status when it ends
                if (MinimumTimeSincePk == null)
                    SetRealmPkStatus(before, broadcast);
            }
            else if (leftPvpRealm && PlayerKillerStatus == PlayerKillerStatus.PK && MinimumTimeSincePk == null && !PropertyManager.GetBool("pk_server").Item)
            {
                // PK with no saved status after a restart: PkLevel is the status the player chose.
                // On a pk_server every character is PK without PkLevel, so there is nothing to undo.
                var normal = PkLevel switch
                {
                    PKLevel.PK => PlayerKillerStatus.PK,
                    PKLevel.Free => PlayerKillerStatus.Free,
                    _ => PlayerKillerStatus.NPK,
                };
                SetRealmPkStatus(normal, broadcast);
            }
        }

        private void SetRealmPkStatus(PlayerKillerStatus status, bool broadcast)
        {
            PlayerKillerStatus = status;

            if (broadcast)
                EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(this, PropertyInt.PlayerKillerStatus, (int)status));
        }
    }
}
