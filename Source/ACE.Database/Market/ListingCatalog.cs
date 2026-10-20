using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;

namespace ACE.Database.Market
{
    /// <summary>
    /// The public catalog: the listings a visitor can see (active, within their lifetime, from sellers who aren't banned),
    /// filtered, sorted and paged with a keyset cursor. The Market API and the game server both serve it; each shapes the rows for its own display.
    /// </summary>
    public static class ListingCatalog
    {
        /// <summary>
        /// An active listing with its Vault row and the seller character's name
        /// </summary>
        public sealed class Row
        {
            public Listing Listing { get; set; }

            public VaultItem Item { get; set; }

            public string Seller { get; set; }
        }

        public sealed record SortOption(string Value, string Label, bool DescendingByDefault);

        /// <summary>
        /// In display order. Newest, and the "best first" item stats, default to descending.
        /// </summary>
        public static readonly IReadOnlyList<SortOption> Sorts = new[]
        {
            new SortOption("newest", "Newest", true),
            new SortOption("name", "Name", false),
            new SortOption("workmanship", "Workmanship", true),
            new SortOption("level", "Level", true),
            new SortOption("arcane", "Arcane Lore", true),
            new SortOption("price", "Price", false),
            new SortOption("seller", "Seller", false),
        };

        /// <summary>
        /// The listings a visitor can see now
        /// </summary>
        public static IQueryable<Row> Visible(ShardDbContext shard, DateTime now, IReadOnlyCollection<uint> bannedAccounts)
        {
            var cutoff = ListingStore.ExpiryCutoff(shard, now);

            return from l in shard.MarketListings.AsNoTracking()
                   where l.Status == ListingStatus.Active && l.CreatedTime > cutoff && !bannedAccounts.Contains(l.SellerAccountId)
                   join v in shard.MarketVaultItems.AsNoTracking() on l.ItemGuid equals v.ItemGuid
                   join c in shard.Character.AsNoTracking() on l.SellerCharacterId equals c.Id into sellers
                   from c in sellers.DefaultIfEmpty()
                   select new Row { Listing = l, Item = v, Seller = c.Name };
        }

        /// <summary>
        /// Rows whose name contains the text, ignoring case (the column's collation); LIKE wildcards in it are literal
        /// </summary>
        public static IQueryable<Row> NameContains(IQueryable<Row> rows, string text) => rows.Where(r => EF.Functions.Like(r.Item.Name, "%" + EscapeLike(text) + "%", "\\"));

        public static IQueryable<Row> NameStartsWith(IQueryable<Row> rows, string text) => rows.Where(r => EF.Functions.Like(r.Item.Name, EscapeLike(text) + "%", "\\"));

        private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        /// <summary>
        /// An ItemType by name (any case) or number
        /// </summary>
        public static bool TryParseItemType(string text, out int itemType)
        {
            itemType = 0;

            if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                itemType = unchecked((int)number);
                return true;
            }

            if (!text.All(char.IsLetter) || !Enum.TryParse<ItemType>(text, true, out var parsed))
                return false;

            itemType = unchecked((int)(uint)parsed);
            return true;
        }

        // ---- sorting and the cursor

        /// <summary>
        /// Where the previous page ended: the sort, its direction, and the last row's sort key and listing id
        /// </summary>
        public sealed record Cursor(string Sort, bool Descending, string Key, long Id)
        {
            public string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            /// <summary>
            /// False when the text isn't a cursor
            /// </summary>
            public static bool TryDecode(string text, out Cursor cursor)
            {
                var base64 = text.Replace('-', '+').Replace('_', '/');
                base64 += new string('=', (4 - base64.Length % 4) % 4);

                try
                {
                    cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(base64));
                }
                catch (Exception e) when (e is FormatException || e is JsonException || e is ArgumentException)
                {
                    cursor = null;
                }

