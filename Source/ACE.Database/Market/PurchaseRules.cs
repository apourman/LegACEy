using System;
using System.Linq;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    public enum BuyOutcome
    {
        // bought: the receipt, the first time or replayed for a repeated key
        Ok,

        // the idempotency key isn't 1 to 64 characters
        InvalidKey,

        // the account used the key for another kind of request
        KeyReused,

        // purchases are paused server-wide
        Paused,

        // the account made the most purchase attempts a minute allows
        RateLimited,

        // the character to buy as isn't one of the account's, or the account has none
        InvalidCharacter,

        // the listing isn't for sale (sold, delisted, expired, returned, unknown, or its seller is banned)
        Gone,

        // the buyer is the seller
        OwnListing,

        // the listing's price isn't the one the buyer saw
        PriceChanged,

        // the count isn't the whole stack
        InvalidCount,

        // the fee policy quoted a fee that isn't a whole MMD between 0 and the price
        InvalidFee,

        // the buyer's balance is less than the price
        InsufficientFunds,

        // the buyer is banned
        Banned,

        // the listing is still buyable, but every save lost a race
        Busy,
    }

    /// <param name="Receipt">Ok only</param>
    /// <param name="Price">PriceChanged only: the listing's price now</param>
    /// <param name="Problem">InvalidFee only: what the operator's log should say</param>
    /// <param name="Replayed">Ok only: the answer is the first one to this idempotency key, and this request changed nothing</param>
    public sealed record BuyResult(BuyOutcome Outcome, PurchaseReceipt Receipt = null, long? Price = null, string Problem = null, bool Replayed = false);

    /// <summary>
    /// Buying a listing, whichever front door asks: one save moves the MMD and the Vault row's owner (see PurchaseStore). The item stays where it is.
    /// </summary>
    public static class PurchaseRules
    {
        /// <summary>
        /// A save that loses a race re-reads and decides again; this many tries before answering busy
        /// </summary>
        private const int MaxAttempts = 5;

        /// <summary>
        /// Buys the listing for the buyer's account. A repeated idempotency key gets the first answer and nothing else happens.
        /// </summary>
        /// <param name="requestedCharacterId">the buyer's character the Vault row names; the account's first character when null</param>
        /// <param name="count">must equal the whole stack</param>
        public static BuyResult Buy(MarketDatabase database, IMarketPause pause, IFeePolicy feePolicy,
            long listingId, uint buyer, uint? requestedCharacterId, long expectedPrice, int count, string idempotencyKey, DateTime now)
        {
            if (!PurchaseStore.IsIdempotencyKey(idempotencyKey))
                return Refused(BuyOutcome.InvalidKey);

            using var shard = database.CreateShard();

            var stored = PurchaseStore.StoredRequest(shard, buyer, idempotencyKey);

            if (stored != null)
                return Replay(stored);

            if (pause.IsPaused)
                return Refused(BuyOutcome.Paused);

            if (!PurchaseLimiter.TryAcquire(shard, buyer, now, Math.Max(1, MarketSettings.Get(shard, MarketSettings.PurchasesPerMinute))))
                return Refused(BuyOutcome.RateLimited);

            var characterId = BuyerCharacter(shard, buyer, requestedCharacterId);

            if (characterId == null)
                return Refused(BuyOutcome.InvalidCharacter);

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var answer = Attempt(shard, listingId, buyer, characterId.Value, expectedPrice, count, idempotencyKey, now, database, feePolicy);

                // the same key may have been used by a parallel request meanwhile: its answer wins over a refusal or a lost race
                if (answer?.Outcome != BuyOutcome.Ok)
                {
                    stored = PurchaseStore.StoredRequest(shard, buyer, idempotencyKey);

                    if (stored != null)
                        return Replay(stored);
                }

                if (answer != null)
                    return answer;
            }

            return Refused(BuyOutcome.Busy);
        }

        /// <summary>
        /// The first answer to a key: a purchase's receipt, or KeyReused if the account used the key for another kind of request
        /// </summary>
        private static BuyResult Replay(Request stored)
        {
            if (stored.Kind != PurchaseStore.RequestKind)
                return Refused(BuyOutcome.KeyReused);

            return new BuyResult(BuyOutcome.Ok, PurchaseReceipt.FromJson(stored.Result), Replayed: true);
        }

        /// <summary>
        /// One read of the listing and one try at the save. Null when the save lost a race and must be decided again.
        /// </summary>
        private static BuyResult Attempt(ShardDbContext shard, long listingId, uint buyer, uint characterId, long expectedPrice, int count, string key, DateTime now,
            MarketDatabase database, IFeePolicy feePolicy)
        {
            // buyable is what a visitor can see; the seller's ban is read just before the save
            var row = ListingCatalog.Visible(shard, now, Array.Empty<uint>()).FirstOrDefault(r => r.Listing.Id == listingId);

            if (row == null)
                return Refused(BuyOutcome.Gone);

            var listing = row.Listing;
            var item = row.Item;

            if (listing.SellerAccountId == buyer)
                return Refused(BuyOutcome.OwnListing);

            if (listing.Price != expectedPrice)
                return new BuyResult(BuyOutcome.PriceChanged, Price: listing.Price);

            if (count != item.StackSize)
                return Refused(BuyOutcome.InvalidCount);

            var sale = new Sale(listing.SellerAccountId, buyer, listing.Id, item.Wcid, item.ItemType, item.StackSize, listing.Price);
            var fee = feePolicy.Quote(sale);

            if (fee == null || fee.Amount < 0 || fee.Amount > listing.Price || fee.Amount != decimal.Truncate(fee.Amount))
                return new BuyResult(BuyOutcome.InvalidFee, Problem: $"Fee policy {feePolicy.GetType().Name} quoted {fee?.Amount} for {sale}: a fee is whole MMD between 0 and the price. The purchase was refused.");

            // read without tracking by the catalog query: track them as read, so the save is guarded by the row versions that were read
            shard.Attach(listing);
            shard.Attach(item);

            var result = PurchaseStore.Complete(shard, listing, item, new Purchase(buyer, characterId, key, (long)fee.Amount, fee.Reason), now, account => database.IsBanned(account, now));

            switch (result.Outcome)
            {
                case PurchaseOutcome.Ok:
                    return new BuyResult(BuyOutcome.Ok, result.Receipt);

                case PurchaseOutcome.NotBuyable:
                    return Refused(BuyOutcome.Gone);

                case PurchaseOutcome.InsufficientFunds:
                    return Refused(BuyOutcome.InsufficientFunds);

                case PurchaseOutcome.BuyerBanned:
                    return Refused(BuyOutcome.Banned);

                case PurchaseOutcome.SellerBanned:
                    // the ban is noticed: the seller's listings go back to their Vault, and their web sessions end
                    MarketUpkeep.ReturnListingsAndEndWebSessions(database, new[] { listing.SellerAccountId }, now);
                    return Refused(BuyOutcome.Gone);

                case PurchaseOutcome.Conflict:
                    return null;

                default:
                    throw new ArgumentOutOfRangeException(nameof(result.Outcome), result.Outcome, null);
            }
        }

        /// <summary>
        /// The requested character if it's one of the account's, else the account's first; null if there's none
        /// </summary>
        private static uint? BuyerCharacter(ShardDbContext shard, uint accountId, uint? requested)
        {
            var characters = shard.Character.Where(c => c.AccountId == accountId && !c.IsDeleted);

            if (requested.HasValue)
                characters = characters.Where(c => c.Id == requested.Value);

            return characters.OrderBy(c => c.Id).Select(c => (uint?)c.Id).FirstOrDefault();
        }

        private static BuyResult Refused(BuyOutcome outcome) => new BuyResult(outcome);
    }
}
