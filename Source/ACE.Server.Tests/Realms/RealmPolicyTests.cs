using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Realms;

namespace ACE.Server.Tests.Realms
{
    [TestClass]
    public class RealmPolicyTests
    {
        [TestMethod]
        public void CanEnter_DefaultsToHomeRealmAndEphemeral_AndOtherRealmsToAdminsOnly()
        {
            Assert.IsTrue(RealmPolicy.Allows(isAdmin: false, realmId: 0, homeRealmId: 0, ephemeral: false), "the home realm");
            Assert.IsTrue(RealmPolicy.Allows(isAdmin: false, realmId: 3, homeRealmId: 0, ephemeral: true), "an ephemeral instance");
            Assert.IsFalse(RealmPolicy.Allows(isAdmin: false, realmId: 3, homeRealmId: 0, ephemeral: false), "another realm, not an admin");
            Assert.IsTrue(RealmPolicy.Allows(isAdmin: true, realmId: 3, homeRealmId: 0, ephemeral: false), "another realm, an admin");
        }

        [TestMethod]
        public void TeleportDestination_StaysInTheCopy_LeavesAnEphemeralCopyUnlessInsideIt_AndKeepsAnInstanceItNames()
        {
            var worldPortal = new Position(0xA9B40019, 84, 7.1f, 94, 0, 0, 0, 1);
            var dungeonPortal = new Position(0x7F040100, 10, 10, 0, 0, 0, 0, 1);
            var realmCopy = Position.InstanceIDFromVars(1, 0, false);
            var dungeonCopy = Position.InstanceIDFromVars(2, 5, true);
            IReadOnlyList<ushort> Template(ushort realm) => realm == 2 ? new ushort[] { 0x7F04 } : null;

            Assert.IsNull(RealmPolicy.TeleportDestination(worldPortal, null, Template).Instance, "base world stays in the base world");
            Assert.AreEqual(realmCopy, RealmPolicy.TeleportDestination(worldPortal, realmCopy, Template).Instance, "a realm copy stays in its copy");
            Assert.IsNull(RealmPolicy.TeleportDestination(worldPortal, dungeonCopy, Template).Instance, "out of an ephemeral copy goes to the base world");
            Assert.AreEqual(dungeonCopy, RealmPolicy.TeleportDestination(dungeonPortal, dungeonCopy, Template).Instance, "inside an ephemeral copy stays in it");

            var namedInstance = new Position(worldPortal) { Instance = realmCopy };
            Assert.AreEqual(realmCopy, RealmPolicy.TeleportDestination(namedInstance, dungeonCopy, Template).Instance, "a named instance is kept");
        }

        [TestMethod]
        public void CanBuyHouseAt_OnlyTheHomeRealm_NeverAnEphemeralCopy()
        {
            var cottage = new Position(0xA9B40019, 84, 7.1f, 94, 0, 0, 0, 1);

            Assert.IsTrue(RealmPolicy.CanBuyHouseAt(cottage, homeRealm: 0), "the base world, home realm 0");
            Assert.IsFalse(RealmPolicy.CanBuyHouseAt(cottage, homeRealm: 1), "the base world, home realm 1");
            Assert.IsTrue(RealmPolicy.CanBuyHouseAt(new Position(cottage) { Instance = Position.InstanceIDFromVars(1, 0, false) }, homeRealm: 1), "realm 1, home realm 1");
            Assert.IsFalse(RealmPolicy.CanBuyHouseAt(new Position(cottage) { Instance = Position.InstanceIDFromVars(1, 3, true) }, homeRealm: 1), "an ephemeral copy of realm 1");
        }
    }
}
