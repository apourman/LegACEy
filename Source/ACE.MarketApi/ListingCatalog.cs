using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;

namespace ACE.MarketApi
{
    /// <summary>
    /// The public catalog: the listings a visitor can see (active, within their lifetime, from sellers who aren't banned),
    /// filtered, sorted with a keyset cursor, and shaped for display
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

            public static Cursor Decode(string text)
            {
                var base64 = text.Replace('-', '+').Replace('_', '/');
                base64 += new string('=', (4 - base64.Length % 4) % 4);

                return JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(base64)) ?? throw new FormatException("empty cursor");
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

        // ---- display

        /// <summary>
        /// A listing ready to display: a browse row, and the listing page's basic facts (name, type, material, listed time, price, seller, wield)
        /// </summary>
        public static object View(Row row) => new
        {
            id = row.Listing.Id,
            itemGuid = row.Listing.ItemGuid,
            wcid = row.Item.Wcid,
            name = row.Item.Name,
            itemType = ItemTypeName(row.Item.ItemType),
            material = MaterialName(row.Item.MaterialType),
            workmanship = row.Item.Workmanship,
            level = row.Item.WieldRequirements == (int)WieldRequirement.Level ? row.Item.WieldDifficulty : null,
            arcaneLore = row.Item.ArcaneLore,
            quantity = row.Item.StackSize,
            price = row.Listing.Price,
            seller = row.Seller,
            listedTime = DateTime.SpecifyKind(row.Listing.CreatedTime, DateTimeKind.Utc),
            wield = WieldText(row.Item),
            icon = new
            {
                underlay = row.Item.IconUnderlay,
                icon = row.Item.Icon,
                overlay = row.Item.IconOverlay,
                overlaySecondary = row.Item.IconOverlaySecondary,
                uiEffects = row.Item.UiEffects,
                paletteTemplate = row.Item.PaletteTemplate,
                clothingBase = row.Item.ClothingBase,
            },
        };

        /// <summary>
        /// The raw ItemType name, as the reference site shows it ("MeleeWeapon")
        /// </summary>
        public static string ItemTypeName(int itemType) => ((ItemType)unchecked((uint)itemType)).ToString();

        /// <summary>
        /// "White Sapphire", or null when the item has no material
        /// </summary>
        public static string MaterialName(int? materialType)
        {
            if (materialType is not int value || value == 0)
                return null;

            var material = (MaterialType)value;

            return Enum.IsDefined(material) ? SplitWords(material.ToString()) : value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// "Level 150" or "Missile Weapons 390"; null when the item has no level or skill requirement
        /// </summary>
        public static string WieldText(VaultItem item)
        {
            if (item.WieldDifficulty is not int difficulty)
                return null;

            switch ((WieldRequirement)(item.WieldRequirements ?? 0))
            {
                case WieldRequirement.Level:
                    return $"Level {difficulty}";

                case WieldRequirement.Skill:
                case WieldRequirement.RawSkill:
                    if (item.WieldSkillType is not int skill)
                        return null;
                    return $"{((Skill)skill).ToSentence()} {difficulty}";

                default:
                    return null;
            }
        }

        private static string SplitWords(string name) => Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
    }
}
