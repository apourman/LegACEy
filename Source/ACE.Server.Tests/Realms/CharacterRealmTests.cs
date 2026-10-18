using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Realms;

namespace ACE.Server.Tests.Realms
{
    [TestClass]
    public class CharacterRealmTests
    {
        private static readonly uint SavedCell = 0x7F0401AD;

        private static Position Spot(uint? instance) =>
            new Position(SavedCell, 12, 3, 0.005f, 0, 0, 0, 1) { Instance = instance };

        [TestMethod]
        public void Login_InALoadedRealm_KeepsTheSavedCopy()
        {
            var saved = Spot(Position.InstanceIDFromVars(5, 0, false));

            var login = RealmPolicy.LoginPosition(saved, homeRealm: 5, realmExists: id => id == 5, templateExit: id => null, out var reason);

            Assert.IsNull(reason);
            Assert.AreEqual(saved.Instance, login.Instance, "the character logs back into realm 5's copy");
            Assert.AreEqual(saved.Cell, login.Cell);
        }

        [TestMethod]
        public void Login_OutsideTheHomeRealm_ComesBackAtTheSameSpotInTheHomeRealm()
        {
            var inBaseWorld = Spot(null);
            var inRealm5 = Spot(Position.InstanceIDFromVars(5, 0, false));

            var toRealm = RealmPolicy.LoginPosition(inBaseWorld, homeRealm: 5, realmExists: id => id == 5, templateExit: id => null, out var reason);
            Assert.IsNotNull(reason);
            Assert.AreEqual(Position.InstanceIDFromVars(5, 0, false), toRealm.Instance, "a base-world save with home realm 5 logs into realm 5");
            Assert.AreEqual(inBaseWorld.Cell, toRealm.Cell);

            var toBase = RealmPolicy.LoginPosition(inRealm5, homeRealm: 0, realmExists: id => id == 5, templateExit: id => null, out _);
            Assert.IsNull(toBase.Instance, "a visit to realm 5 with home realm 0 logs into the base world");
        }

        [TestMethod]
        public void Login_WhenTheSavedOrHomeRealmIsMissing_GoesToTheBaseWorldAtTheSameSpot()
        {
            var inMissingRealm = Spot(Position.InstanceIDFromVars(5, 0, false));
            var inEphemeralCopy = Spot(Position.InstanceIDFromVars(5, 1, true));
            var inBaseWorld = Spot(null);

            var missingSaved = RealmPolicy.LoginPosition(inMissingRealm, homeRealm: 0, realmExists: id => false, templateExit: id => null, out var savedReason);
            Assert.IsNotNull(savedReason, "a saved realm that isn't loaded is logged");
            Assert.IsNull(missingSaved.Instance, "and the character is placed in the base world");
            Assert.AreEqual(inMissingRealm.Cell, missingSaved.Cell, "at the saved cell");

            var ephemeralSaved = RealmPolicy.LoginPosition(inEphemeralCopy, homeRealm: 0, realmExists: id => true, templateExit: id => null, out var ephemeralReason);
            Assert.IsNotNull(ephemeralReason, "an ephemeral copy never outlives a session");
            Assert.IsNull(ephemeralSaved.Instance);
            Assert.AreEqual(inEphemeralCopy.Cell, ephemeralSaved.Cell, "without its template, the same spot in the base world");

            var missingHome = RealmPolicy.LoginPosition(inBaseWorld, homeRealm: 7, realmExists: id => id == 5, templateExit: id => null, out var homeReason);
            Assert.IsNotNull(homeReason, "a home realm that isn't loaded is logged");
            Assert.IsNull(missingHome.Instance);
            Assert.AreEqual(inBaseWorld.Cell, missingHome.Cell);
        }

        [TestMethod]
        public void Login_InAnEphemeralCopy_GoesToTheTemplateExit_InTheHomeRealm()
        {
            var inEphemeralCopy = Spot(Position.InstanceIDFromVars(5, 1, true));
            var exit = new Position(0xA9B40019, 84, 7.1f, 94, 0, 0, -0.0784591f, 0.996917f);

            var login = RealmPolicy.LoginPosition(inEphemeralCopy, homeRealm: 0, realmExists: id => true, templateExit: id => id == 5 ? exit : null, out var reason);

            Assert.IsNotNull(reason, "the copy is gone, so the character is logged");
            Assert.IsNull(login.Instance, "home realm 0: the exit is in the base world");
            Assert.AreEqual(exit.Cell, login.Cell);
            Assert.AreEqual(exit.PositionX, login.PositionX);

            var home = RealmPolicy.LoginPosition(inEphemeralCopy, homeRealm: 3, realmExists: id => true, templateExit: id => id == 5 ? exit : null, out _);
            Assert.AreEqual(Position.InstanceIDFromVars(3, 0, false), home.Instance, "the exit is in the home realm");
        }

        [TestMethod]
        public void Recall_IntoTheHomeRealm_TakesItsPrimaryCopy_AndTheBaseWorldForRealmZero()
        {
            var lifestone = Spot(Position.InstanceIDFromVars(7, 0, false));

            var home = RealmPolicy.HomeRealmOf(5L, realmExists: id => id == 5);
            var recall = RealmPolicy.InRealm(lifestone, home);

            Assert.AreEqual((ushort)5, home);
            Assert.AreEqual(Position.InstanceIDFromVars(5, 0, false), recall.Instance, "the home realm's primary copy, not the copy the lifestone is in");
            Assert.AreEqual(lifestone.Cell, recall.Cell, "the same spot");

            Assert.IsNull(RealmPolicy.InRealm(lifestone, 0).Instance, "the base world is null, as a saved position has it");
            Assert.AreEqual((ushort)0, RealmPolicy.HomeRealmOf(5L, realmExists: id => false), "a home realm that isn't loaded is the base world");
            Assert.AreEqual((ushort)0, RealmPolicy.HomeRealmOf(null, realmExists: id => true), "no home realm set is the base world");
        }
    }
}
