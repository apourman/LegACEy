using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

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

        public const int MaxSuggestions = 10;

        public static void Map(WebApplication app)
        {
            app.MapGet("/listings", Browse);
            app.MapGet("/listings/suggest", Suggest);
            app.MapGet("/listings/{id:long}", Detail);
            app.MapGet("/facets", Facets);
        }

        /// <summary>
        /// Filters: q (part of the name), type (ItemType name or number), minPrice, maxPrice, seller (character name).
        /// Sort: sort (newest, name, workmanship, level, arcane, price, seller) and dir (asc, desc). Paging: limit and the previous page's nextCursor.
        /// </summary>
        private static IResult Browse(HttpRequest request, MarketDatabase database, TimeProvider time)
        {
            var query = request.Query;

            var sortValue = Text(query["sort"]) ?? "newest";
            var sort = ListingCatalog.Sorts.FirstOrDefault(s => s.Value == sortValue);
            if (sort == null)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_sort");

            bool descending;
            switch (Text(query["dir"]))
            {
                case null: descending = sort.DescendingByDefault; break;
                case "asc": descending = false; break;
                case "desc": descending = true; break;
                default: return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_sort");
            }

            var limit = DefaultPageSize;
            if (Text(query["limit"]) is string limitText && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_limit");
            limit = Math.Min(limit, MaxPageSize);

            if (!TryPrice(query["minPrice"], out var minPrice) || !TryPrice(query["maxPrice"], out var maxPrice))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_price");

            int? itemType = null;
            if (Text(query["type"]) is string typeText)
            {
                if (!ListingCatalog.TryParseItemType(typeText, out var parsed))
                    return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_type");
                itemType = parsed;
            }

            ListingCatalog.Cursor after = null;
            if (Text(query["cursor"]) is string cursorText)
            {
                try
                {
                    after = ListingCatalog.Cursor.Decode(cursorText);
                }
                catch (Exception e) when (e is FormatException || e is JsonException)
                {
                    return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_cursor");
                }

                if (after.Sort != sort.Value || after.Descending != descending)
                    return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_cursor");
            }

            var now = time.GetUtcNow().UtcDateTime;
            var banned = MarketUpkeep.ExpireAndReturnBanned(database, now);

            using var shard = database.CreateShard();

            var rows = ListingCatalog.Visible(shard, now, banned);

            if (Text(query["q"]) is string text)
                rows = ListingCatalog.NameContains(rows, text);
            if (itemType is int type)
                rows = rows.Where(r => r.Item.ItemType == type);
            if (minPrice is long min)
                rows = rows.Where(r => r.Listing.Price >= min);
            if (maxPrice is long max)
                rows = rows.Where(r => r.Listing.Price <= max);
            if (Text(query["seller"]) is string seller)
                rows = rows.Where(r => r.Seller == seller);

            List<ListingCatalog.Row> page;
            Func<ListingCatalog.Row, string> cursorKey;
            try
            {
                page = ListingCatalog.Sort(rows, sort.Value, descending, after, out cursorKey).Take(limit + 1).ToList();
            }
            catch (FormatException)
            {
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_cursor");
            }

            string nextCursor = null;
            if (page.Count > limit)
            {
                page.RemoveAt(limit);
                var last = page[^1];
                nextCursor = new ListingCatalog.Cursor(sort.Value, descending, cursorKey(last), last.Listing.Id).Encode();
            }

            return Results.Json(new { listings = page.Select(ListingCatalog.View), nextCursor });
        }

        /// <summary>
        /// Up to 10 distinct names of visible listings that start with q
        /// </summary>
        private static IResult Suggest(HttpRequest request, MarketDatabase database, TimeProvider time)
        {
            var text = Text(request.Query["q"]);

            if (text == null)
                return Results.Json(new { suggestions = Array.Empty<string>() });

            var now = time.GetUtcNow().UtcDateTime;
            var banned = MarketUpkeep.ExpireAndReturnBanned(database, now);

            using var shard = database.CreateShard();

            var suggestions = ListingCatalog.NameStartsWith(ListingCatalog.Visible(shard, now, banned), text)
                .Select(r => r.Item.Name)
                .Distinct()
                .OrderBy(n => n)
                .Take(MaxSuggestions)
                .ToList();

            return Results.Json(new { suggestions });
        }

        /// <summary>
        /// The listing page's basic facts, or 404 when the listing isn't active (sold, delisted, expired, returned, unknown, or its seller is banned)
        /// </summary>
        private static IResult Detail(long id, MarketDatabase database, TimeProvider time)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var banned = MarketUpkeep.ExpireAndReturnBanned(database, now);

            using var shard = database.CreateShard();

            var row = ListingCatalog.Visible(shard, now, banned).FirstOrDefault(r => r.Listing.Id == id);

            if (row == null)
                return MarketHttp.Error(StatusCodes.Status404NotFound, "not_found");

            return Results.Json(ListingCatalog.View(row));
        }

        /// <summary>
        /// Dropdown values: the item types on sale now with their counts, and the sort orders
        /// </summary>
        private static IResult Facets(MarketDatabase database, TimeProvider time)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var banned = MarketUpkeep.ExpireAndReturnBanned(database, now);

            using var shard = database.CreateShard();

            var types = ListingCatalog.Visible(shard, now, banned)
                .GroupBy(r => r.Item.ItemType)
                .Select(g => new { ItemType = g.Key, Count = g.Count() })
                .ToList()
                .Select(t => new { value = ListingCatalog.ItemTypeName(t.ItemType), label = ListingCatalog.ItemTypeName(t.ItemType), count = t.Count })
                .OrderBy(t => t.label, StringComparer.Ordinal)
                .ToList();

            return Results.Json(new
            {
                itemTypes = types,
                sorts = ListingCatalog.Sorts.Select(s => new { value = s.Value, label = s.Label, defaultDir = s.DescendingByDefault ? "desc" : "asc" }),
            });
        }

        /// <summary>
        /// The trimmed query value, or null when it's missing or blank
        /// </summary>
        private static string Text(StringValues value)
        {
            var text = value.ToString().Trim();

            return text.Length == 0 ? null : text;
        }

        private static bool TryPrice(StringValues value, out long? price)
        {
            price = null;

            if (Text(value) is not string text)
                return true;

            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                return false;

            price = parsed;
            return true;
        }
    }
}
