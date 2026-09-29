using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    public enum ListingOutcome
    {
        Ok,

        // the price isn't a whole MMD of at least 1
        InvalidPrice,

        // the item isn't in the seller's Vault
        NotInVault,

        // the Vault row isn't held (listed, being withdrawn, or changed by someone else meanwhile)
        NotHeld,

        // the seller already has the most active listings the setting allows
        ListingLimit,

        // the character to list as isn't one of the account's
        InvalidCharacter,

        // no such listing of the account's
        NotFound,

        // the listing is no longer active (sold, delisted, expired, returned, or changed by someone else meanwhile)
        NotActive,

        // the account was banned when the save was about to happen
        Banned,
    }

    public sealed record ListingResult(ListingOutcome Outcome, Listing Listing = null);

    /// <summary>
    /// Listing, delisting, expiry and ban returns. Market tables only: the item never moves, only its Vault row's state.
    /// Every change is one SaveChanges guarded by the Vault row's and the listing's row versions, so a change that loses a race for a row writes nothing.
    /// The active-listing cap is a count, not a row, so parallel listings by one seller can pass it together.
    /// </summary>
    public static class ListingStore
    {
        /// <summary>
        /// Lists a held item of the account's Vault, the whole stack, for a whole-MMD price.
        /// </summary>
        /// <param name="characterId">the character to list as; the depositing character when null</param>
        /// <param name="isBanned">checked just before the save</param>
        public static ListingResult List(ShardDbContext shard, uint accountId, uint itemGuid, long price, uint? characterId, DateTime now, Func<bool> isBanned)
        {
            now = Truncate(now);

            if (price < 1)
                return new ListingResult(ListingOutcome.InvalidPrice);

            var item = shard.MarketVaultItems.FirstOrDefault(v => v.ItemGuid == itemGuid && v.AccountId == accountId);

            if (item == null)
                return new ListingResult(ListingOutcome.NotInVault);

            if (item.State != VaultItemState.Held)
                return new ListingResult(ListingOutcome.NotHeld);

            if (characterId.HasValue && !shard.Character.Any(c => c.Id == characterId.Value && c.AccountId == accountId && !c.IsDeleted))
                return new ListingResult(ListingOutcome.InvalidCharacter);

            var cap = MarketSettings.Get(shard, MarketSettings.ActiveListings);

            if (shard.MarketListings.Count(l => l.SellerAccountId == accountId && l.Status == ListingStatus.Active) >= cap)
                return new ListingResult(ListingOutcome.ListingLimit);

            if (isBanned())
                return new ListingResult(ListingOutcome.Banned);

            var listing = new Listing
            {
                ItemGuid = item.ItemGuid,
                SellerAccountId = accountId,
                SellerCharacterId = characterId ?? item.CharacterId,
                Price = price,
                Status = ListingStatus.Active,
                CreatedTime = now,
            };

            item.State = VaultItemState.Listed;
            item.RowVersion++;

            shard.MarketListings.Add(listing);
            shard.MarketItemEvents.Add(new ItemEvent { ItemGuid = item.ItemGuid, AccountId = accountId, CharacterId = listing.SellerCharacterId, Kind = ItemEventKind.List, Listing = listing, EventTime = now });

            if (!TrySave(shard))
                return new ListingResult(ListingOutcome.NotHeld);

            return new ListingResult(ListingOutcome.Ok, listing);
        }

        /// <summary>
        /// Takes an active listing of the account's down and puts its item back to held
        /// </summary>
        /// <param name="isBanned">checked just before the save</param>
        public static ListingResult Delist(ShardDbContext shard, uint accountId, long listingId, DateTime now, Func<bool> isBanned)
        {
            now = Truncate(now);

            var listing = shard.MarketListings.FirstOrDefault(l => l.Id == listingId && l.SellerAccountId == accountId);

            if (listing == null)
                return new ListingResult(ListingOutcome.NotFound);

            if (listing.Status != ListingStatus.Active)
                return new ListingResult(ListingOutcome.NotActive);

            var item = shard.MarketVaultItems.FirstOrDefault(v => v.ItemGuid == listing.ItemGuid);

            if (isBanned())
                return new ListingResult(ListingOutcome.Banned);

            Close(shard, listing, item, ListingStatus.Delisted, ItemEventKind.Delist, now);

            if (!TrySave(shard))
                return new ListingResult(ListingOutcome.NotActive);

            return new ListingResult(ListingOutcome.Ok, listing);
        }

        /// <summary>
        /// Listings created at or before this time have outlived the lifetime setting
        /// </summary>
        public static DateTime ExpiryCutoff(ShardDbContext shard, DateTime now)
        {
            var days = Math.Max(0, MarketSettings.Get(shard, MarketSettings.ListingLifetimeDays));

            return Truncate(now) - TimeSpan.FromDays(days);
        }

        /// <summary>
        /// Expires every active listing past its lifetime, back to held, with an expire event. Returns the number expired.
        /// When another change wins a race for one of them, nothing is saved and the next call tries again.
        /// </summary>
        public static int ExpireOverdue(ShardDbContext shard, DateTime now)
        {
            var cutoff = ExpiryCutoff(shard, now);

            var overdue = shard.MarketListings.Where(l => l.Status == ListingStatus.Active && l.CreatedTime <= cutoff).ToList();

            return CloseAll(shard, overdue, ListingStatus.Expired, ItemEventKind.Expire, now);
        }

        /// <summary>
        /// Returns every active listing of the given (banned) accounts to their Vaults: ban_returned, item held, a ban_return event.
        /// Returns the number returned. When another change wins a race for one of them, nothing is saved and the next call tries again.
        /// </summary>
        public static int ReturnListings(ShardDbContext shard, IReadOnlyCollection<uint> accountIds, DateTime now)
        {
            if (accountIds.Count == 0)
                return 0;

            var listings = shard.MarketListings.Where(l => l.Status == ListingStatus.Active && accountIds.Contains(l.SellerAccountId)).ToList();

            return CloseAll(shard, listings, ListingStatus.BanReturned, ItemEventKind.BanReturn, now);
        }

        private static int CloseAll(ShardDbContext shard, List<Listing> listings, string status, string eventKind, DateTime now)
        {
            if (listings.Count == 0)
                return 0;

            now = Truncate(now);

            var guids = listings.Select(l => l.ItemGuid).ToList();
            var items = shard.MarketVaultItems.Where(v => guids.Contains(v.ItemGuid)).ToDictionary(v => v.ItemGuid);

            foreach (var listing in listings)
                Close(shard, listing, items.GetValueOrDefault(listing.ItemGuid), status, eventKind, now);

            return TrySave(shard) ? listings.Count : 0;
        }

        /// <summary>
        /// Closes the listing and, if the seller's Vault row is still listed, puts it back to held
        /// </summary>
        private static void Close(ShardDbContext shard, Listing listing, VaultItem item, string status, string eventKind, DateTime now)
        {
            listing.Status = status;
            listing.ClosedTime = now;
            listing.RowVersion++;

            if (item != null && item.AccountId == listing.SellerAccountId && item.State == VaultItemState.Listed)
            {
                item.State = VaultItemState.Held;
                item.RowVersion++;
            }

            shard.MarketItemEvents.Add(new ItemEvent { ItemGuid = listing.ItemGuid, AccountId = listing.SellerAccountId, CharacterId = listing.SellerCharacterId, Kind = eventKind, ListingId = listing.Id, EventTime = now });
        }

        /// <summary>
        /// One SaveChanges. False, with nothing written and nothing left tracked, when a row version shows someone else changed a row first.
        /// </summary>
        private static bool TrySave(ShardDbContext shard)
        {
            try
            {
                shard.SaveChanges();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                shard.ChangeTracker.Clear();
                return false;
            }
        }

        /// <summary>
        /// Whole microseconds, as datetime(6) stores them, so a stored time compares equal to the time it was written from
        /// </summary>
        public static DateTime Truncate(DateTime time) => new DateTime(time.Ticks - time.Ticks % 10, time.Kind);
    }
}
