using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum;
using ACE.Server.ClientChannel;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.Tests.ClientChannel;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ClientChannel.ChannelTestClient;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// The Sell screen's channel actions: which items can be listed, listing from the pack or the Vault, the refusals, delisting and the
    /// account's listings. Sent as the Market window sends them, in the Vault's test world. Listing and delisting run the shared listing store.
    /// </summary>
    public partial class VaultTests
    {
        [TestMethod]
        public void MarketSell_NeedsTheMarketStation_AndMovesNothing()
        {
            var player = ConnectedPlayer();
            var item = VaultTestWorld.Give(player, NewSword()).Guid.ClientGUID;

            Assert.AreEqual(ChannelStatus.NoStation, Request(player, MarketSellActions.Sellable, SellableBody(item)).Status);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, MarketSellActions.List, ListBody(item, 10)).Status);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, MarketSellActions.Delist, DelistBody(1)).Status);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, MarketSellActions.Reprice, RepriceBody(1, 10)).Status);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, MarketSellActions.Listings, ListingsBody(0, 10)).Status);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, MarketSellActions.VaultPage, ListRequestBody(string.Empty, 0, 10)).Status);

            Assert.IsNotNull(player.GetInventoryItem(item), "a refused list moved nothing");
            Assert.IsNull(VaultStore.Get(item));
        }

        [TestMethod]
        public void MarketSell_Sellable_SaysWhichItemsCanBeListedAndWhy()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var plain = VaultTestWorld.Give(player, NewSword()).Guid.ClientGUID;
            var attuned = NewSword();
            attuned.Attuned = AttunedStatus.Attuned;
            VaultTestWorld.Give(player, attuned);
            var bonded = NewSword();
            bonded.Bonded = BondedStatus.Bonded;
            VaultTestWorld.Give(player, bonded);
            var (_, held) = DepositedItem(player);
            var (_, listedInVault) = DepositedItem(player);
            ListAs(player, listedInVault, 25);

            var answers = Sellable(player, plain, attuned.Guid.ClientGUID, bonded.Guid.ClientGUID, held, listedInVault, 0x7FFFFFF0u);

            Assert.IsTrue(answers[plain].Sellable, "a plain pack item");
            Assert.IsFalse(answers[attuned.Guid.ClientGUID].Sellable);
            Assert.AreEqual(VaultMessages.For(VaultOutcome.Attuned, attuned.Name), answers[attuned.Guid.ClientGUID].Reason);
            Assert.AreEqual(VaultMessages.For(VaultOutcome.Bonded, bonded.Name), answers[bonded.Guid.ClientGUID].Reason);
            Assert.IsTrue(answers[held].Sellable, "a held Vault item");
            Assert.IsFalse(answers[listedInVault].Sellable, "a listed Vault item is already for sale");
            Assert.IsFalse(answers[0x7FFFFFF0u].Sellable);
            Assert.AreEqual("That item is not in your pack or Vault.", answers[0x7FFFFFF0u].Reason);
            Assert.IsNotNull(player.GetInventoryItem(plain), "a check moves nothing");
        }

        [TestMethod]
        public void MarketSell_ListFromThePack_DepositsItThenListsIt()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var item = VaultTestWorld.Give(player, NewSword());
            var guid = item.Guid.ClientGUID;

            var (listed, inVault, message) = ListReply(player, guid, 975);

            Assert.IsTrue(listed, message);
            Assert.IsTrue(inVault, "the item is in the Vault now");
            Assert.IsNull(player.GetInventoryItem(guid), "the item left the pack");
            Assert.AreEqual(VaultItemState.Listed, VaultStore.Get(guid).State);
            StringAssert.Contains(message, "listed for 975 MMD");
            var active = Listings(player, 0, 10);
            Assert.AreEqual(1, active.Rows.Count(r => r.ItemGuid == guid && r.Price == 975 && r.Status == ListingStatus.Active));
        }

        [TestMethod]
        public void MarketSell_TheVaultTab_ReadsTheVaultAtTheMarketStation()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, guid) = DepositedItem(player);

            // the chest's own list needs the chest's station; the Sell screen reads the same page here
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, VaultChannelActions.List, ListRequestBody(string.Empty, 0, 10)).Status);
            CollectionAssert.Contains(ListReply(player, ListRequestBody(string.Empty, 0, 10), MarketSellActions.VaultPage).Guids, guid);
        }

        [TestMethod]
        public void MarketSell_ListFromTheVault_ListsWithoutMovingIt()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, guid) = DepositedItem(player);

            var (listed, inVault, message) = ListReply(player, guid, 300);

            Assert.IsTrue(listed, message);
            Assert.IsTrue(inVault);
            Assert.AreEqual(VaultItemState.Listed, VaultStore.Get(guid).State);
            Assert.AreEqual(1, Listings(player, 0, 10).Rows.Count(r => r.ItemGuid == guid));
        }

        [TestMethod]
        public void MarketSell_ARefusedPrice_MovesNothing()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var guid = VaultTestWorld.Give(player, NewSword()).Guid.ClientGUID;

            var (listed, inVault, message) = ListReply(player, guid, 0);

            Assert.IsFalse(listed);
            Assert.IsFalse(inVault);
            StringAssert.Contains(message, "at least 1 MMD");
            Assert.IsNotNull(player.GetInventoryItem(guid), "still in the pack");
        }

        [TestMethod]
        public void MarketSell_AnAttunedPackItem_IsRefusedWithTheDepositMessage_AndMovesNothing()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var attuned = NewSword();
            attuned.Attuned = AttunedStatus.Attuned;
            VaultTestWorld.Give(player, attuned);

            var (listed, inVault, message) = ListReply(player, attuned.Guid.ClientGUID, 100);

            Assert.IsFalse(listed);
            Assert.IsFalse(inVault);
            Assert.AreEqual(VaultMessages.For(VaultOutcome.Attuned, attuned.Name), message);
            Assert.IsNotNull(player.GetInventoryItem(attuned.Guid.ClientGUID));
        }

        [TestMethod]
        public void MarketSell_ABondedItem_CannotBeListed_FromThePackOrTheVault()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var pack = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            pack.Bonded = BondedStatus.Bonded;
            VaultTestWorld.Give(player, pack);
            // the chest still takes a bonded item into the Vault; only a sale is refused
            var stored = DepositedNamed(player, 1, n => "Bonded sword", item => item.Bonded = BondedStatus.Bonded)[0];

            var fromPack = ListReply(player, pack.Guid.ClientGUID, 100);
            Assert.IsFalse(fromPack.Listed);
            Assert.IsFalse(fromPack.InVault, "refused before the deposit, so it stays in the pack");
            Assert.AreEqual(VaultMessages.For(VaultOutcome.Bonded, pack.Name), fromPack.Message);
            Assert.IsNotNull(player.GetInventoryItem(pack.Guid.ClientGUID));

            var fromVault = ListReply(player, stored, 100);
            Assert.IsFalse(fromVault.Listed);
            Assert.IsTrue(fromVault.InVault);
            Assert.AreEqual(VaultMessages.For(VaultOutcome.Bonded, "Bonded sword"), fromVault.Message);
            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(stored).State);
        }

        [TestMethod]
        public void MarketSell_AFullVault_RefusesAPackItem_AndMovesNothing()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, stored) = DepositedItem(player);
            var guid = VaultTestWorld.Give(player, NewSword()).Guid.ClientGUID;

            SetSetting(MarketSettings.VaultSize, 1);
            try
            {
                var (listed, inVault, message) = ListReply(player, guid, 100);

                Assert.IsFalse(listed);
                Assert.IsFalse(inVault);
                Assert.AreEqual(VaultMessages.For(VaultOutcome.VaultFull, null), message);
                Assert.IsNotNull(player.GetInventoryItem(guid), "still in the pack");
                Assert.IsNotNull(VaultStore.Get(stored));
            }
            finally
            {
                ClearSetting(MarketSettings.VaultSize);
            }
        }

        [TestMethod]
        public void MarketSell_AtTheListingLimit_RefusesAPackItemBeforeAnythingMoves()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, first) = DepositedItem(player);
            var second = VaultTestWorld.Give(player, NewSword()).Guid.ClientGUID;

            SetSetting(MarketSettings.ActiveListings, 1);
            try
            {
                Assert.IsTrue(ListReply(player, first, 100).Listed, "the one listing the limit allows");

                var (listed, inVault, message) = ListReply(player, second, 100);

                Assert.IsFalse(listed);
                Assert.IsFalse(inVault, "the limit is known before the deposit, so the item never moves");
                StringAssert.Contains(message, "1 active listing");
                Assert.IsNotNull(player.GetInventoryItem(second), "still in the pack");
                Assert.IsNull(VaultStore.Get(second));
            }
            finally
            {
                ClearSetting(MarketSettings.ActiveListings);
            }
        }

        [TestMethod]
        public void MarketSell_ABannedAccount_RefusesAPackItem_AndNothingMoves()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var item = NewSword();
            VaultTestWorld.Give(player, item);
            var guid = item.Guid.ClientGUID;
            var account = player.Character.AccountId;

            MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"INSERT INTO account (accountId, accountName, passwordHash, passwordSalt, accessLevel) VALUES ({account}, 'vaultpack{account}', 'x', 'use bcrypt', 0);");
            MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"UPDATE account SET banned_Time = UTC_TIMESTAMP(), ban_Expire_Time = UTC_TIMESTAMP() + INTERVAL 1 DAY WHERE accountId = {account};");
            try
            {
                var (listed, inVault, message) = ListReply(player, guid, 100);

                Assert.IsFalse(listed);
                Assert.IsFalse(inVault);
                Assert.AreEqual(VaultMessages.For(VaultOutcome.Banned, null), message);
                Assert.IsNotNull(player.GetInventoryItem(guid), "still in the pack");
                Assert.IsNull(VaultStore.Get(guid));
            }
            finally
            {
                MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"DELETE FROM account WHERE accountId = {account};");
            }
        }

        [TestMethod]
        public void MarketSell_ARecentPlayerFight_RefusesAPackItem_LikeTheChest_AndMovesNothing()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var item = NewSword();
            VaultTestWorld.Give(player, item);
            var guid = item.Guid.ClientGUID;
            var attacker = NewPk();
            player.PlayerKillerStatus = PlayerKillerStatus.PK;
            Player.UpdatePKTimers(attacker, player);

            var reason = VaultMessages.For(VaultOutcome.RecentPlayerFight, item.Name);
            Assert.AreEqual(reason, Sellable(player, guid)[guid].Reason, "sellable says why");

            var (listed, inVault, message) = ListReply(player, guid, 100);

            Assert.IsFalse(listed);
            Assert.IsFalse(inVault);
            Assert.AreEqual(reason, message);
            Assert.IsNotNull(player.GetInventoryItem(guid), "still in the pack");
        }

        [TestMethod]
        public void MarketSell_ABlockedWcid_IsNotSellable_AndIsRefusedToList()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var item = NewSword();
            VaultTestWorld.Give(player, item);
            var guid = item.Guid.ClientGUID;

            using (var shard = new ShardDbContext())
                VaultStore.Block(shard, VaultTestWorld.SwordWcid, "a test", 1, DateTime.UtcNow, out _);
            try
            {
                Assert.AreEqual(VaultMessages.For(VaultOutcome.BlockedWcid, item.Name), Sellable(player, guid)[guid].Reason);

                var (listed, inVault, message) = ListReply(player, guid, 100);
                Assert.IsFalse(listed);
                Assert.IsFalse(inVault);
                Assert.AreEqual(VaultMessages.For(VaultOutcome.BlockedWcid, item.Name), message);
                Assert.IsNotNull(player.GetInventoryItem(guid), "still in the pack");
            }
            finally
            {
                using var shard = new ShardDbContext();
                VaultStore.Unblock(shard, VaultTestWorld.SwordWcid);
            }
        }

        [TestMethod]
        public void MarketSell_EveryAction_IsRefusedWhileMarketEnabledIsOff()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, guid) = DepositedItem(player);

            PropertyManager.ModifyBool(Vault.MarketEnabledKey, false);
            try
            {
                foreach (var (action, body) in new[]
                {
                    (MarketSellActions.Sellable, SellableBody(guid)),
                    (MarketSellActions.List, ListBody(guid, 10)),
                    (MarketSellActions.Delist, DelistBody(1)),
                    (MarketSellActions.Reprice, RepriceBody(1, 10)),
                    (MarketSellActions.Listings, ListingsBody(0, 10)),
                    (MarketSellActions.VaultPage, ListRequestBody(string.Empty, 0, 10)),
                })
                {
                    var refused = Request(player, action, body);
                    Assert.AreEqual(ChannelStatus.Unavailable, refused.Status, action);
                    Assert.AreEqual(VaultMessages.For(VaultOutcome.MarketClosed, null), Text(refused.Body), action);
                }
            }
            finally
            {
                PropertyManager.ModifyBool(Vault.MarketEnabledKey, true);
            }
        }

        [TestMethod]
        public void MarketSell_AnExpiredListing_LeavesActive_FreesItsSlot_AndCannotBeDelisted()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, guid) = DepositedItem(player);
            ListAs(player, guid, 400);
            var listingId = Listings(player, 0, 10).Rows.Single(r => r.ItemGuid == guid).Id;

            // age the listing past its lifetime: the next game action expires it, as the portal's would
            var lifetime = MarketSettings.Get(MarketSettings.ListingLifetimeDays);
            using (var shard = new ShardDbContext())
            {
                var listing = shard.MarketListings.Single(l => l.Id == listingId);
                listing.CreatedTime = listing.CreatedTime.AddDays(-(lifetime + 1));
                shard.SaveChanges();
            }

            Assert.AreEqual(0, ReadAccount(player).ListingCount, "the account summary expires it before it counts");
            Assert.AreEqual(0, Listings(player, 0, 10).Total, "it is not active");
            Assert.IsTrue(Listings(player, 2, 10).Rows.Any(r => r.ItemGuid == guid && r.Status == ListingStatus.Expired), "it is on Expired");

            var delisted = ReadOutcome(Request(player, MarketSellActions.Delist, DelistBody(listingId)).Body);
            Assert.IsFalse(delisted.Done);
            StringAssert.Contains(delisted.Message, "no longer active");

            // the slot it held is free: with the limit at one, another listing goes through
            var (_, second) = DepositedItem(player);
            SetSetting(MarketSettings.ActiveListings, 1);
            try
            {
                Assert.IsTrue(ListReply(player, second, 300).Listed, "the expired listing no longer counts toward the limit");
            }
            finally
            {
                ClearSetting(MarketSettings.ActiveListings);
            }
        }

        [TestMethod]
        public void MarketSell_Sold_ShowsTheNameAfterTheBuyerHasWithdrawnTheItem()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, guid) = DepositedItem(player);
            ListAs(player, guid, 300);
            var listingId = Listings(player, 0, 10).Rows.Single(r => r.ItemGuid == guid).Id;

            // a sale, and the buyer's withdrawal, which takes the Vault row: the item's own record is what is left
            using (var shard = new ShardDbContext())
            {
                var listing = shard.MarketListings.Single(l => l.Id == listingId);
                listing.Status = ListingStatus.Sold;
                listing.ClosedTime = DateTime.UtcNow;
                listing.BuyerAccountId = VaultTestWorld.NewAccountId();
                shard.MarketVaultItems.Remove(shard.MarketVaultItems.Single(v => v.ItemGuid == guid));
                shard.SaveChanges();
            }

            var sold = Listings(player, 1, 10);
            Assert.AreEqual(1, sold.Total);
            var row = sold.Rows.Single();
            Assert.AreEqual(ListingStatus.Sold, row.Status);
            Assert.AreEqual(300L, row.Price);
            Assert.AreEqual("Vault sword", row.Name, "the name comes from the item, not the Vault row the buyer took");
        }

        [TestMethod]
        public void MarketSell_ABannedAccount_CannotListAVaultItem()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, guid) = DepositedItem(player);
            var account = VaultStore.Get(guid).AccountId;

            MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"INSERT INTO account (accountId, accountName, passwordHash, passwordSalt, accessLevel) VALUES ({account}, 'vaultban{account}', 'x', 'use bcrypt', 0);");
            MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"UPDATE account SET banned_Time = UTC_TIMESTAMP(), ban_Expire_Time = UTC_TIMESTAMP() + INTERVAL 1 DAY WHERE accountId = {account};");
            try
            {
                var (listed, inVault, message) = ListReply(player, guid, 100);

                Assert.IsFalse(listed);
                Assert.IsTrue(inVault);
                Assert.AreEqual(VaultMessages.For(VaultOutcome.Banned, null), message);
                Assert.AreEqual(VaultItemState.Held, VaultStore.Get(guid).State);
            }
            finally
            {
                MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"DELETE FROM account WHERE accountId = {account};");
            }
        }

        [TestMethod]
        public void MarketSell_Reprice_ChangesThePriceInPlace_AndKeepsTheListingsLifetime()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, guid) = DepositedItem(player);
            ListAs(player, guid, 400);
            var listingId = Listings(player, 0, 10).Rows.Single(r => r.ItemGuid == guid).Id;
            var createdBefore = ListingRecord(listingId).CreatedTime;

            var repriced = ReadOutcome(Request(player, MarketSellActions.Reprice, RepriceBody(listingId, 650)).Body);
            Assert.IsTrue(repriced.Done, repriced.Message);
            StringAssert.Contains(repriced.Message, "now listed for 650 MMD");

            var row = Listings(player, 0, 10).Rows.Single(r => r.Id == listingId);
            Assert.AreEqual(650, row.Price, "the same listing shows the new price");
            Assert.AreEqual(VaultItemState.Listed, VaultStore.Get(guid).State, "the item stays listed");
            Assert.AreEqual(createdBefore, ListingRecord(listingId).CreatedTime, "the lifetime is unchanged");
        }

        [TestMethod]
        public void MarketSell_Reprice_RefusesAPriceUnder1_AnotherAccountsListing_AndAnInactiveOne_AndChangesNothing()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, guid) = DepositedItem(player);
            ListAs(player, guid, 400);
            var listingId = Listings(player, 0, 10).Rows.Single(r => r.ItemGuid == guid).Id;

            var notAPrice = ReadOutcome(Request(player, MarketSellActions.Reprice, RepriceBody(listingId, 0)).Body);
            Assert.IsFalse(notAPrice.Done);
            StringAssert.Contains(notAPrice.Message, "whole price of at least 1");

            var stranger = ConnectedPlayer();
            AtTheMarketStation(stranger);
            var (_, theirs) = DepositedItem(stranger);
            ListAs(stranger, theirs, 50);
            var theirListing = Listings(stranger, 0, 10).Rows.Single(r => r.ItemGuid == theirs).Id;
            var notMine = ReadOutcome(Request(player, MarketSellActions.Reprice, RepriceBody(theirListing, 60)).Body);
            Assert.IsFalse(notMine.Done);
            StringAssert.Contains(notMine.Message, "not yours");
            Assert.AreEqual(50, ListingRecord(theirListing).Price);

            Assert.IsTrue(ReadOutcome(Request(player, MarketSellActions.Delist, DelistBody(listingId)).Body).Done);
            var inactive = ReadOutcome(Request(player, MarketSellActions.Reprice, RepriceBody(listingId, 700)).Body);
            Assert.IsFalse(inactive.Done);
            StringAssert.Contains(inactive.Message, "no longer active");
            Assert.AreEqual(400, ListingRecord(listingId).Price, "a delisted listing keeps the price it had");
        }

        [TestMethod]
        public void MarketSell_Delist_ReturnsTheItemToTheVault_AndOnlyTheOwnersActiveListings()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, guid) = DepositedItem(player);
            ListReply(player, guid, 400);
            var listingId = Listings(player, 0, 10).Rows.Single(r => r.ItemGuid == guid).Id;

            var delisted = Request(player, MarketSellActions.Delist, DelistBody(listingId));
            Assert.AreEqual(ChannelStatus.Ok, delisted.Status);
            Assert.AreEqual(1, delisted.Body[0], "delisted");
            Assert.IsTrue(ReadOutcome(delisted.Body).InVault, "the item is in the Vault");
            StringAssert.Contains(ReadOutcome(delisted.Body).Message, "back in your Vault");
            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(guid).State);
            Assert.AreEqual(0, Listings(player, 0, 10).Rows.Count(r => r.ItemGuid == guid), "no longer active");

            var again = Request(player, MarketSellActions.Delist, DelistBody(listingId));
            Assert.AreEqual(0, again.Body[0], "a delisted listing can't be delisted again");
            StringAssert.Contains(ReadOutcome(again.Body).Message, "no longer active");

            var stranger = ConnectedPlayer();
            AtTheMarketStation(stranger);
            var (_, theirs) = DepositedItem(stranger);
            ListReply(stranger, theirs, 50);
            var theirListing = Listings(stranger, 0, 10).Rows.Single(r => r.ItemGuid == theirs).Id;
            var notMine = Request(player, MarketSellActions.Delist, DelistBody(theirListing));
            Assert.AreEqual(0, notMine.Body[0], "another account's listing is not found");
            Assert.AreEqual(VaultItemState.Listed, VaultStore.Get(theirs).State);
        }

        [TestMethod]
        public void MarketSell_Listings_ShowTheActiveAndExpiredTabsWithTheLifetimeFromTheSettings()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);
            var (_, kept) = DepositedItem(player);
            var (_, expiring) = DepositedItem(player);
            ListReply(player, kept, 10);
            ListReply(player, expiring, 20);

            var active = Listings(player, 0, 10);
            Assert.AreEqual((int)MarketSettings.Get(MarketSettings.ListingLifetimeDays), active.LifetimeDays, "the lifetime is the server's setting");
            Assert.AreEqual(2, active.Total);
            CollectionAssert.AreEquivalent(new[] { kept, expiring }, active.Rows.Select(r => r.ItemGuid).ToArray());

            // a listing past its lifetime leaves Active for Expired, as the upkeep expires it
            using (var shard = new ShardDbContext())
                ListingStore.ExpireOverdue(shard, DateTime.UtcNow.AddDays(MarketSettings.Get(MarketSettings.ListingLifetimeDays) + 1));

            var expired = Listings(player, 2, 10);
            Assert.AreEqual(2, expired.Total);
            Assert.IsTrue(expired.Rows.All(r => r.Status == ListingStatus.Expired));
            Assert.AreEqual(0, Listings(player, 0, 10).Total, "nothing is active any more");
        }

        [TestMethod]
        public void MarketSell_Sellable_AndListings_TruncatedRequests_AreBadRequests()
        {
            var player = ConnectedPlayer();
            AtTheMarketStation(player);

            Assert.AreEqual(ChannelStatus.BadRequest, Request(player, MarketSellActions.Sellable, ChannelWire.Body(w => w.Write(0))).Status);
            Assert.AreEqual(ChannelStatus.BadRequest, Request(player, MarketSellActions.List, new byte[] { 1, 2 }).Status);
            Assert.AreEqual(ChannelStatus.BadRequest, Request(player, MarketSellActions.Delist, new byte[] { 1 }).Status);
            Assert.AreEqual(ChannelStatus.BadRequest, Request(player, MarketSellActions.Listings, new byte[] { 0 }).Status);
        }

        // ---- helpers for the Sell actions ----

        /// <summary>
        /// Starts the player's session at the market station, as using it does
        /// </summary>
        private static void AtTheMarketStation(Player player)
        {
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);
        }

        /// <summary>
        /// Gives the player a sword in the Vault, and returns it with its guid: the Vault's own deposit, as the chest does it
        /// </summary>
        private static (Player Player, uint Guid) DepositedItem(Player player)
        {
            var guid = DepositedNamed(player, 1, n => "Vault sword", item => item.Bonded = BondedStatus.Normal)[0];
            return (player, guid);
        }

        /// <summary>
        /// A sword that can be sold: the test world's sword weenie is bonded, so it is set plain
        /// </summary>
        private static WorldObject NewSword()
        {
            var item = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            item.Bonded = BondedStatus.Normal;
            return item;
        }

        private static byte[] SellableBody(params uint[] guids) => ChannelWire.Body(w =>
        {
            w.Write(guids.Length);
            foreach (var guid in guids)
                w.Write(guid);
        });

        private static byte[] ListBody(uint guid, long price) => ChannelWire.Body(w =>
        {
            w.Write(guid);
            w.Write(price);
        });

        private static byte[] DelistBody(long listingId) => ChannelWire.Body(w => w.Write(listingId));

        private static byte[] RepriceBody(long listingId, long price) => ChannelWire.Body(w =>
        {
            w.Write(listingId);
            w.Write(price);
        });

        /// <summary>
        /// The listing row as the shard holds it, for the price and the lifetime the reply doesn't show
        /// </summary>
        private static Listing ListingRecord(long listingId)
        {
            using var shard = new ShardDbContext();
            return shard.MarketListings.Single(l => l.Id == listingId);
        }

        private static byte[] ListingsBody(byte tab, int count) => ChannelWire.Body(w =>
        {
            w.Write(tab);
            w.Write(count);
        });

        /// <summary>
        /// market.sellable for the guids: each one's answer, as read back
        /// </summary>
        private static Dictionary<uint, (bool Sellable, string Reason)> Sellable(Player player, params uint[] guids)
        {
            var reply = Request(player, MarketSellActions.Sellable, SellableBody(guids));
            Assert.AreEqual(ChannelStatus.Ok, reply.Status);

            var reader = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            var count = reader.ReadInt32();
            var answers = new Dictionary<uint, (bool Sellable, string Reason)>();
            for (var index = 0; index < count; index++)
            {
                var guid = reader.ReadUInt32();
                var sellable = reader.ReadByte() != 0;
                answers[guid] = (sellable, ChannelWire.ReadString(reader));
            }
            return answers;
        }

        /// <summary>
        /// market.list for an item at a price: whether it was listed, whether it is in the Vault now, and the message
        /// </summary>
        private static (bool Listed, bool InVault, string Message) ListReply(Player player, uint guid, long price)
        {
            var reply = Request(player, MarketSellActions.List, ListBody(guid, price));
            Assert.AreEqual(ChannelStatus.Ok, reply.Status);

            var reader = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            var listed = reader.ReadByte() != 0;
            var inVault = reader.ReadByte() != 0;
            return (listed, inVault, ChannelWire.ReadString(reader));
        }

        /// <summary>
        /// Lists a Vault item, asserting that it went through
        /// </summary>
        private static void ListAs(Player player, uint guid, long price)
        {
            var (listed, _, message) = ListReply(player, guid, price);
            Assert.IsTrue(listed, message);
        }

        private sealed record ListingRow(long Id, uint ItemGuid, string Name, long Price, string Status);

        private sealed record ListingsPage(long ServerNow, int LifetimeDays, int Total, List<ListingRow> Rows);

        /// <summary>
        /// market.listings for a tab (0 active, 1 sold, 2 expired), read back
        /// </summary>
        private static ListingsPage Listings(Player player, byte tab, int count)
        {
            var reply = Request(player, MarketSellActions.Listings, ListingsBody(tab, count));
            Assert.AreEqual(ChannelStatus.Ok, reply.Status);

            var reader = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            var now = reader.ReadInt64();
            var lifetime = reader.ReadInt32();
            var total = reader.ReadInt32();
            var shown = reader.ReadInt32();
            var rows = new List<ListingRow>(shown);
            for (var index = 0; index < shown; index++)
            {
                var id = reader.ReadInt64();
                var guid = reader.ReadUInt32();
                var name = ChannelWire.ReadString(reader);
                var price = reader.ReadInt64();
                var status = ChannelWire.ReadString(reader);
                reader.ReadInt64(); // created
                reader.ReadInt64(); // closed
                rows.Add(new ListingRow(id, guid, name, price, status));
            }
            return new ListingsPage(now, lifetime, total, rows);
        }

        /// <summary>
        /// The message of a reply that starts with a byte
        /// </summary>
        private static (bool Done, bool InVault, string Message) ReadOutcome(byte[] body)
        {
            var reader = new BinaryReader(new MemoryStream(body), Encoding.UTF8);
            var done = reader.ReadByte() != 0;
            var inVault = reader.ReadByte() != 0;
            return (done, inVault, ChannelWire.ReadString(reader));
        }

        /// <summary>
        /// Sets a market setting in the config table, as an admin would; ClearSetting puts the default back
        /// </summary>
        private static void SetSetting(MarketSetting setting, long value) =>
            MarketTestDatabase.Execute(Db, $"REPLACE INTO config_properties_long (`key`, `value`, description) VALUES ('{setting.Key}', {value}, 'test');");

        private static void ClearSetting(MarketSetting setting) =>
            MarketTestDatabase.Execute(Db, $"DELETE FROM config_properties_long WHERE `key` = '{setting.Key}';");
    }
}
