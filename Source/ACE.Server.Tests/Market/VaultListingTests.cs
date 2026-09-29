using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
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
        public void List_WhileTheGameMarksTheItemWithdrawing_OnlyOneWins()
        {
            var (_, guid) = DepositedItem();
            var row = VaultStore.Get(guid);

            // the channel marked it first; a listing that read the row before then must not overwrite the mark
            Assert.IsNotNull(VaultStore.TryMarkWithdrawing(guid, row.AccountId, row.RowVersion));

            using (var shard = new ShardDbContext())
                Assert.AreEqual(ListingOutcome.NotHeld, ListingStore.List(shard, row.AccountId, guid, 25, null, DateTime.UtcNow, () => false).Outcome);

            Assert.AreEqual(VaultItemState.Withdrawing, VaultStore.Get(guid).State);
        }
    }
}
