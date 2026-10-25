using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Network;

namespace ACE.Server.Tests.Network
{
    [TestClass]
    public class DatGateTests
    {
        [TestMethod]
        public void NewerDatsAreKickedAtConnectWhateverTheSettings()
        {
            Assert.AreEqual(DatVerdict.KickNewer, DatGate.Decide(false, true, datPatching: false, refuseOlder: true));
            Assert.AreEqual(DatVerdict.KickNewer, DatGate.Decide(true, true, datPatching: true, refuseOlder: false));
        }

        [TestMethod]
        public void OlderDatsWithoutPatchingReachCharacterSelectAndAreRefusedOrWarnedAtEnter()
        {
            Assert.AreEqual(DatVerdict.RefuseAtEnter, DatGate.Decide(true, false, datPatching: false, refuseOlder: true));
            Assert.AreEqual(DatVerdict.WarnAtEnter, DatGate.Decide(true, false, datPatching: false, refuseOlder: false));
        }

        [TestMethod]
        public void OlderDatsWithPatchingArePatchedAndMatchingDatsGetIn()
        {
            Assert.AreEqual(DatVerdict.Patch, DatGate.Decide(true, false, datPatching: true, refuseOlder: true));
            Assert.AreEqual(DatVerdict.LetIn, DatGate.Decide(false, false, datPatching: false, refuseOlder: true));
        }

        [TestMethod]
        public void AReportSentAgainWithTheServersEditionLetsARefusedClientIn()
        {
            var fresh = DatGate.Decide(false, false, datPatching: false, refuseOlder: true);
            Assert.AreEqual(DatVerdict.LetIn, DatGate.Recheck(fresh, DatVerdict.RefuseAtEnter));
            // DDD is never started by a repeat report.
            Assert.AreEqual(DatVerdict.RefuseAtEnter, DatGate.Recheck(DatVerdict.Patch, DatVerdict.RefuseAtEnter));
        }
    }
}
