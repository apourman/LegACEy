using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Server.Market;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: a listed item can't leave the Vault in game, and a delisted one can. Listing and delisting use the same store as the Market API.
    /// </summary>
    public partial class VaultTests
    {
        [TestMethod]
        public void Delist_ThenWithdrawInGame_Succeeds()
        {
            var (player, guid) = DepositedItem();
            var account = VaultStore.Get(guid).AccountId;

            ListingResult listed;
            using (var shard = new ShardDbContext())
                listed = ListingStore.List(shard, account, guid, 25, null, DateTime.UtcNow, () => false);

            Assert.AreEqual(ListingOutcome.Ok, listed.Outcome);
            Assert.AreEqual(VaultItemState.Listed, VaultStore.Get(guid).State);

            var refused = VaultTestWorld.Withdraw(player, guid);
            Assert.AreEqual(VaultOutcome.Listed, refused.Outcome, refused.Message);
            Assert.IsNull(player.GetInventoryItem(guid));

            ListingResult delisted;
            using (var shard = new ShardDbContext())
                delisted = ListingStore.Delist(shard, account, listed.Listing.Id, DateTime.UtcNow, () => false);

            Assert.AreEqual(ListingOutcome.Ok, delisted.Outcome);
            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(guid).State);

            var withdrawn = VaultTestWorld.Withdraw(player, guid);
            Assert.AreEqual(VaultOutcome.Withdrawn, withdrawn.Outcome, withdrawn.Message);
            Assert.IsNotNull(player.GetInventoryItem(guid));
            Assert.IsNull(VaultStore.Get(guid));
        }

        [TestMethod]
        public void List_ItemTheGameMarkedWithdrawing_IsRefused()
        {
            var (_, guid) = DepositedItem();
            var row = VaultStore.Get(guid);

            // the withdrawal channel marked it; listing must not overwrite the mark
            Assert.IsNotNull(VaultStore.TryMarkWithdrawing(guid, row.AccountId, row.RowVersion));

            using (var shard = new ShardDbContext())
                Assert.AreEqual(ListingOutcome.NotHeld, ListingStore.List(shard, row.AccountId, guid, 25, null, DateTime.UtcNow, () => false).Outcome);

            Assert.AreEqual(VaultItemState.Withdrawing, VaultStore.Get(guid).State);
        }

        [TestMethod]
        public void List_LosesTheRaceToAWithdrawalMark_WritesNothing()
        {
            var (_, guid) = DepositedItem();
            var row = VaultStore.Get(guid);

            // the ban check runs after the row was read and just before the save: the game marks the row in between
            ListingResult result;
            using (var shard = new ShardDbContext())
                result = ListingStore.List(shard, row.AccountId, guid, 25, null, DateTime.UtcNow, () =>
                {
                    Assert.IsNotNull(VaultStore.TryMarkWithdrawing(guid, row.AccountId, row.RowVersion));
                    return false;
                });

            Assert.AreEqual(ListingOutcome.NotHeld, result.Outcome);
            Assert.AreEqual(VaultItemState.Withdrawing, VaultStore.Get(guid).State);
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_listing WHERE item_Guid = {guid};"));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {guid} AND kind = 'list';"));
        }

        [TestMethod]
        public void Delist_LosesTheRaceToAnotherChange_WritesNothing()
        {
            var (_, guid) = DepositedItem();
            var account = VaultStore.Get(guid).AccountId;

            ListingResult listed;
            using (var shard = new ShardDbContext())
                listed = ListingStore.List(shard, account, guid, 25, null, DateTime.UtcNow, () => false);

            // an expiry (or a sale) closes the listing between the delist's read and its save
            ListingResult result;
            using (var shard = new ShardDbContext())
                result = ListingStore.Delist(shard, account, listed.Listing.Id, DateTime.UtcNow, () =>
                {
                    MarketTestDatabase.Execute(Db, $"UPDATE market_listing SET status = 'expired', row_Version = row_Version + 1 WHERE id = {listed.Listing.Id};");
                    return false;
                });

            Assert.AreEqual(ListingOutcome.NotActive, result.Outcome);
            Assert.AreEqual(VaultItemState.Listed, VaultStore.Get(guid).State);
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {guid} AND kind = 'delist';"));
        }
    }
}
