using System;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Server.ClientChannel;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ClientChannel.ChannelTestClient;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// The Market window's buy actions, sent as the window sends them, in the Vault's test world. A purchase runs the shared purchase rules, then
    /// delivers to the pack as the Vault's batch withdrawal does. Every refusal moves nothing: both balances and the item stay as they were.
    /// </summary>
    public partial class VaultTests
    {
        [TestMethod]
        public void MarketBuy_NeedsTheMarketStation_AndIsRefusedWhileMarketEnabledIsOff()
        {
            var sale = NewSale(300);
            var player = ConnectedPlayer();

            Assert.AreEqual(ChannelStatus.NoStation, Request(player, MarketBuyActions.Buy, BuyBody(sale.ListingId, 300, "station-1")).Status);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, MarketBuyActions.Fits, BitConverter.GetBytes(sale.ListingId)).Status);

            AtTheMarketStation(player);
            PropertyManager.ModifyBool(Vault.MarketEnabledKey, false);
            try
            {
                var refused = Request(player, MarketBuyActions.Buy, BuyBody(sale.ListingId, 300, "station-2"));
                Assert.AreEqual(ChannelStatus.Unavailable, refused.Status);
                Assert.AreEqual(VaultMessages.For(VaultOutcome.MarketClosed, null), Text(refused.Body));
            }
            finally
            {
                PropertyManager.ModifyBool(Vault.MarketEnabledKey, true);
            }

            Assert.AreEqual(VaultItemState.Listed, VaultStore.Get(sale.Guid).State, "nothing was bought");
        }

        [TestMethod]
        public void MarketBuy_ToThePack_PaysTheSeller_AndMovesTheItemIntoThePackAtOnce()
        {
            var sale = NewSale(300);
            var buyer = Buyer(1000);

            var fit = FitsReply(buyer, sale.ListingId);
            Assert.IsTrue(fit.Fits, "an empty pack has room");
            Assert.IsTrue(fit.FreeSlots > 0 && fit.FreeSlots <= fit.Slots, $"{fit.FreeSlots} of {fit.Slots} slots free");

            var bought = BuyReply(buyer, sale.ListingId, 300, "pack-1", ToPack: true);

            Assert.AreEqual(MarketBuyActions.Ok, bought.Code, bought.Message);
            Assert.IsTrue(bought.InPack, bought.Message);
            Assert.AreEqual(700L, bought.Balance);
            Assert.AreEqual("Buy sword is in your pack.", bought.Message);
            Assert.IsNotNull(buyer.GetInventoryItem(sale.Guid), "in the pack now");
            Assert.IsNull(VaultStore.Get(sale.Guid), "out of the Vault");
            Assert.AreEqual(700L, Ledger.GetBalance(buyer.Character.AccountId));
            Assert.AreEqual(300L, Ledger.GetBalance(sale.SellerAccount));
        }

        [TestMethod]
        public void MarketBuy_ToTheVault_LeavesTheItemInTheBuyersVault()
        {
            var sale = NewSale(250);
            var buyer = Buyer(1000);

            var bought = BuyReply(buyer, sale.ListingId, 250, "vault-1", ToPack: false);

            Assert.AreEqual(MarketBuyActions.Ok, bought.Code, bought.Message);
            Assert.IsFalse(bought.InPack);
            Assert.AreEqual("Buy sword is in your Vault.", bought.Message);
            var row = VaultStore.Get(sale.Guid);
            Assert.AreEqual(buyer.Character.AccountId, row.AccountId);
            Assert.AreEqual(VaultItemState.Held, row.State);
            Assert.IsNull(buyer.GetInventoryItem(sale.Guid));
            Assert.AreEqual(750L, Ledger.GetBalance(buyer.Character.AccountId));
        }

        [TestMethod]
        public void MarketBuy_WithAFullPack_StillBuys_AndLeavesTheItemInTheVault_AndSaysSo()
        {
            var sale = NewSale(300);
            var buyer = Buyer(1000);
            buyer.ItemCapacity = 0; // the pack has no room

            var fits = FitsReply(buyer, sale.ListingId);
            Assert.IsFalse(fits.Fits, "the window greys the pack out");
            Assert.AreEqual(VaultMessages.For(VaultOutcome.NoPackSpace, "Buy sword"), fits.Reason);

            var bought = BuyReply(buyer, sale.ListingId, 300, "full-1", ToPack: true);

            Assert.AreEqual(MarketBuyActions.Ok, bought.Code, "the purchase stands");
            Assert.IsFalse(bought.InPack);
            Assert.AreEqual($"{VaultMessages.For(VaultOutcome.NoPackSpace, "Buy sword")} Buy sword is in your Vault.", bought.Message);
            Assert.AreEqual(buyer.Character.AccountId, VaultStore.Get(sale.Guid).AccountId, "the buyer's, in the Vault");
            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(sale.Guid).State);
            Assert.IsNull(buyer.GetInventoryItem(sale.Guid));
            Assert.AreEqual(700L, Ledger.GetBalance(buyer.Character.AccountId));
        }

        [TestMethod]
        public void MarketBuy_TheSameKeyTwice_ChargesOnce_AndReturnsTheSameReceipt()
        {
            var sale = NewSale(300);
            var buyer = Buyer(1000);

            var first = BuyReply(buyer, sale.ListingId, 300, "repeat-1", ToPack: false);
            var again = BuyReply(buyer, sale.ListingId, 300, "repeat-1", ToPack: false);

            Assert.AreEqual(MarketBuyActions.Ok, first.Code, first.Message);
            Assert.AreEqual(first, again, "the same receipt");
            Assert.AreEqual(700L, Ledger.GetBalance(buyer.Character.AccountId), "charged once");
            Assert.AreEqual(300L, Ledger.GetBalance(sale.SellerAccount));
        }

        [TestMethod]
        public void MarketBuy_RepeatingAPackBuysKey_AnswersAsTheFirstTime_AndNeverMovesTheItemAgain()
        {
            var sale = NewSale(300);
            var buyer = Buyer(1000);

            var first = BuyReply(buyer, sale.ListingId, 300, "pack-repeat-1", ToPack: true);
            Assert.IsTrue(first.InPack, first.Message);
            Assert.AreEqual(first, BuyReply(buyer, sale.ListingId, 300, "pack-repeat-1", ToPack: true), "the same answer");

            // the buyer puts the item back in the Vault: the same key must not take it out again
            Assert.AreEqual(VaultOutcome.Deposited, VaultTestWorld.Deposit(buyer, sale.Guid).Outcome);
            Assert.AreEqual(first, BuyReply(buyer, sale.ListingId, 300, "pack-repeat-1", ToPack: true), "still the first answer");

            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(sale.Guid).State, "left in the Vault");
            Assert.IsNull(buyer.GetInventoryItem(sale.Guid));
            Assert.AreEqual(700L, Ledger.GetBalance(buyer.Character.AccountId), "charged once");
        }

        [TestMethod]
        public void MarketBuy_APackThatFillsAfterTheFitCheck_StillBuys_AndLeavesTheItemInTheVault_AndSaysSo()
        {
            var sale = NewSale(300);
            var buyer = Buyer(1000);
            Assert.IsTrue(FitsReply(buyer, sale.ListingId).Fits, "room when the dialog asked");
            buyer.ItemCapacity = 0; // the pack filled meanwhile

            var bought = BuyReply(buyer, sale.ListingId, 300, "filled-1", ToPack: true);

            Assert.AreEqual(MarketBuyActions.Ok, bought.Code, "the purchase stands");
            Assert.IsFalse(bought.InPack);
            Assert.AreEqual($"{VaultMessages.For(VaultOutcome.NoPackSpace, "Buy sword")} Buy sword is in your Vault.", bought.Message);
            Assert.AreEqual(buyer.Character.AccountId, VaultStore.Get(sale.Guid).AccountId);
            Assert.IsNull(buyer.GetInventoryItem(sale.Guid));
        }

        [TestMethod]
        public void MarketBuy_AfterAPriceChange_AtTheOldPrice_IsRefusedWithTheNewPrice_AndNothingMoves()
        {
            var sale = NewSale(300);
            var buyer = Buyer(1000);

            // the seller changes the price while the buyer looks, as a reprice saves it
            using (var shard = new ShardDbContext())
                Assert.AreEqual(ListingOutcome.Ok, ListingStore.Reprice(shard, sale.SellerAccount, sale.ListingId, 450, () => false).Outcome);

            var refused = BuyReply(buyer, sale.ListingId, 300, "changed-1", ToPack: true);

            Assert.AreEqual("price_changed", refused.Code);
            Assert.AreEqual(450L, refused.Price, "the new price");
            Assert.AreEqual("The price has changed to 450 MMD. Nothing was charged.", refused.Message);
            AssertNothingMoved(buyer, sale, 1000);
        }

        [TestMethod]
        public void MarketBuy_EachRefusal_TellsThePlayerWhy_AndMovesNothing()
        {
            // gone: the listing was delisted
            var delisted = NewSale(300);
            var buyer = Buyer(1000);
            using (var shard = new ShardDbContext())
                Assert.AreEqual(ListingOutcome.Ok, ListingStore.Delist(shard, delisted.SellerAccount, delisted.ListingId, DateTime.UtcNow, () => false).Outcome);
            AssertRefused(buyer, delisted, "gone", MarketBrowseActions.GoneMessage, 1000, VaultItemState.Held);

            // can't afford
            var dear = NewSale(5000);
            AssertRefused(buyer, dear, "insufficient_funds", "You do not have enough MMD for this purchase.", 1000);

            // own listing: the seller at the station buys their own
            var own = NewSale(300);
            var seller = own.Seller;
            AtTheMarketStation(seller);
            AssertRefused(seller, own, "own_listing", "You cannot buy your own listing.", 0);

            // the front door refuses a price under 1 before the rules
            var cheap = NewSale(300);
            var zero = BuyReply(buyer, cheap.ListingId, 0, "zero-1", ToPack: true);
            Assert.AreEqual("invalid_price", zero.Code);
            Assert.AreEqual(MarketSellActions.InvalidPrice, zero.Message, "the sell path's words");
            AssertNothingMoved(buyer, cheap, 1000);
        }

        [TestMethod]
        public void MarketBuy_WhileTheMarketIsPaused_IsRefused_AndMovesNothing()
        {
            var sale = NewSale(300);
            var buyer = Buyer(1000);

            using (var shard = new ShardDbContext())
                MarketPause.Pause(shard, "a test", DateTime.UtcNow);
            try
            {
                AssertRefused(buyer, sale, "paused", "The market is paused. Purchases and MMD withdrawals are temporarily unavailable.", 1000);
            }
            finally
            {
                using var shard = new ShardDbContext();
                MarketPause.Resume(shard, "a test", DateTime.UtcNow);
            }
        }

        [TestMethod]
        public void MarketBuy_ABannedBuyer_IsRefused_AndMovesNothing()
        {
            var sale = NewSale(300);
            var buyer = Buyer(1000);
            MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"UPDATE account SET banned_Time = UTC_TIMESTAMP(), ban_Expire_Time = UTC_TIMESTAMP() + INTERVAL 1 DAY WHERE accountId = {buyer.Character.AccountId};");

            AssertRefused(buyer, sale, "banned", VaultMessages.For(VaultOutcome.Banned, null), 1000);
        }

        [TestMethod]
        public void MarketBuy_TooManyAttempts_IsRateLimited_AndMovesNothing()
        {
            var sale = NewSale(300);
            var buyer = Buyer(1000);

            SetSetting(MarketSettings.PurchasesPerMinute, 1);
            try
            {
                // the one attempt the minute allows, refused for its price: it still counts
                Assert.AreEqual("price_changed", BuyReply(buyer, sale.ListingId, 299, "rate-1", ToPack: true).Code);

                AssertRefused(buyer, sale, "rate_limited", "Too many purchase attempts. Wait a minute and try again.", 1000);
            }
            finally
            {
                ClearSetting(MarketSettings.PurchasesPerMinute);
            }
        }

        // ---- helpers for the buy actions ----

        private sealed record Sale(Player Seller, uint Guid, long ListingId)
        {
            public uint SellerAccount => Seller.Character.AccountId;
        }

        private sealed record Bought(string Code, string Message, long Price, long Balance, bool InPack);

        /// <summary>
        /// A sword named "Buy sword" listed at the price by a new seller with an auth account, as the purchase reads the seller's ban from it
        /// </summary>
        private static Sale NewSale(long price)
        {
            var seller = Trader();
            var guid = Listed(seller, "Buy sword", price);
            using var shard = new ShardDbContext();
            return new Sale(seller, guid, shard.MarketListings.Single(l => l.ItemGuid == guid).Id);
        }

        /// <summary>A buyer at the market station with an auth account, a saved character and the balance</summary>
        private static Player Buyer(long balance)
        {
            var buyer = Trader();
            AddLedgerTransfer(buyer.Character.AccountId, balance, DateTime.UtcNow, SystemAccount.Admin, TransferKind.AdminAdjust, "test");
            AtTheMarketStation(buyer);
            return buyer;
        }

        /// <summary>
        /// A connected player with an auth account and a saved character: the purchase reads the bans from the one and the buyer's character from the other
        /// </summary>
        private static Player Trader()
        {
            var player = ConnectedPlayer();
            AuthAccount(player.Character.AccountId);
            Assert.IsTrue(DatabaseManager.Shard.BaseDatabase.SaveCharacter(player.Character, player.CharacterDatabaseLock), "the character saved");
            return player;
        }

        private static void AuthAccount(uint account) => MarketTestDatabase.Execute(VaultTestWorld.AuthDb,
            $"INSERT INTO account (accountId, accountName, passwordHash, passwordSalt, accessLevel) VALUES ({account}, 'buy{account}', 'x', 'use bcrypt', 0);");

        private static void AssertRefused(Player buyer, Sale sale, string code, string message, long buyerBalance, string itemState = VaultItemState.Listed)
        {
            var refused = BuyReply(buyer, sale.ListingId, PriceOf(sale), "refused-" + Guid.NewGuid().ToString("N"), ToPack: true);

            Assert.AreEqual(code, refused.Code, refused.Message);
            Assert.AreEqual(message, refused.Message);
            Assert.IsFalse(refused.InPack);
            AssertNothingMoved(buyer, sale, buyerBalance, itemState);
        }

        private static void AssertNothingMoved(Player buyer, Sale sale, long buyerBalance, string itemState = VaultItemState.Listed)
        {
            var row = VaultStore.Get(sale.Guid);
            Assert.AreEqual(sale.SellerAccount, row.AccountId, "still the seller's");
            Assert.AreEqual(itemState, row.State);
            Assert.IsNull(buyer.GetInventoryItem(sale.Guid), "not in the buyer's pack");
            Assert.AreEqual(buyerBalance, Ledger.GetBalance(buyer.Character.AccountId), "the buyer's balance");
            if (buyer.Character.AccountId != sale.SellerAccount)
                Assert.AreEqual(0L, Ledger.GetBalance(sale.SellerAccount), "the seller's balance");
        }

        private static long PriceOf(Sale sale)
        {
            using var shard = new ShardDbContext();
            return shard.MarketListings.Single(l => l.Id == sale.ListingId).Price;
        }

        /// <summary>A buy of the whole stack, which is one sword in these tests</summary>
        private static byte[] BuyBody(long listingId, long price, string key, bool toPack = true) => ChannelWire.Body(w =>
        {
            w.Write(listingId);
            w.Write(price);
            w.Write(1);
            ChannelWire.WriteString(w, key);
            w.Write(toPack ? MarketBuyActions.ToPack : MarketBuyActions.ToVault);
        });

        private static Bought BuyReply(Player buyer, long listingId, long price, string key, bool ToPack)
        {
            var reply = Request(buyer, MarketBuyActions.Buy, BuyBody(listingId, price, key, ToPack));
            if (reply.Status != ChannelStatus.Ok) Assert.Fail($"{reply.Status}: {Text(reply.Body)}");

            var reader = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            return new Bought(ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), reader.ReadInt64(), reader.ReadInt64(), reader.ReadByte() != 0);
        }

        private static (bool Fits, int FreeSlots, int Slots, string Reason) FitsReply(Player buyer, long listingId)
        {
            var reply = Request(buyer, MarketBuyActions.Fits, BitConverter.GetBytes(listingId));
            if (reply.Status != ChannelStatus.Ok) Assert.Fail($"{reply.Status}: {Text(reply.Body)}");

            var reader = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            return (reader.ReadByte() != 0, reader.ReadInt32(), reader.ReadInt32(), ChannelWire.ReadString(reader));
        }
    }
}
