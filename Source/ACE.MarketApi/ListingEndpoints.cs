using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// Selling: listing a Vault item and delisting it. Market tables only, one save each (see ListingStore).
    /// </summary>
    public static class ListingEndpoints
    {
        /// <param name="Price">whole MMD for the whole stack; a decimal so that 1.5 is refused as a price rather than as unreadable JSON</param>
        /// <param name="CharacterId">the account's character to list as; the depositing character when left out</param>
        public sealed record ListRequest(uint ItemGuid, decimal? Price, uint? CharacterId);

        public static void Map(WebApplication app)
        {
            app.MapPost("/listings", List).RequireAuthorization();
            app.MapPost("/listings/{id:long}/delist", Delist).RequireAuthorization();
        }

        private static IResult List(ListRequest request, HttpContext context, MarketDatabase database, TimeProvider time)
        {
            if (request == null)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request");

            if (request.Price is not decimal price || price < 1 || price != decimal.Truncate(price) || price > long.MaxValue)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_price");

            var accountId = MarketHttp.AccountId(context);
            var now = time.GetUtcNow().UtcDateTime;

            // expired listings no longer count toward the cap
            MarketUpkeep.Expire(database, now);

            using var shard = database.CreateShard();

            var result = ListingStore.List(shard, accountId, request.ItemGuid, (long)price, request.CharacterId, now, () => database.IsBanned(accountId, now));

            if (result.Outcome != ListingOutcome.Ok)
                return Refusal(result.Outcome);

            var listing = result.Listing;

            return Results.Created($"/listings/{listing.Id}", new
            {
                id = listing.Id,
                itemGuid = listing.ItemGuid,
                price = listing.Price,
                status = listing.Status,
                listedTime = DateTime.SpecifyKind(listing.CreatedTime, DateTimeKind.Utc),
            });
        }

        private static IResult Delist(long id, HttpContext context, MarketDatabase database, TimeProvider time)
        {
            var accountId = MarketHttp.AccountId(context);
            var now = time.GetUtcNow().UtcDateTime;

            // an overdue listing has already expired, so delisting it answers not_active
            MarketUpkeep.Expire(database, now);

            using var shard = database.CreateShard();

            var result = ListingStore.Delist(shard, accountId, id, now, () => database.IsBanned(accountId, now));

            if (result.Outcome != ListingOutcome.Ok)
                return Refusal(result.Outcome);

            return Results.Json(new { id = result.Listing.Id, itemGuid = result.Listing.ItemGuid, status = result.Listing.Status });
        }

        private static IResult Refusal(ListingOutcome outcome) => outcome switch
        {
            ListingOutcome.InvalidPrice => MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_price"),
            ListingOutcome.InvalidCharacter => MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_character"),
            ListingOutcome.NotInVault => MarketHttp.Error(StatusCodes.Status404NotFound, "not_in_vault"),
            ListingOutcome.NotFound => MarketHttp.Error(StatusCodes.Status404NotFound, "not_found"),
            ListingOutcome.NotHeld => MarketHttp.Error(StatusCodes.Status409Conflict, "not_held"),
            ListingOutcome.NotActive => MarketHttp.Error(StatusCodes.Status409Conflict, "not_active"),
            ListingOutcome.ListingLimit => MarketHttp.Error(StatusCodes.Status409Conflict, "listing_limit"),
            ListingOutcome.Banned => MarketHttp.Error(StatusCodes.Status403Forbidden, "banned"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
    }
}
