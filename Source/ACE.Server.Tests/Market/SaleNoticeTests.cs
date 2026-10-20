using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Server.ClientChannel;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.Tests.ClientChannel;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ClientChannel.ChannelTestClient;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// market.sold: the online seller's client hears of each sale once, whether the buyer used the game or the website, with no station open.
    /// A seller offline at the sale isn't told; the ledger has it.
    /// </summary>
    public partial class VaultTests
    {
        [TestMethod]
        public void MarketSold_IsPushedOnceToTheOnlineSeller_ForAGameSaleAndAWebsiteSale_AndNotForAReplayedKey()
        {
            var seller = Trader();
            using var online = VaultTestWorld.Online(seller);
            var buyer = Buyer(1000);

            var game = ListingBy(seller, "Game sword", 300);
            Assert.AreEqual(MarketBuyActions.Ok, BuyReply(buyer, game, 300, "sold-game", ToPack: false).Code);
            Assert.AreEqual(MarketBuyActions.Ok, BuyReply(buyer, game, 300, "sold-game", ToPack: false).Code, "the same key, replayed");

            // the website's purchase: the shared rules as the portal calls them, which the game hears of only through its poll
            var website = ListingBy(seller, "Web sword", 200);
            Assert.AreEqual(BuyOutcome.Ok, WebsiteBuy(buyer, website, 200, "sold-web"));

            // no game sale follows yet, so only the poll can have found it
            var sold = SoldPushes(seller, website);

            var last = ListingBy(seller, "Last sword", 100);
            Assert.AreEqual(MarketBuyActions.Ok, BuyReply(buyer, last, 100, "sold-last", ToPack: false).Code);
            sold.AddRange(SoldPushes(seller, last));

            Assert.AreEqual(3, sold.Count, "one push per sale: " + string.Join(", ", sold.Select(s => s.Item)));
            Assert.AreEqual((game, "Game sword", buyer.Name, 300L, 300L), sold[0]);
            Assert.AreEqual((website, "Web sword", buyer.Name, 200L, 500L), sold[1]);
            Assert.AreEqual((last, "Last sword", buyer.Name, 100L, 600L), sold[2]);
        }

        [TestMethod]
        public void MarketSold_ASellerOfflineAtTheSale_IsNotTold_AndTheLedgerHasIt()
        {
            var seller = Trader();
            var buyer = Buyer(1000);

            var away = ListingBy(seller, "Away sword", 300);
            Assert.AreEqual(MarketBuyActions.Ok, BuyReply(buyer, away, 300, "sold-away", ToPack: false).Code);

            // the seller logs in after the sale: the next sale is the first they hear of
            var online = VaultTestWorld.Online(seller);
            var back = ListingBy(seller, "Back sword", 100);
            Assert.AreEqual(MarketBuyActions.Ok, BuyReply(buyer, back, 100, "sold-back", ToPack: false).Code);

            Assert.AreEqual(back, SoldPushes(seller, back).Single().ListingId, "only the sale made while online");

            // a quick logout and login, with no poll between to see the seller gone (market_enabled off stops the poll): the website's sale
            // between them is the ledger's alone, though the server still remembers the seller
            PropertyManager.ModifyBool(Vault.MarketEnabledKey, false);
            try
            {
                online.Dispose();
                var between = ListingBy(seller, "Between sword", 100);
                Assert.AreEqual(BuyOutcome.Ok, WebsiteBuy(buyer, between, 100, "sold-between"));
                online = VaultTestWorld.Online(seller);
            }
            finally
            {
                PropertyManager.ModifyBool(Vault.MarketEnabledKey, true);
            }
            using var again = online;
            var after = ListingBy(seller, "After sword", 100);
            Assert.AreEqual(MarketBuyActions.Ok, BuyReply(buyer, after, 100, "sold-after", ToPack: false).Code);

            Assert.AreEqual(after, SoldPushes(seller, after).Single().ListingId, "not the sale made between the sessions");

            using var shard = new ShardDbContext();
            var page = MarketHistory.ReadAsync(shard, seller.Character.AccountId, null, null, null, null, null).GetAwaiter().GetResult();
            Assert.IsTrue(page.Lines.Any(l => l.Text.StartsWith($"Sold Away sword to {buyer.Name}")), "the ledger has the sale made while offline");
        }

        [TestMethod]
        public void MarketSold_GoesToOneClient_TheListingCharacterWhenOnline_ElseTheOneLoggedInLast()
        {
            var seller = Trader();
            var alt = VaultTestWorld.NewPlayer(seller.Character.AccountId);
            Request(alt, ChannelHelloActions.Hello, Array.Empty<byte>());
            var buyer = Buyer(1000);

            var sellerOnline = VaultTestWorld.Online(seller);
            using var altOnline = VaultTestWorld.Online(alt);

            // both characters online: the one that listed it hears
            var first = ListingBy(seller, "First sword", 100);
            Assert.AreEqual(MarketBuyActions.Ok, BuyReply(buyer, first, 100, "one-first", ToPack: false).Code);
            Assert.AreEqual(first, SoldPushes(seller, first).Single().ListingId);

            // the listing's character logs off: the other one hears, and it never heard of the first
            sellerOnline.Dispose();
            var second = ListingBy(seller, "Second sword", 100);
            Assert.AreEqual(MarketBuyActions.Ok, BuyReply(buyer, second, 100, "one-second", ToPack: false).Code);
            Assert.AreEqual(second, SoldPushes(alt, second).Single().ListingId, "only the second sale, not the first again");
        }

        // ---- helpers for the sale notices ----

        /// <summary>A purchase as the portal makes it: the shared rules, which the game hears of only through its poll</summary>
        private static BuyOutcome WebsiteBuy(Player buyer, long listingId, long price, string key)
        {
            var database = new MarketDatabase(ConfigManager.Config.MySql);
            return PurchaseRules.Buy(database, new DatabaseMarketPause(database), MarketFees.Policy, listingId, buyer.Character.AccountId, null, price, 1, key,
                DateTime.UtcNow).Outcome;
        }

        /// <summary>A sword with the name listed by the seller at the price; its listing id</summary>
        private static long ListingBy(Player seller, string name, long price)
        {
            var guid = Listed(seller, name, price);
            using var shard = new ShardDbContext();
            return shard.MarketListings.Single(l => l.ItemGuid == guid).Id;
        }

        /// <summary>The market.sold pushes the seller's client got, up to the one for the listing, decoded as the client reads them</summary>
        private static List<(long ListingId, string Item, string Buyer, long Price, long BalanceAfter)> SoldPushes(Player seller, long upToListing)
        {
            static (long, string, string, long, long) Read(ChannelEvent e)
            {
                var reader = new BinaryReader(new MemoryStream(e.Body), Encoding.UTF8);
                return (reader.ReadInt64(), ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), reader.ReadInt64(), reader.ReadInt64());
            }

            static bool IsSold(ChannelEvent e) => e.Kind == ChannelEventKind.Push && e.Topic == SaleNotices.Sold;

            return WaitForEvents(seller, e => IsSold(e) && Read(e).Item1 == upToListing).Where(IsSold).Select(Read).ToList();
        }
    }
}
