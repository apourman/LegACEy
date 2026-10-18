using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Realms;

namespace ACE.Server.Tests.Realms
{
    [TestClass]
    public class EphemeralInstanceTests
    {
        [TestMethod]
        public void Allocate_IsSequentialPerRealm()
        {
            var ids = new ShortInstanceAllocator();

            CollectionAssert.AreEqual(new ushort[] { 1, 2, 3 }, new[] { ids.Allocate(2).Value, ids.Allocate(2).Value, ids.Allocate(2).Value });
            Assert.AreEqual((ushort)1, ids.Allocate(3).Value, "another realm starts at 1 too");
        }

        [TestMethod]
        public void Allocate_NeverHandsOutAHeldId_AndFailsCleanlyWhenAllAreHeld()
        {
            var ids = new ShortInstanceAllocator();

            var held = new HashSet<ushort>();
            for (var i = 0; i < ShortInstanceAllocator.Last; i++)
                Assert.IsTrue(held.Add(ids.Allocate(2).Value), "every id is handed out once");

            Assert.AreEqual(ShortInstanceAllocator.Last, held.Count);
            Assert.IsNull(ids.Allocate(2), "all 65535 are held: no id, and no exception");
        }

        [TestMethod]
        public void Release_MakesAnIdAvailableAgain_AfterTheAllocatorWraps()
        {
            var ids = new ShortInstanceAllocator();
            for (var i = 0; i < ShortInstanceAllocator.Last; i++)
                ids.Allocate(2);

            ids.Release(2, 7);

            Assert.AreEqual((ushort)7, ids.Allocate(2).Value, "the one free id is found by wrapping round");
            Assert.IsNull(ids.Allocate(2));
        }

        [TestMethod]
        public void Cleanup_DestroysAnOccupiedCopy_OnceItsTtlEnds()
        {
            var created = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            var ttl = TimeSpan.FromMinutes(30);

            Assert.IsFalse(EphemeralInstances.ShouldDestroy(created.AddMinutes(29), created, ttl, emptySince: null, grace: TimeSpan.FromMinutes(2)));
            Assert.IsTrue(EphemeralInstances.ShouldDestroy(created.AddMinutes(30), created, ttl, emptySince: null, grace: TimeSpan.FromMinutes(2)));
        }

        [TestMethod]
        public void Cleanup_DestroysACopyEmptyPastItsGrace_AndKeepsItWithinIt()
        {
            var created = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            var emptySince = created.AddMinutes(10);
            var grace = TimeSpan.FromMinutes(2);
            var ttl = TimeSpan.FromMinutes(30);

            Assert.IsFalse(EphemeralInstances.ShouldDestroy(emptySince.AddMinutes(1), created, ttl, emptySince, grace), "empty, but within the grace period");
            Assert.IsTrue(EphemeralInstances.ShouldDestroy(emptySince.AddMinutes(2), created, ttl, emptySince, grace), "empty past the grace period");
        }

        [TestMethod]
        public void Cleanup_KeepsAnOccupiedCopy_WithinItsTtl()
        {
            var created = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsFalse(EphemeralInstances.ShouldDestroy(created.AddMinutes(5), created, TimeSpan.FromMinutes(30), emptySince: null, grace: TimeSpan.FromMinutes(2)));
        }
    }
}
