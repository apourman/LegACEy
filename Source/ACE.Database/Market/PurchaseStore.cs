using System;
using System.Linq;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    public enum PurchaseOutcome
    {
        Ok,

        // the listing isn't active, or its Vault row isn't the seller's listed item
        NotBuyable,

        // the buyer's balance is less than the price
        InsufficientFunds,

        // the buyer was banned when the save was about to happen
        BuyerBanned,

        // the seller was banned when the save was about to happen
        SellerBanned,

        // another change won a race for the listing, the Vault row, a balance or the idempotency key: nothing was written, re-read and decide again
        Conflict,
    }

    /// <summary>
    /// A purchase the buyer asked for, with the fee the policy quoted
    /// </summary>
    /// <param name="Fee">whole MMD, between 0 and the price, taken from the seller</param>
    /// <param name="FeeReason">recorded on the fee's ledger entry</param>
    public sealed record Purchase(uint BuyerAccountId, uint BuyerCharacterId, string IdempotencyKey, long Fee, string FeeReason = null);

    /// <summary>
    /// The answer to a successful purchase, stored with the request so a replay returns it unchanged
    /// </summary>
    /// <param name="Balance">the buyer's balance after the purchase</param>
    public sealed record PurchaseReceipt(string Status, long ListingId, uint ItemGuid, long Price, long Fee, long Balance)
    {
        private static readonly JsonSerializerOptions json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        public string ToJson() => JsonSerializer.Serialize(this, json);

        /// <summary>
        /// MySQL keeps a json column in its own normal form, so a stored receipt is read back and written out again rather than returned as stored
        /// </summary>
        public static PurchaseReceipt FromJson(string text) => JsonSerializer.Deserialize<PurchaseReceipt>(text, json);
    }

    public sealed record PurchaseResult(PurchaseOutcome Outcome, PurchaseReceipt Receipt = null);

    /// <summary>
    /// The purchase's one save. Market tables only: the item stays where it is, and its Vault row changes owner.
    /// </summary>
    public static class PurchaseStore
    {
        public const string RequestKind = "purchase";

        /// <summary>
        /// The account's earlier request with this key, of any kind (a key is the account's, whatever it was used for), or null
        /// </summary>
        public static Request StoredRequest(ShardDbContext shard, uint accountId, string idempotencyKey)
        {
            return shard.MarketRequests.AsNoTracking().FirstOrDefault(r => r.AccountId == accountId && r.IdempotencyKey == idempotencyKey);
        }

        /// <summary>
        /// Sells the listing to the buyer in one SaveChanges: the listing is marked sold with the buyer, the Vault row moves to the buyer's account as held
        /// (both row versions bumped), one purchase transfer is written (buyer −price, seller +price, seller −fee, FEES +fee, the fee pair even at 0),
        /// sold and bought item events are recorded, and the request row stores the receipt.
        /// The listing and its Vault row must be tracked by the context as read, so the save fails if either changed since.
        /// Writes nothing unless the outcome is Ok.
        /// </summary>
        /// <param name="isBanned">called for the buyer and then the seller, just before the save</param>
        public static PurchaseResult Complete(ShardDbContext shard, Listing listing, VaultItem item, Purchase purchase, DateTime now, Func<uint, bool> isBanned)
        {
            now = ListingStore.Truncate(now);

            if (purchase.BuyerAccountId == listing.SellerAccountId)
                throw new ArgumentException("A buyer can't buy from their own account", nameof(purchase));

            if (purchase.Fee < 0 || purchase.Fee > listing.Price)
                throw new ArgumentOutOfRangeException(nameof(purchase), purchase.Fee, $"A fee is between 0 and the price ({listing.Price})");

            if (listing.Status != ListingStatus.Active || item.ItemGuid != listing.ItemGuid || item.AccountId != listing.SellerAccountId || item.State != VaultItemState.Listed)
                return new PurchaseResult(PurchaseOutcome.NotBuyable);

            var transfer = new Transfer
            {
                Kind = TransferKind.Purchase,
                ActorAccountId = purchase.BuyerAccountId,
                ActorCharacterId = purchase.BuyerCharacterId,
                ListingId = listing.Id,
                RequestKey = purchase.IdempotencyKey,
                CreatedTime = now,
            };

            var buyerEntry = Ledger.PlayerEntry(purchase.BuyerAccountId, -listing.Price);

            transfer.Entries.Add(buyerEntry);
            transfer.Entries.Add(Ledger.PlayerEntry(listing.SellerAccountId, listing.Price));
            transfer.Entries.Add(Ledger.PlayerEntry(listing.SellerAccountId, -purchase.Fee, purchase.FeeReason));
            transfer.Entries.Add(Ledger.SystemEntry(SystemAccount.Fees, purchase.Fee));

            if (!Ledger.TryAdd(shard, transfer))
            {
                shard.ChangeTracker.Clear();
                return new PurchaseResult(PurchaseOutcome.InsufficientFunds);
            }

            listing.Status = ListingStatus.Sold;
            listing.BuyerAccountId = purchase.BuyerAccountId;
            listing.BuyerCharacterId = purchase.BuyerCharacterId;
            listing.ClosedTime = now;
            listing.RowVersion++;

            item.AccountId = purchase.BuyerAccountId;
            item.CharacterId = purchase.BuyerCharacterId;
            item.State = VaultItemState.Held;
            item.RowVersion++;

            shard.MarketItemEvents.Add(new ItemEvent { ItemGuid = item.ItemGuid, AccountId = listing.SellerAccountId, CharacterId = listing.SellerCharacterId, Kind = ItemEventKind.Sold, ListingId = listing.Id, Transfer = transfer, EventTime = now });
            shard.MarketItemEvents.Add(new ItemEvent { ItemGuid = item.ItemGuid, AccountId = purchase.BuyerAccountId, CharacterId = purchase.BuyerCharacterId, Kind = ItemEventKind.Bought, ListingId = listing.Id, Transfer = transfer, EventTime = now });

            var receipt = new PurchaseReceipt("ok", listing.Id, item.ItemGuid, listing.Price, purchase.Fee, buyerEntry.BalanceAfter.Value);

            shard.MarketRequests.Add(new Request { AccountId = purchase.BuyerAccountId, IdempotencyKey = purchase.IdempotencyKey, Kind = RequestKind, Result = receipt.ToJson(), CreatedTime = now });

            var outcome = PurchaseOutcome.Ok;

            if (isBanned(purchase.BuyerAccountId))
                outcome = PurchaseOutcome.BuyerBanned;
            else if (isBanned(listing.SellerAccountId))
                outcome = PurchaseOutcome.SellerBanned;
            else if (!TrySave(shard))
                outcome = PurchaseOutcome.Conflict;

            if (outcome != PurchaseOutcome.Ok)
            {
                shard.ChangeTracker.Clear();
                return new PurchaseResult(outcome);
            }

            return new PurchaseResult(PurchaseOutcome.Ok, receipt);
        }

        /// <summary>
        /// One SaveChanges. False when a row version shows another change got there first, or another save took a first balance row or the same idempotency key.
        /// </summary>
        private static bool TrySave(ShardDbContext shard)
        {
            try
            {
                shard.SaveChanges();
                return true;
            }
            catch (DbUpdateException ex) when (Ledger.IsLostRace(ex))
            {
                return false;
            }
        }
    }
}
