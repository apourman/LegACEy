using System;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Realms;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Per-realm housing, on a real cottage landblock in the Vault's started world: a realm's primary copy has its own houses.
    /// </summary>
    public partial class VaultTests
    {
        // cottages west of Holtburg, each with a slumlord, a storage chest and hooks
        private static readonly LandblockId Cottages = new LandblockId(0xA867FFFF);

        private const uint Cottage = 0x7A86709A; // housecottage452

        private const uint NextCottage = 0x7A86709B; // housecottage453

        private static readonly uint OtherRealmCopy = Position.InstanceIDFromVars(2, 0, false);

        [TestMethod]
        public void AHouseBoughtInARealm_ReloadsOwnedThereAndNowhereElse()
        {
            using var cleanup = new UnloadCopiesAfterwards();

            var owner = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var guest = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            using var ownerOnline = VaultTestWorld.Online(owner);
            using var guestOnline = VaultTestWorld.Online(guest);

            var house = HouseIn(LoadCopy(Cottages, RealmCopy), Cottage);
            Buy(owner, house);

            Assert.AreEqual(new ObjectGuid(Cottage, RealmCopy).Full, owner.HouseInstance, "the owner's house is the realm's copy");

            var stored = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            VaultTestWorld.OnWorldThread(() =>
            {
                house.AddGuest(guest, true);
                Assert.IsTrue(house.Storage.Single().TryAddToInventory(stored), "the storage chest takes the item");
            });

            // the restart: every copy unloads, saving what changed, and loads again from the database
            UnloadAndSave(cleanup);

            var realmHouse = HouseIn(LoadCopy(Cottages, RealmCopy), Cottage);
            var baseHouse = HouseIn(LoadCopy(Cottages, 0), Cottage);
            var otherRealmHouse = HouseIn(LoadCopy(Cottages, OtherRealmCopy), Cottage);

            Assert.AreEqual(owner.Guid.ClientGUID, realmHouse.HouseOwner, "owned in the realm it was bought in");
            Assert.AreEqual(owner.Guid.ClientGUID, realmHouse.SlumLord.HouseOwner, "the slumlord is linked to its own copy's house");
            Assert.IsTrue(realmHouse.Guests.TryGetValue(guest.Guid, out var storageAccess) && storageAccess, "the guest keeps storage access");

            var storage = realmHouse.Storage.Single();
            VaultTestWorld.WaitUntil(() => storage.InventoryLoaded, "the storage chest to load its contents");
            Assert.IsTrue(storage.Inventory.ContainsKey(stored.Guid), "the stored item is in the realm's storage chest");

            foreach (var unowned in new[] { baseHouse, otherRealmHouse })
            {
                Assert.IsNull(unowned.HouseOwner, $"copy 0x{unowned.Guid.Instance:X8} isn't owned");
                Assert.IsNull(unowned.SlumLord.HouseOwner);
                Assert.AreEqual(0, unowned.Guests.Count);
                VaultTestWorld.WaitUntil(() => unowned.Storage.Single().InventoryLoaded, "the storage chest to load its contents");
                Assert.AreEqual(0, unowned.Storage.Single().Inventory.Count, "another copy's storage chest is empty");
            }

            // the offline copy that rent and recall use, and the rent queue a restart rebuilds
            var offline = House.Load(owner.HouseInstance.Value);
            Assert.AreEqual(owner.Guid.ClientGUID, offline.HouseOwner);
            Assert.AreEqual(RealmCopy, offline.SlumLord.Location.Instance, "house recall goes to the realm's copy");

            VaultTestWorld.OnWorldThread(HouseManager.BuildRentQueue);
            Assert.AreEqual(owner.HouseInstance, HouseManager.GetCharacterHouses(owner.Guid.ClientGUID).Single().Guid.Full, "the rent queue has the realm's house");

            VaultTestWorld.OnWorldThread(() => HouseManager.RemoveRentQueue(offline.Guid));
        }

        [TestMethod]
        public void HouseLimit_CountsTheAccountsHousesInEveryRealm()
        {
            using var cleanup = new UnloadCopiesAfterwards();

            var account = VaultTestWorld.NewAccountId();
            var inRealm = VaultTestWorld.NewPlayer(account);
            var inBaseWorld = VaultTestWorld.NewPlayer(account);
            using var realmOnline = VaultTestWorld.Online(inRealm);
            using var baseOnline = VaultTestWorld.Online(inBaseWorld);

            var realmHouse = HouseIn(LoadCopy(Cottages, RealmCopy), Cottage);
            Buy(inRealm, realmHouse);

            Assert.IsTrue(RealmPolicy.HouseLimitReached(inBaseWorld), "a house in another realm counts against the account");

            var baseHouse = HouseIn(LoadCopy(Cottages, 0), NextCottage);
            Buy(inBaseWorld, baseHouse);

            Assert.AreEqual(2, RealmPolicy.HousesCountedFor(inBaseWorld).Count, "both realms' houses count");

            PropertyManager.ModifyBool("house_per_char", true);
            try
            {
                Assert.AreEqual(1, RealmPolicy.HousesCountedFor(inBaseWorld).Count, "per character, only its own");
            }
            finally
            {
                PropertyManager.ModifyBool("house_per_char", false);
            }

            VaultTestWorld.OnWorldThread(() =>
            {
                HouseManager.RemoveRentQueue(realmHouse.Guid);
                HouseManager.RemoveRentQueue(baseHouse.Guid);
            });
        }

        private static House HouseIn(Landblock landblock, uint houseGuid)
        {
            House house = null;
            VaultTestWorld.OnWorldThread(() => house = landblock.GetObject(houseGuid) as House);

            Assert.IsNotNull(house, $"the copy has house 0x{houseGuid:X8}");
            Assert.AreEqual(landblock.Instance, house.Guid.Instance);

            return house;
        }

        /// <summary>
        /// The purchase, from the point the slumlord has taken the payment: the player stands at the slumlord and becomes the owner
        /// </summary>
        private static void Buy(Player player, House house)
        {
            var landblock = house.CurrentLandblock;
            player.Location = new Position(house.SlumLord.Location);

            VaultTestWorld.OnWorldThread(() =>
            {
                Assert.IsTrue(landblock.AddWorldObject(player));
                player.SetHouseOwner(house.SlumLord);
            });

            // the purchase adds the house to the rent queue a few seconds later
            VaultTestWorld.WaitUntil(() => HouseManager.GetCharacterHouses(player.Guid.ClientGUID).Any(h => h.Guid == house.Guid), "the house to join the rent queue");

            VaultTestWorld.OnWorldThread(() => landblock.RemoveWorldObject(player.Guid));
        }

        /// <summary>
        /// Unloads the copies the test loaded and waits for the saves the unloads queued
        /// </summary>
        private static void UnloadAndSave(UnloadCopiesAfterwards loaded)
        {
            loaded.UnloadAdded();

            var saved = new ManualResetEventSlim();
            DatabaseManager.Shard.GetCurrentQueueWaitTime(_ => saved.Set());
            Assert.IsTrue(saved.Wait(TimeSpan.FromSeconds(30)), "the database thread saved the unloaded copies");
        }
    }
}
