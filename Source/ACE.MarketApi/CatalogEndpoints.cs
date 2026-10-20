using System;
using System.Globalization;
using System.Linq;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// The public catalog: browse, name suggestions, dropdown values and the listing page. No sign-in.
    /// Each request first expires overdue listings and notices bans itself (returning banned sellers' listings), so what it shows is current.
    /// </summary>
    public static class CatalogEndpoints
    {
        public const int DefaultPageSize = 50;

        public const int MaxPageSize = 100;

        /// <summary>
        /// Browse's query parameters: their names are the keys Browse reads, and the document lists them with these types. Browse parses each value itself.
        /// </summary>
        public sealed record BrowseQuery(string Q, string Type, long? MinPrice, long? MaxPrice, string Seller, string Sort, string Dir, int? Limit, string Cursor);

        public sealed record SuggestQuery(string Q);

        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/listings", Browse).Json<BrowseResponse>(200, 400).Query<BrowseQuery>();
            app.MapGet("/listings/suggest", Suggest).Json<SuggestionsResponse>().Query<SuggestQuery>();
            app.MapGet("/listings/{id:long}", Detail).Json<ListingDetailResponse>(200, 404);
            app.MapGet("/facets", Facets).Json<FacetsResponse>();
        }

        /// <summary>
        /// Filters: q (part of the name), type (ItemType name or number), minPrice, maxPrice, seller (character name).
        /// Sort: sort (newest, name, workmanship, level, arcane, price, seller) and dir (asc, desc). Paging: limit and the previous page's nextCursor.
        /// </summary>
        private static IResult Browse(HttpRequest request, MarketDatabase database, GameData gameData, TimeProvider time)
        {
            var query = request.Query;

            var sortValue = MarketHttp.QueryValue(query[MarketHttp.QueryName(nameof(BrowseQuery.Sort))]) ?? "newest";
            var sort = ListingCatalog.Sorts.FirstOrDefault(s => s.Value == sortValue);
            if (sort == null)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_sort");

            bool descending;
            switch (MarketHttp.QueryValue(query[MarketHttp.QueryName(nameof(BrowseQuery.Dir))]))
            {
                case null: descending = sort.DescendingByDefault; break;
                case "asc": descending = false; break;
                case "desc": descending = true; break;
                default: return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_sort");
            }

            var limit = DefaultPageSize;
            if (MarketHttp.QueryValue(query[MarketHttp.QueryName(nameof(BrowseQuery.Limit))]) is string limitText && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_limit");
            limit = Math.Min(limit, MaxPageSize);

            if (!TryPrice(query[MarketHttp.QueryName(nameof(BrowseQuery.MinPrice))], out var minPrice) || !TryPrice(query[MarketHttp.QueryName(nameof(BrowseQuery.MaxPrice))], out var maxPrice))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_price");

            int? itemType = null;
            if (MarketHttp.QueryValue(query[MarketHttp.QueryName(nameof(BrowseQuery.Type))]) is string typeText)
            {
                if (!ListingCatalog.TryParseItemType(typeText, out var parsed))
                    return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_type");
                itemType = parsed;
            }

            ListingCatalog.Cursor after = null;
            if (MarketHttp.QueryValue(query[MarketHttp.QueryName(nameof(BrowseQuery.Cursor))]) is string cursorText)
            {
                if (!ListingCatalog.Cursor.TryDecode(cursorText, out after) || after.Sort != sort.Value || after.Descending != descending)
                    return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_cursor");
            }

            using var shard = database.CreateShard();

            var filters = new ListingCatalog.Filters(
                MarketHttp.QueryValue(query[MarketHttp.QueryName(nameof(BrowseQuery.Q))]),
                itemType,
                minPrice,
                maxPrice,
                MarketHttp.QueryValue(query[MarketHttp.QueryName(nameof(BrowseQuery.Seller))]));

            var rows = ListingCatalog.Filter(ListingCatalog.VisibleNow(shard, database, time.GetUtcNow().UtcDateTime), filters);

            if (!ListingCatalog.TryPage(rows, sort.Value, descending, after, limit, out var page, out var nextCursor))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_cursor");

            var items = AppraisalItem.Load(shard, gameData, page.Select(r => (uint)r.Listing.ItemGuid).ToList());

            return Results.Json(new BrowseResponse(page.Select(r => ApiContractViews.Listing(ListingViews.View(r, items[(uint)r.Listing.ItemGuid], gameData))).ToList(), nextCursor));
        }

        /// <summary>
        /// Up to 10 distinct names of visible listings that start with q
        /// </summary>
        private static IResult Suggest(HttpRequest request, MarketDatabase database, TimeProvider time)
        {
            var text = MarketHttp.QueryValue(request.Query[MarketHttp.QueryName(nameof(SuggestQuery.Q))]);

            if (text == null)
                return Results.Json(new SuggestionsResponse(Array.Empty<string>()));

            using var shard = database.CreateShard();

            return Results.Json(new SuggestionsResponse(ListingCatalog.Suggest(ListingCatalog.VisibleNow(shard, database, time.GetUtcNow().UtcDateTime), text)));
        }

        /// <summary>
        /// The listing page (basic facts, appraisal lines and spells), or 404 when the listing isn't active (sold, delisted, expired, returned, unknown, or its seller is banned)
        /// </summary>
        private static IResult Detail(long id, MarketDatabase database, GameData gameData, AppraisalRules rules, TimeProvider time)
        {
            using var shard = database.CreateShard();

            var row = ListingCatalog.VisibleNow(shard, database, time.GetUtcNow().UtcDateTime).FirstOrDefault(r => r.Listing.Id == id);

            if (row == null)
                return MarketHttp.Error(StatusCodes.Status404NotFound, "not_found");

            var item = AppraisalItem.Load(shard, gameData, new[] { (uint)row.Listing.ItemGuid })[(uint)row.Listing.ItemGuid];

            return Results.Json(ApiContractViews.Detail(ListingViews.Detail(row, item, rules, gameData)));
        }

        /// <summary>
        /// Dropdown values: the item types on sale now with their counts, and the sort orders
        /// </summary>
        private static IResult Facets(MarketDatabase database, TimeProvider time)
        {
            using var shard = database.CreateShard();

            var types = ListingCatalog.ItemTypeCounts(ListingCatalog.VisibleNow(shard, database, time.GetUtcNow().UtcDateTime));

            return Results.Json(new FacetsResponse(
                types.Select(t => new FacetItemResponse(t.Name, t.Name, t.Count)).ToList(),
                ListingCatalog.Sorts.Select(s => new SortResponse(s.Value, s.Label, s.DescendingByDefault ? "desc" : "asc")).ToList()));
        }

        /// <summary>
        /// The trimmed query value, or null when it's missing or blank
        /// </summary>
        private static bool TryPrice(StringValues value, out long? price)
        {
            price = null;

            if (MarketHttp.QueryValue(value) is not string text)
                return true;

            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                return false;

            price = parsed;
            return true;
        }
    }
}
