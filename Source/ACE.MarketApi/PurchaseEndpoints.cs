using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// Buying a listing: the request's shape is checked here, and the purchase rules (PurchaseRules) are the ones the game server's market actions share
    /// </summary>
    public static class PurchaseEndpoints
    {
        /// <param name="Count">must equal the whole stack</param>
        /// <param name="ExpectedPrice">the price the buyer saw; a decimal so that 1.5 is refused as a price rather than as unreadable JSON</param>
        /// <param name="IdempotencyKey">the buyer's key for this request (up to 64 characters): a repeat returns the first answer</param>
        /// <param name="CharacterId">the buyer's character the Vault row names; the account's first character when left out</param>
        public sealed record PurchaseRequest(int? Count, decimal? ExpectedPrice, string IdempotencyKey, uint? CharacterId);

        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/listings/{id:long}/purchase", Purchase).Json<PurchaseReceipt>(200, 400, 403, 404, 409, 410, 429, 500, 503).RequireAuthorization();
        }

        private static IResult Purchase(long id, PurchaseRequest request, HttpContext context, MarketDatabase database, TimeProvider time, IMarketPause pause, IFeePolicy feePolicy, ILoggerFactory loggers)
        {
            if (request == null || !PurchaseStore.IsIdempotencyKey(request.IdempotencyKey))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request");

            if (!MarketHttp.TryWholeMmd(request.ExpectedPrice, out var expected))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_price");

            if (request.Count is not int count || count < 1)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_count");

            var result = PurchaseRules.Buy(database, pause, feePolicy, id, MarketHttp.AccountId(context), request.CharacterId, expected, count, request.IdempotencyKey, time.GetUtcNow().UtcDateTime);

            if (result.Problem != null)
                loggers.CreateLogger(typeof(PurchaseEndpoints).FullName).LogError("{Problem}", result.Problem);

            return result.Outcome switch
            {
                // a purchase's receipt, written out the same way the first time and on every replay
                BuyOutcome.Ok => Results.Content(result.Receipt.ToJson(), "application/json"),
                BuyOutcome.InvalidKey => MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request"),
                BuyOutcome.InvalidCharacter => MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_character"),
                BuyOutcome.InvalidCount => MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_count"),
                BuyOutcome.OwnListing => MarketHttp.Error(StatusCodes.Status403Forbidden, "own_listing"),
                BuyOutcome.Banned => MarketHttp.Error(StatusCodes.Status403Forbidden, "banned"),
                BuyOutcome.KeyReused => MarketHttp.Error(StatusCodes.Status409Conflict, "key_reused"),
                BuyOutcome.PriceChanged => Results.Json(new ApiError("price_changed", result.Price), statusCode: StatusCodes.Status409Conflict),
                BuyOutcome.InsufficientFunds => MarketHttp.Error(StatusCodes.Status409Conflict, "insufficient_funds"),
                BuyOutcome.Gone => MarketHttp.Error(StatusCodes.Status410Gone, "gone"),
                BuyOutcome.RateLimited => MarketHttp.Error(StatusCodes.Status429TooManyRequests, "rate_limited"),
                BuyOutcome.InvalidFee => MarketHttp.Error(StatusCodes.Status500InternalServerError, "invalid_fee"),
                BuyOutcome.Paused => MarketHttp.Error(StatusCodes.Status503ServiceUnavailable, "paused"),
                BuyOutcome.Busy => MarketHttp.Error(StatusCodes.Status503ServiceUnavailable, "busy"),
                _ => throw new ArgumentOutOfRangeException(nameof(result.Outcome), result.Outcome, null),
            };
        }
    }
}
