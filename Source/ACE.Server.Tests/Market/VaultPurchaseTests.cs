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
    /// Seam 2: a bought item is the buyer's to withdraw in game, on any of the buyer's characters. The sale is the same store the Market API's purchase saves through.
    /// </summary>
    public partial class VaultTests
    {
        [TestMethod]
        public void Purchase_BoughtItem_IsWithdrawnInGameByAnyOfTheBuyersCharacters()
        {
            var (seller, guid) = DepositedItem();
            var sellerAccount = VaultStore.Get(guid).AccountId;

            ListingResult listed;
            using (var shard = new ShardDbContext())
                listed = ListingStore.List(shard, sellerAccount, guid, 25, null, DateTime.UtcNow, () => false);

            Assert.AreEqual(ListingOutcome.Ok, listed.Outcome);

            var buyerAccount = VaultTestWorld.NewAccountId();
            var buyingCharacter = VaultTestWorld.NewPlayer(buyerAccount);
            var otherCharacter = VaultTestWorld.NewPlayer(buyerAccount);
            MarketTestDatabase.Execute(Db, $"INSERT INTO market_balance (account_Id, balance) VALUES ({buyerAccount}, 100);");

            PurchaseResult bought;
            using (var shard = new ShardDbContext())
            {
                var listing = shard.MarketListings.Find(listed.Listing.Id);
                var item = shard.MarketVaultItems.Find(guid);

                bought = PurchaseStore.Complete(shard, listing, item, new Purchase(buyerAccount, buyingCharacter.Character.Id, "seam2-" + guid, 0), DateTime.UtcNow, _ => false);
            }

            Assert.AreEqual(PurchaseOutcome.Ok, bought.Outcome);
            Assert.AreEqual(75L, bought.Receipt.Balance);
            Assert.AreEqual(buyingCharacter.Character.Id, VaultStore.Get(guid).CharacterId);

            // the seller no longer has it
            var refused = VaultTestWorld.Withdraw(seller, guid);
            Assert.AreEqual(VaultOutcome.NotInVault, refused.Outcome, refused.Message);
            Assert.IsNull(seller.GetInventoryItem(guid));

            // a character of the buyer's other than the one the purchase named takes it out
            var withdrawn = VaultTestWorld.Withdraw(otherCharacter, guid);
            Assert.AreEqual(VaultOutcome.Withdrawn, withdrawn.Outcome, withdrawn.Message);
            Assert.IsNotNull(otherCharacter.GetInventoryItem(guid));
            Assert.IsNull(VaultStore.Get(guid));
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {guid} AND kind = 'withdraw' AND account_Id = {buyerAccount} AND character_Id = {otherCharacter.Character.Id};"));
        }
    }
}
