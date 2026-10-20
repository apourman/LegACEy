using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// The signed-in account's money and item history. The history itself is read by ACE.Database.Market.MarketHistory; this is the HTTP adapter.
    /// </summary>
    public static class HistoryEndpoints
    {
        /// <summary>
        /// History's query parameters: their names are the keys History reads, and the document lists them with these types. History parses each value itself.
        /// </summary>
        public sealed record HistoryQuery(long? Since, long? TransfersBefore, long? ItemsBefore, long? ItemsLimit, long? TransfersLimit);

        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/history", History).Json<HistoryResponse>(200, 400).Query<HistoryQuery>().RequireAuthorization();
        }

        /// <summary>
        /// GET /api/history?since={seq}&amp;transfersLimit={n}&amp;itemsBefore={eventId}&amp;itemsLimit={n}
        /// → balance, head (the account's last ledger sequence), one bounded page of its ledger lines,
        /// and a page of its item movements (newest first, paged back by event id).
        /// </summary>
        private static async Task<IResult> History(HttpContext context, MarketDatabase database)
        {
            var accountId = MarketHttp.AccountId(context);
            var query = context.Request.Query;

            if (!TryWhole(query[MarketHttp.QueryName(nameof(HistoryQuery.Since))], 0, out var since) || !TryWhole(query[MarketHttp.QueryName(nameof(HistoryQuery.TransfersBefore))], 1, out var transfersBefore) || !TryWhole(query[MarketHttp.QueryName(nameof(HistoryQuery.ItemsBefore))], 1, out var itemsBefore))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_cursor");

            if (since != null && transfersBefore != null)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_cursor");

            // like the catalog's limit: at least 1, and a larger one is capped
            if (!TryWhole(query[MarketHttp.QueryName(nameof(HistoryQuery.ItemsLimit))], 1, out var itemsLimit))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_limit");

            if (!TryWhole(query[MarketHttp.QueryName(nameof(HistoryQuery.TransfersLimit))], 1, out var transfersLimit))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_limit");

            using var shard = database.CreateShard();

            var page = await MarketHistory.ReadAsync(shard, accountId, since, transfersBefore, itemsBefore, transfersLimit, itemsLimit);

            var transfers = page.Lines.Select(l => new HistoryTransferResponse(l.Sequence, l.TransferId, l.Kind, l.Amount, l.BalanceAfter, l.Time, l.Text, l.Memo)).ToList();
            var items = page.Movements.Select(m => new HistoryItemResponse(m.Id, m.ItemGuid, m.Kind, m.Name, m.ListingId, m.Time, m.Text)).ToList();

            return Results.Json(new HistoryResponse(page.Balance, page.Head, transfers, page.NextTransfersBefore, page.NextSince, page.More, items, page.NextItemsBefore));
        }

        /// <summary>
        /// A whole number at least min, or null when the parameter is absent
        /// </summary>
        private static bool TryWhole(StringValues value, long min, out long? result)
        {
            result = null;

            if (MarketHttp.QueryValue(value) is not string text)
                return true;

            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < min)
                return false;

            result = parsed;
            return true;
        }
    }
}
