using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Change price at the listing store: an active listing of the account's gets a new valid price in one save, and every other case is refused with nothing changed.
    /// A change that loses a race is re-read: still active is Changed, gone is NotActive. A purchase that read the old price is refused as price changed.
    /// Runs against a scratch shard built by the fresh-install path, with the real listing rule creating the listings it reprices.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class ListingStoreRepriceTests
    {
        private const string Db = "ace_shard_market_reprice";
        private const string AuthDb = "ace_auth_market_reprice";

        private const uint SellerAccountId = 41;
        private const uint OtherAccountId = 42;

        private static uint nextGuid = 0x80002000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;

            // the purchase reads the accounts' bans from the auth database
            MarketTestDatabase.CreateAuth(AuthDb);
            MarketTestDatabase.Execute(AuthDb, "INSERT INTO account (accountId, accountName, passwordHash, passwordSalt, accessLevel) VALUES (41, 'reprice-seller', 'x', 'x', 0), (42, 'reprice-other', 'x', 'x', 0), (43, 'reprice-buyer', 'x', 'x', 0);");
        }

        [ClassCleanup]
        public static void TestCleanup()
        {
            MarketTestDatabase.Drop(Db);
            MarketTestDatabase.Drop(AuthDb);
        }

        [TestMethod]
        public void Reprice_ActiveListing_ChangesThePriceInPlace_KeepingIdItemAndLifetime()
        {
            var (listingId, guid) = ListItem(SellerAccountId, 100);
            var before = LoadListing(listingId);

            var result = Reprice(SellerAccountId, listingId, 250);

            Assert.AreEqual(ListingOutcome.Ok, result.Outcome);
            var after = LoadListing(listingId);
            Assert.AreEqual(250, after.Price);
            Assert.AreEqual(ListingStatus.Active, after.Status);
            Assert.AreEqual(guid, after.ItemGuid);
            Assert.AreEqual(before.CreatedTime, after.CreatedTime, "the lifetime starts when the listing was made");
            Assert.AreEqual(VaultItemState.Listed, VaultState(guid), "the item stays listed in the seller's Vault");
        }

        [TestMethod]
        public void Reprice_AnotherAccountsListing_IsNotFound_AndChangesNothing()
        {
            var (listingId, _) = ListItem(SellerAccountId, 100);

            Assert.AreEqual(ListingOutcome.NotFound, Reprice(OtherAccountId, listingId, 250).Outcome);
            Assert.AreEqual(100, LoadListing(listingId).Price);
        }

        [TestMethod]
        public void Reprice_NoLongerActive_IsNotActive_AndChangesNothing()
        {
            var (listingId, _) = ListItem(SellerAccountId, 100);

            using (var shard = MarketTestDatabase.CreateContext(Db))
                Assert.AreEqual(ListingOutcome.Ok, ListingStore.Delist(shard, SellerAccountId, listingId, DateTime.UtcNow, () => false).Outcome);

            Assert.AreEqual(ListingOutcome.NotActive, Reprice(SellerAccountId, listingId, 250).Outcome);
            Assert.AreEqual(100, LoadListing(listingId).Price);
        }

        [TestMethod]
        public void Reprice_InvalidPrice_IsRefused_AndChangesNothing()
        {
            var (listingId, _) = ListItem(SellerAccountId, 100);

            Assert.AreEqual(ListingOutcome.InvalidPrice, Reprice(SellerAccountId, listingId, 0).Outcome);
            Assert.AreEqual(ListingOutcome.InvalidPrice, Reprice(SellerAccountId, listingId, -5).Outcome);
            Assert.AreEqual(100, LoadListing(listingId).Price);
        }

        [TestMethod]
        public void Reprice_BannedSeller_IsRefused_AndChangesNothing()
        {
            var (listingId, _) = ListItem(SellerAccountId, 100);

            using var shard = MarketTestDatabase.CreateContext(Db);
            Assert.AreEqual(ListingOutcome.Banned, ListingStore.Reprice(shard, SellerAccountId, listingId, 250, () => true).Outcome);
            Assert.AreEqual(100, LoadListing(listingId).Price);
        }

        [TestMethod]
        public void Reprice_LosesARaceToAnotherPriceChange_IsChanged_AndKeepsTheOtherPrice()
        {
            var (listingId, _) = ListItem(SellerAccountId, 100);

            // the other change saves between this one's read and its save
            ListingResult result;
            using (var shard = MarketTestDatabase.CreateContext(Db))
                result = ListingStore.Reprice(shard, SellerAccountId, listingId, 250, () =>
                {
                    Assert.AreEqual(ListingOutcome.Ok, Reprice(SellerAccountId, listingId, 300).Outcome);
                    return false;
                });

            Assert.AreEqual(ListingOutcome.Changed, result.Outcome);
            Assert.AreEqual(300, LoadListing(listingId).Price, "the other change's price stands");
        }

        [TestMethod]
        public void Reprice_LosesARaceToADelisting_IsNotActive()
        {
            var (listingId, _) = ListItem(SellerAccountId, 100);

            ListingResult result;
            using (var shard = MarketTestDatabase.CreateContext(Db))
                result = ListingStore.Reprice(shard, SellerAccountId, listingId, 250, () =>
                {
                    using var delisting = MarketTestDatabase.CreateContext(Db);
                    Assert.AreEqual(ListingOutcome.Ok, ListingStore.Delist(delisting, SellerAccountId, listingId, DateTime.UtcNow, () => false).Outcome);
                    return false;
                });

            Assert.AreEqual(ListingOutcome.NotActive, result.Outcome);
            Assert.AreEqual(ListingStatus.Delisted, LoadListing(listingId).Status);
        }

        [TestMethod]
        public void Delist_LosesARaceToAReprice_IsChanged_AndTheListingStaysActiveAtTheNewPrice()
        {
            var (listingId, guid) = ListItem(SellerAccountId, 100);

            // the reprice saves between this delisting's read and its save
            ListingResult result;
            using (var shard = MarketTestDatabase.CreateContext(Db))
                result = ListingStore.Delist(shard, SellerAccountId, listingId, DateTime.UtcNow, () =>
                {
                    Assert.AreEqual(ListingOutcome.Ok, Reprice(SellerAccountId, listingId, 300).Outcome);
                    return false;
                });

            Assert.AreEqual(ListingOutcome.Changed, result.Outcome);
            var listing = LoadListing(listingId);
            Assert.AreEqual(ListingStatus.Active, listing.Status);
            Assert.AreEqual(300, listing.Price);
            Assert.AreEqual(VaultItemState.Listed, VaultState(guid), "the item is still listed");
        }

        [TestMethod]
        public void Purchase_AtTheOldPrice_AfterARepriceSavedMeanwhile_IsRefusedAsPriceChanged_AndNothingMoves()
        {
            var (listingId, guid) = ListItem(SellerAccountId, 120);
            var buyer = 43u;
            SeedCharacter(buyer, BuyerCharacterId);
            MarketTestDatabase.Execute(Db, $"INSERT INTO market_balance (account_Id, balance) VALUES ({buyer}, 500);");

            // the fee policy is asked after the purchase has read the listing at 120 and before it saves: the seller reprices in that gap, once
            var fee = new RepriceOnceFeePolicy(() => Assert.AreEqual(ListingOutcome.Ok, Reprice(SellerAccountId, listingId, 150).Outcome));
            var database = new MarketDatabase(ConfigManager.Config.MySql, AuthDb, Db);

            var result = PurchaseRules.Buy(database, new DatabaseMarketPause(database), fee, listingId, buyer, null, 120, 5, "reprice-race", DateTime.UtcNow);

            Assert.AreEqual(BuyOutcome.PriceChanged, result.Outcome, $"the purchase was refused with {result.Outcome} before its fee was quoted");
            Assert.AreEqual(150L, result.Price);
            Assert.IsTrue(fee.Repriced, "the reprice happened inside the purchase");
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_transfer;"), "no sale");
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_ledger_entry;"), "no ledger rows");
            Assert.AreEqual(500, MarketTestDatabase.Scalar(Db, $"SELECT balance FROM market_balance WHERE account_Id = {buyer};"), "nothing charged");
            Assert.AreEqual(ListingStatus.Active, LoadListing(listingId).Status, "not sold");
            Assert.AreEqual(VaultItemState.Listed, VaultState(guid));
        }

        private const uint BuyerCharacterId = 0x50000043;

        /// <summary>
        /// A fee policy that runs a hook once, when the purchase quotes its fee, and charges nothing
        /// </summary>
        private sealed class RepriceOnceFeePolicy : IFeePolicy
        {
            private readonly Action hook;

            public RepriceOnceFeePolicy(Action hook)
            {
                this.hook = hook;
            }

            public bool Repriced { get; private set; }

            public SellerFee Quote(Sale sale)
            {
                if (!Repriced)
                {
                    Repriced = true;
                    hook();
                }

                return new SellerFee(0);
            }
        }

        private static void SeedCharacter(uint accountId, uint id)
        {
            MarketTestDatabase.Execute(Db, $"INSERT INTO biota (id, weenie_Class_Id, weenie_Type, populated_Collection_Flags) VALUES ({id}, 1, {(int)WeenieType.Creature}, {(uint)ShardDatabase.PopulatedCollectionFlags.BiotaPropertiesPosition});");
            MarketTestDatabase.Execute(Db, $"INSERT INTO biota_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES ({id}, {(int)PositionType.Location}, 0x7D64000D, 0, 0, 0, 1, 0, 0, 0);");
            MarketTestDatabase.Execute(Db, $"INSERT INTO `character` (id, account_Id, name, is_Plussed, is_Deleted, delete_Time, last_Login_Timestamp, total_Logins, character_Options_1, character_Options_2, spellbook_Filters, hair_Texture, default_Hair_Texture) VALUES ({id}, {accountId}, 'Reprice{id:X}', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);");
        }

        /// <summary>
        /// Puts an item in the seller's Vault and lists it at the price, through the listing store
        /// </summary>
        private static (long ListingId, uint Guid) ListItem(uint accountId, long price)
        {
            var guid = nextGuid++;
            MarketTestDatabase.Execute(Db, $"INSERT INTO biota (id, weenie_Class_Id, weenie_Type) VALUES ({guid}, 20630, 51);");

            using (var shard = MarketTestDatabase.CreateContext(Db))
            {
                shard.MarketVaultItems.Add(NewVaultItem(guid, accountId));
                shard.SaveChanges();
            }

            using var listing = MarketTestDatabase.CreateContext(Db);
            var result = ListingStore.List(listing, accountId, guid, price, null, DateTime.UtcNow, () => false);
            Assert.AreEqual(ListingOutcome.Ok, result.Outcome);

            return (result.Listing.Id, guid);
        }

        private static ListingResult Reprice(uint accountId, long listingId, long price)
        {
            using var shard = MarketTestDatabase.CreateContext(Db);
            return ListingStore.Reprice(shard, accountId, listingId, price, () => false);
        }

        private static Listing LoadListing(long listingId)
        {
            using var shard = MarketTestDatabase.CreateContext(Db);
            return shard.MarketListings.AsNoTracking().Single(l => l.Id == listingId);
        }

        private static string VaultState(uint guid) => MarketTestDatabase.Rows(Db, $"SELECT state FROM market_vault_item WHERE item_Guid = {guid};").Single();

        private static VaultItem NewVaultItem(uint guid, uint accountId)
        {
            return new VaultItem
            {
                ItemGuid = guid,
                AccountId = accountId,
                CharacterId = 0x50000001,
                State = VaultItemState.Held,
                DepositedTime = DateTime.UtcNow,
                Wcid = 20630,
                Name = "Trade Note",
                ItemType = 0x2000,
                StackSize = 5,
                Value = 250000,
                Icon = 0x06001234,
                DamageMod = 1.65,
            };
        }
    }
}
