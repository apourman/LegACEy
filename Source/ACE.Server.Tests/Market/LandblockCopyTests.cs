using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Landblock copies, in the Vault's test world (its lifecycle is per class, so these share the Vault class): real landblocks loaded
    /// from the DATs and the world database, in the base world and in other instances.
    /// </summary>
    public partial class VaultTests
    {
        // Holtburg: NPCs, doors and generators
        private static readonly LandblockId Holtburg = new LandblockId(0xA9B4FFFF);

        private static readonly uint RealmCopy = Position.InstanceIDFromVars(1, 0, false);

        private static readonly uint EphemeralCopy = Position.InstanceIDFromVars(1, 7, true);

        [TestMethod]
        public void TwoCopiesOfALandblock_HaveTheirOwnStatics()
        {
            using var cleanup = new UnloadCopiesAfterwards();

            var baseWorld = LoadCopy(0);
            var copy = LoadCopy(RealmCopy);

            Assert.AreNotSame(baseWorld, copy);
            Assert.AreNotSame(baseWorld.PhysicsLandblock, copy.PhysicsLandblock, "each copy has its own physics landblock");

            // the DAT scenery (buildings' plinths, trees, statics) collides only in its own copy, so content placed on it stays put
            var scenery = copy.PhysicsLandblock.StaticObjects.Concat(copy.PhysicsLandblock.Scenery).ToList();
            Assert.IsTrue(scenery.Count > 0, "Holtburg has DAT scenery");
            Assert.IsTrue(scenery.All(obj => obj.Instance == RealmCopy), "the copy's scenery carries its instance");
            Assert.IsTrue(scenery.SelectMany(obj => obj.ShadowObjects.Values).All(shadow => shadow.Cell.Instance == RealmCopy), "its shadows are in the copy's cells");

            var baseStatics = Statics(baseWorld);
            var copyStatics = Statics(copy).ToDictionary(wo => wo.Guid.ClientGUID);

            Assert.IsTrue(baseStatics.Count > 0, "Holtburg has statics");
            Assert.AreEqual(baseStatics.Count, copyStatics.Count, "the copy has the same content");

            foreach (var original in baseStatics)
            {
                var twin = copyStatics[original.Guid.ClientGUID];

                Assert.AreNotSame(original, twin);
                Assert.AreEqual(0u, original.Guid.Instance);
                Assert.AreEqual(RealmCopy, twin.Guid.Instance, "the copy's static carries its instance");
                Assert.AreEqual(RealmCopy, twin.Location.Instance);
                Assert.AreSame(twin, copy.GetObject(twin.Guid.ClientGUID), "a client guid resolves in the sender's copy");

                if (original.PhysicsObj?.CurCell == null)
                    continue;

                Assert.AreNotSame(original.PhysicsObj, twin.PhysicsObj);
                Assert.AreNotSame(original.PhysicsObj.CurCell, twin.PhysicsObj.CurCell, "the copies don't share cells or their object lists");
                Assert.AreEqual(RealmCopy, twin.PhysicsObj.CurCell.Instance);
            }

            // using a door in one copy leaves the other copy's door alone
            var door = baseStatics.OfType<Door>().First(d => !d.IsOpen);
            var twinDoor = (Door)copyStatics[door.Guid.ClientGUID];

            VaultTestWorld.OnWorldThread(() => twinDoor.Open());

            Assert.IsTrue(twinDoor.IsOpen);
            Assert.IsFalse(door.IsOpen, "the base world's door is still shut");

            // a player teleported to the same spot in the other copy moves into the copy, physics cell included (@teleinstance with no location)
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            VaultTestWorld.OnWorldThread(() => Assert.IsTrue(baseWorld.AddWorldObject(player)));
            Assert.AreEqual(0u, player.PhysicsObj.CurCell.Instance);

            VaultTestWorld.OnWorldThread(() => player.Teleport(new Position(player.Location) { Instance = RealmCopy }));

            Assert.AreSame(copy, player.CurrentLandblock);
            Assert.AreEqual(RealmCopy, player.Location.Instance);
            Assert.AreEqual(RealmCopy, player.PhysicsObj.Instance);
            Assert.AreEqual(RealmCopy, player.PhysicsObj.CurCell.Instance, "the physics object is in the copy's cell");
            Assert.AreSame(copy.PhysicsLandblock, player.PhysicsObj.CurLandblock);

            VaultTestWorld.OnWorldThread(() => copy.RemoveWorldObject(player.Guid));
        }

        [TestMethod]
        public void ACopysObjects_GoToTheClientWithClientGuidsOnly()
        {
            using var cleanup = new UnloadCopiesAfterwards();

            var copy = LoadCopy(RealmCopy);

            List<WorldObject> objects = null;
            VaultTestWorld.OnWorldThread(() => objects = copy.GetWorldObjectsForPhysicsHandling().ToList());

            // a 64-bit guid written whole, as Write(ulong) does, is the client guid followed by the copy's instance
            var fullGuids = objects.Where(wo => wo.Guid.Instance != 0).Select(wo => System.BitConverter.GetBytes(wo.Guid.Full)).ToList();
            Assert.IsTrue(fullGuids.Count > 0);

            var leaks = objects
                .Select(wo => (wo, bytes: CreateObjectBytes(wo)))
                .Where(m => fullGuids.Any(guid => m.bytes.AsSpan().IndexOf(guid) >= 0))
                .Select(m => $"{m.wo.Guid} {m.wo.WeenieClassName} ({m.wo.WeenieType})")
                .ToList();

            Assert.AreEqual(0, leaks.Count, string.Join("\n", leaks));
        }

        private static byte[] CreateObjectBytes(WorldObject wo) => new ACE.Server.Network.GameMessages.Messages.GameMessageCreateObject(wo).Data.ToArray();

        [TestMethod]
        public void EphemeralCopy_SavesNothing_AndUnloadDestroysAllButPlayers()
        {
            using var cleanup = new UnloadCopiesAfterwards();

            var ephemeral = LoadCopy(EphemeralCopy);
            var persistent = LoadCopy(RealmCopy);

            var dropped = Drop(ephemeral);
            var control = Drop(persistent);

            VaultTestWorld.OnWorldThread(() =>
            {
                // the periodic save, once the items are in (not just pending)
                foreach (var landblock in new[] { ephemeral, persistent })
                {
                    landblock.GetWorldObjectsForPhysicsHandling();
                    landblock.SaveDB();
                }
            });

            Assert.IsTrue(control.BiotaOriginatedFromOrHasBeenSavedToDatabase(), "a persistent copy saves a dropped item");
            Assert.IsFalse(dropped.BiotaOriginatedFromOrHasBeenSavedToDatabase(), "an ephemeral copy saves nothing");

            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            player.Location.Instance = EphemeralCopy;

            List<WorldObject> contents = null;
            VaultTestWorld.OnWorldThread(() =>
            {
                Assert.IsTrue(ephemeral.AddWorldObject(player));
                contents = ephemeral.GetWorldObjectsForPhysicsHandling().Where(wo => wo != player).ToList();
            });

            Assert.IsTrue(contents.Any(wo => wo.Guid.IsStatic()), "the copy has statics to destroy");

            Unload(ephemeral);

            Assert.IsTrue(contents.All(wo => wo.IsDestroyed), "every object but players is destroyed");
            Assert.IsFalse(player.IsDestroyed, "a player is only removed");
            Assert.IsTrue(LandblockManager.IsLoaded(Holtburg, RealmCopy), "the other copy stays loaded");
        }

        /// <summary>
        /// Unloads every landblock the test loaded (copies, and the adjacents a teleport loads), so the shared world is left as it was:
        /// a loaded landblock keeps saving on the world thread, which races the database stopping at class cleanup
        /// </summary>
        private sealed class UnloadCopiesAfterwards : System.IDisposable
        {
            private readonly HashSet<Landblock> before = new HashSet<Landblock>(LandblockManager.GetLoadedLandblocks());

            public void Dispose() => UnloadAdded();

            /// <summary>
            /// Unloads the landblocks loaded since this was made
            /// </summary>
            public void UnloadAdded()
            {
                foreach (var landblock in LandblockManager.GetLoadedLandblocks().Where(lb => !before.Contains(lb)))
                    Unload(landblock);
            }
        }

        private static void Unload(Landblock landblock)
        {
            VaultTestWorld.OnWorldThread(() => LandblockManager.AddToDestructionQueue(landblock));
            VaultTestWorld.WaitUntil(() => !LandblockManager.IsLoaded(landblock.Id, landblock.Instance), $"landblock 0x{landblock.Id.Landblock:X4} copy 0x{landblock.Instance:X8} to unload");
        }

        private static Landblock LoadCopy(uint instance) => LoadCopy(Holtburg, instance);

        private static Landblock LoadCopy(LandblockId id, uint instance)
        {
            Landblock landblock = null;
            VaultTestWorld.OnWorldThread(() => landblock = LandblockManager.GetLandblock(id, instance, false));

            Assert.AreEqual(instance, landblock.Instance);
            VaultTestWorld.WaitUntil(() => landblock.CreateWorldObjectsCompleted, $"copy 0x{instance:X8} to create its objects");

            return landblock;
        }

        private static List<WorldObject> Statics(Landblock landblock)
        {
            List<WorldObject> statics = null;
            VaultTestWorld.OnWorldThread(() => statics = landblock.GetWorldObjectsForPhysicsHandling().Where(wo => wo.Guid.IsStatic()).ToList());
            return statics;
        }

        /// <summary>
        /// A new item on the ground in the copy, as if a player had dropped it
        /// </summary>
        private static WorldObject Drop(Landblock landblock)
        {
            var item = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            item.Location = new Position(0xA9B4001F, 84.0f, 7.1f, 94.0f, 0.0f, 0.0f, 0.0f, 1.0f) { Instance = landblock.PositionInstance };

            VaultTestWorld.OnWorldThread(() => Assert.IsTrue(landblock.AddWorldObject(item), "the item lands in the copy"));

            return item;
        }
    }
}