                return cursor != null;
            }
        }

        /// <summary>
        /// Orders the rows by the sort and listing id, starting after the cursor. Missing stats sort last in either direction.
        /// Throws FormatException if the cursor's key doesn't fit the sort.
        /// </summary>
        /// <param name="cursorKey">gives a row's sort key, for the next page's cursor</param>
        public static IQueryable<Row> Sort(IQueryable<Row> rows, string sort, bool descending, Cursor after, out Func<Row, string> cursorKey)
        {
            // stands in for a missing stat so that it sorts last
            var missing = descending ? int.MinValue : int.MaxValue;
            var level = (int)WieldRequirement.Level;

            return sort switch
            {
                "newest" => Keyset(rows, r => r.Listing.CreatedTime, descending, after, out cursorKey),
                "name" => Keyset(rows, r => r.Item.Name, descending, after, out cursorKey),
                "workmanship" => Keyset(rows, r => r.Item.Workmanship ?? missing, descending, after, out cursorKey),
                "level" => Keyset(rows, r => r.Item.WieldRequirements == level ? r.Item.WieldDifficulty ?? missing : missing, descending, after, out cursorKey),
                "arcane" => Keyset(rows, r => r.Item.ArcaneLore ?? missing, descending, after, out cursorKey),
                "price" => Keyset(rows, r => r.Listing.Price, descending, after, out cursorKey),
                "seller" => Keyset(rows, r => r.Seller ?? "", descending, after, out cursorKey),
                _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
            };
        }

        private static IQueryable<Row> Keyset<TKey>(IQueryable<Row> rows, Expression<Func<Row, TKey>> key, bool descending, Cursor after, out Func<Row, string> cursorKey)
        {
            var compiled = key.Compile();
            cursorKey = row => FormatKey(compiled(row));

            if (after != null)
                rows = rows.Where(After(key, descending, ParseKey<TKey>(after.Key), after.Id));

            return descending
                ? rows.OrderByDescending(key).ThenByDescending(r => r.Listing.Id)
                : rows.OrderBy(key).ThenBy(r => r.Listing.Id);
        }

        /// <summary>
        /// key beyond the cursor's, or equal to it with a listing id beyond the cursor's
        /// </summary>
        private static Expression<Func<Row, bool>> After<TKey>(Expression<Func<Row, TKey>> key, bool descending, TKey afterKey, long afterId)
        {
            var row = key.Parameters[0];

            // through a closure, so the values are SQL parameters rather than literals
            var bounds = new CursorBounds<TKey> { Key = afterKey, Id = afterId };
            var boundKey = Expression.Field(Expression.Constant(bounds), nameof(CursorBounds<TKey>.Key));
            var boundId = Expression.Field(Expression.Constant(bounds), nameof(CursorBounds<TKey>.Id));

            Expression beyond;

            if (typeof(TKey) == typeof(string))
            {
                var compare = Expression.Call(typeof(string).GetMethod(nameof(string.Compare), new[] { typeof(string), typeof(string) }), key.Body, boundKey);
                beyond = descending ? Expression.LessThan(compare, Expression.Constant(0)) : Expression.GreaterThan(compare, Expression.Constant(0));
            }
            else
                beyond = descending ? Expression.LessThan(key.Body, boundKey) : Expression.GreaterThan(key.Body, boundKey);

            var id = Expression.Property(Expression.Property(row, nameof(Row.Listing)), nameof(Listing.Id));
            var idBeyond = descending ? Expression.LessThan(id, boundId) : Expression.GreaterThan(id, boundId);

            var body = Expression.OrElse(beyond, Expression.AndAlso(Expression.Equal(key.Body, boundKey), idBeyond));

            return Expression.Lambda<Func<Row, bool>>(body, row);
        }

        private sealed class CursorBounds<TKey>
        {
            public TKey Key;
            public long Id;
        }

        private static string FormatKey(object key) => key switch
        {
            DateTime time => time.Ticks.ToString(CultureInfo.InvariantCulture),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => (string)key,
        };

        private static TKey ParseKey<TKey>(string text)
        {
            object key;

            if (typeof(TKey) == typeof(DateTime))
                key = new DateTime(long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture));
            else if (typeof(TKey) == typeof(int))
                key = int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            else if (typeof(TKey) == typeof(long))
                key = long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            else
                key = text ?? throw new FormatException("missing cursor key");

            return (TKey)key;
        }

        // ---- what the front doors (the Market API and the game's market actions) call

        public const int MaxSuggestions = 10;

        /// <summary>
        /// The listings a visitor can see, after expiring overdue listings and returning banned sellers' listings
        /// </summary>
        public static IQueryable<Row> VisibleNow(ShardDbContext shard, MarketDatabase database, DateTime now)
        {
            var banned = MarketUpkeep.ExpireAndHandleBans(database, now);

            return Visible(shard, now, banned);
        }

        /// <param name="Text">part of the name</param>
        /// <param name="Seller">the seller character's name</param>
        /// <param name="MinLevel">the least wield level, for an item that needs a level</param>
        /// <param name="MinWorkmanship">the least workmanship</param>
        /// <param name="MinArcane">the least arcane lore</param>
        public sealed record Filters(string Text, int? ItemType, long? MinPrice, long? MaxPrice, string Seller,
            int? MinLevel = null, int? MinWorkmanship = null, int? MinArcane = null);

        /// <summary>
        /// The rows that pass every filter that's set
        /// </summary>
        public static IQueryable<Row> Filter(IQueryable<Row> rows, Filters filters)
        {
            if (filters.Text is string text)
                rows = NameContains(rows, text);
            if (filters.ItemType is int type)
                rows = rows.Where(r => r.Item.ItemType == type);
            if (filters.MinPrice is long min)
                rows = rows.Where(r => r.Listing.Price >= min);
            if (filters.MaxPrice is long max)
                rows = rows.Where(r => r.Listing.Price <= max);
            if (filters.Seller is string seller)
                rows = rows.Where(r => r.Seller == seller);
            if (filters.MinLevel is int level)
            {
                // the same reading as the Level sort: an item with no level requirement has no level to meet
                var wieldLevel = (int)WieldRequirement.Level;
                rows = rows.Where(r => r.Item.WieldRequirements == wieldLevel && r.Item.WieldDifficulty >= level);
            }
            if (filters.MinWorkmanship is int workmanship)
                rows = rows.Where(r => r.Item.Workmanship >= workmanship);
            if (filters.MinArcane is int arcane)
                rows = rows.Where(r => r.Item.ArcaneLore >= arcane);

            return rows;
        }

        /// <summary>
        /// One page of up to limit rows in the sort's order after the cursor, and the next page's cursor (null on the last page).
        /// False when the cursor is from another sort or direction, or its key doesn't fit the sort.
        /// </summary>
        public static bool TryPage(IQueryable<Row> rows, string sort, bool descending, Cursor after, int limit, out List<Row> page, out string nextCursor)
        {
            Func<Row, string> cursorKey;
            page = null;
            nextCursor = null;

            if (after != null && (after.Sort != sort || after.Descending != descending))
                return false;

            try
            {
                page = Sort(rows, sort, descending, after, out cursorKey).Take(limit + 1).ToList();
            }
            catch (Exception e) when (e is FormatException || e is OverflowException || e is ArgumentOutOfRangeException)
            {
                // a cursor key that doesn't fit the sort's type
                return false;
            }

            if (page.Count > limit)
            {
                page.RemoveAt(limit);
                var last = page[^1];
                nextCursor = new Cursor(sort, descending, cursorKey(last), last.Listing.Id).Encode();
            }

            return true;
        }

        /// <summary>
        /// Up to MaxSuggestions distinct names of the rows that start with the text, in order
        /// </summary>
        public static List<string> Suggest(IQueryable<Row> rows, string text) => NameStartsWith(rows, text)
            .Select(r => r.Item.Name)
            .Distinct()
            .OrderBy(n => n)
            .Take(MaxSuggestions)
            .ToList();

        /// <summary>
        /// The item types among the rows with their counts, by name
        /// </summary>
        public static List<(string Name, int Count)> ItemTypeCounts(IQueryable<Row> rows) => rows
            .GroupBy(r => r.Item.ItemType)
            .Select(g => new { ItemType = g.Key, Count = g.Count() })
            .ToList()
            .Select(t => (Name: ItemTypeName(t.ItemType), t.Count))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        /// <summary>
        /// The raw ItemType name, as the reference site shows it ("MeleeWeapon")
        /// </summary>
        public static string ItemTypeName(int itemType) => ((ItemType)unchecked((uint)itemType)).ToString();

        /// <summary>
        /// The item type as a label: "MeleeWeapon" → "Melee Weapon"
        /// </summary>
        public static string ItemTypeLabel(int itemType) => Regex.Replace(ItemTypeName(itemType), "(?<=[a-z])(?=[A-Z])", " ");

        /// <summary>
        /// The wield level of a stored item, when it has a level requirement. For rows already read; the catalog's queries use the same rule inline.
        /// </summary>
        public static int? LevelOf(VaultItem item) => item.WieldRequirements == (int)WieldRequirement.Level ? item.WieldDifficulty : null;

        /// <summary>
        /// The plate the client draws under an icon, by item type: the retail item-type plate (weapon, armor, clothing, jewelry, gem, or the plain one).
        /// </summary>
        public static uint Plate(int itemType)
        {
            var type = (ItemType)unchecked((uint)itemType);

            if ((type & ItemType.WeaponOrCaster) != 0)
                return 0x060011D2;
            if ((type & ItemType.Armor) != 0)
                return 0x060011CF;
            if ((type & ItemType.Clothing) != 0)
                return 0x060011F3;
            if ((type & ItemType.Jewelry) != 0)
                return 0x060011D5;
            if ((type & ItemType.Gem) != 0)
                return 0x060011D3;

            return 0x060011D4;
        }
    }
}
