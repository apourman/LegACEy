using System;
using System.Linq;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using ACE.Database.Market;
using ACE.Database.Models.Shard;

namespace ACE.MarketApi
{
    /// <summary>
    /// Buying a listing: one save moves the MMD and the Vault row's owner (see PurchaseStore). The game server isn't involved.
    /// </summary>
    public static class PurchaseEndpoints
    {
        /// <summary>
        /// A save that loses a race re-reads and decides again; this many tries before answering busy
        /// </summary>
        private const int MaxAttempts = 5;

        private const int MaxKeyLength = 64;

        private static readonly uint[] noBans = Array.Empty<uint>();

        /// <param name="Count">must equal the whole stack</param>
        /// <param name="ExpectedPrice">the price the buyer saw; a decimal so that 1.5 is refused as a price rather than as unreadable JSON</param>
        /// <param name="IdempotencyKey">the buyer's key for this request (up to 64 characters): a repeat returns the first answer</param>
        /// <param name="CharacterId">the buyer's character the Vault row names; the account's first character when left out</param>
        public sealed record PurchaseRequest(int? Count, decimal? ExpectedPrice, string IdempotencyKey, uint? CharacterId);

        public static void Map(WebApplication app)
        {
            app.MapPost("/listings/{id:long}/purchase", Purchase).RequireAuthorization();
        }

        private static IResult Purchase(long id, PurchaseRequest request, HttpContext context, MarketDatabase database, TimeProvider time, IMarketPause pause, PurchaseLimiter limiter, IFeePolicy feePolicy, ILoggerFactory loggers)
        {
            if (request == null || string.IsNullOrEmpty(request.IdempotencyKey) || request.IdempotencyKey.Length > MaxKeyLength)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request");

            if (request.ExpectedPrice is not decimal expected || expected < 1 || expected != decimal.Truncate(expected) || expected > long.MaxValue)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_price");

            if (request.Count is not int count || count < 1)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_count");

            var buyer = MarketHttp.AccountId(context);
            var now = time.GetUtcNow().UtcDateTime;

            using var shard = database.CreateShard();

            // a repeated key gets the first answer and nothing else happens
            var stored = PurchaseStore.StoredReceipt(shard, buyer, request.IdempotencyKey);

            if (stored != null)
                return new Stored(stored).Result;

            if (pause.IsPaused)
                return MarketHttp.Error(StatusCodes.Status503ServiceUnavailable, "paused");

            if (!limiter.TryAcquire(buyer, now, Math.Max(1, MarketSettings.Get(shard, MarketSettings.PurchasesPerMinute))))
                return MarketHttp.Error(StatusCodes.Status429TooManyRequests, "rate_limited");

            var characterId = BuyerCharacter(shard, buyer, request.CharacterId);

            if (characterId == null)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_character");

            var logger = loggers.CreateLogger(typeof(PurchaseEndpoints).FullName);

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var answer = Attempt(shard, id, buyer, characterId.Value, (long)expected, count, request.IdempotencyKey, now, database, feePolicy, logger);

                if (answer == null)
                    continue;

                // the same key may have been bought by a parallel request meanwhile: that answer wins over a refusal
                if (answer is not Stored)
                {
                    stored = PurchaseStore.StoredReceipt(shard, buyer, request.IdempotencyKey);

                    if (stored != null)
                        return new Stored(stored).Result;
                }

                return answer.Result;
            }

            return MarketHttp.Error(StatusCodes.Status503ServiceUnavailable, "busy");
        }

        /// <summary>
        /// One read of the listing and one try at the save. Null when the save lost a race and must be decided again.
        /// </summary>
        private static Answer Attempt(ShardDbContext shard, long listingId, uint buyer, uint characterId, long expectedPrice, int count, string key, DateTime now,
            MarketDatabase database, IFeePolicy feePolicy, ILogger logger)
        {
            // buyable is what a visitor can see; the seller's ban is read just before the save
            var row = ListingCatalog.Visible(shard, now, noBans).FirstOrDefault(r => r.Listing.Id == listingId);

            if (row == null)
                return Refused(StatusCodes.Status410Gone, "gone");

            var listing = row.Listing;
            var item = row.Item;

            if (listing.SellerAccountId == buyer)
                return Refused(StatusCodes.Status403Forbidden, "own_listing");

            if (listing.Price != expectedPrice)
                return new Answer(Results.Json(new { error = "price_changed", price = listing.Price }, statusCode: StatusCodes.Status409Conflict));

            if (count != item.StackSize)
                return Refused(StatusCodes.Status400BadRequest, "invalid_count");

            var sale = new Sale(listing.SellerAccountId, buyer, listing.Id, item.Wcid, item.ItemType, item.StackSize, listing.Price);
            var fee = feePolicy.Quote(sale);

            if (fee == null || fee.Amount < 0 || fee.Amount > listing.Price || fee.Amount != decimal.Truncate(fee.Amount))
            {
                logger.LogError("Fee policy {Policy} quoted {Fee} for {Sale}: a fee is whole MMD between 0 and the price. The purchase was refused.", feePolicy.GetType().Name, fee?.Amount, sale);
                return Refused(StatusCodes.Status500InternalServerError, "invalid_fee");
            }

            // read without tracking by the catalog query: track them as read, so the save is guarded by the row versions that were read
            shard.Attach(listing);
            shard.Attach(item);

            var result = PurchaseStore.Complete(shard, listing, item, new Purchase(buyer, characterId, key, (long)fee.Amount, fee.Reason), now, account => database.IsBanned(account, now));

            switch (result.Outcome)
            {
                case PurchaseOutcome.Ok:
                    return new Stored(result.Receipt);

                case PurchaseOutcome.NotBuyable:
                    return Refused(StatusCodes.Status410Gone, "gone");

                case PurchaseOutcome.InsufficientFunds:
                    return Refused(StatusCodes.Status409Conflict, "insufficient_funds");

                case PurchaseOutcome.BuyerBanned:
                    return Refused(StatusCodes.Status403Forbidden, "banned");

                case PurchaseOutcome.SellerBanned:
                    // the ban is noticed: the seller's listings go back to their Vault
                    MarketUpkeep.ReturnListings(database, new[] { listing.SellerAccountId }, now);
                    return Refused(StatusCodes.Status410Gone, "gone");

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

        private static Answer Refused(int status, string error) => new Answer(MarketHttp.Error(status, error));

        private class Answer
        {
            public IResult Result { get; }

            public Answer(IResult result)
            {
                Result = result;
            }
        }

        /// <summary>
        /// A purchase's receipt, written out the same way the first time and on every replay
        /// </summary>
        private sealed class Stored : Answer
        {
            public Stored(PurchaseReceipt receipt) : base(Results.Content(receipt.ToJson(), "application/json"))
            {
            }
        }
    }
}
