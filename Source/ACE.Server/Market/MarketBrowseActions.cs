using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using log4net;

using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Server.ClientChannel;

namespace ACE.Server.Market
{
    /// <summary>
    /// The market window's browse actions on the in-band server channel: the catalog the portal browses, through the shared ListingCatalog, so a query
    /// returns what the portal returns for it. Every action needs the market station and is refused while market_enabled is off.
    /// Bodies are written with ChannelWire; the LegACEy Market plugin's MarketBrowseProtocol reads them.
    /// </summary>
    public static class MarketBrowseActions
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string Browse = "market.browse";
        public const string Suggest = "market.suggest";
        public const string Facets = "market.facets";
        public const string Detail = "market.detail";

        public const int MaxPageSize = 100;

        /// <summary>What a player is told about a listing that is no longer for sale: the detail's refusal, and a buy's</summary>
        internal const string GoneMessage = "That listing is no longer for sale.";

        /// <summary>A listing's stat that the listing doesn't have, on the wire</summary>
        public const int NoStat = -1;

        /// <summary>How a filter kind takes its value: text, a number minimum, a number range or one of the facets' choices</summary>
        public enum FilterShape : byte { Text = 0, Minimum = 1, Range = 2, Choice = 3 }

        /// <param name="Key">the name the window sends the filter under</param>
        /// <param name="Label">what the window shows on the chip and in the Add filter menu</param>
        /// <param name="Query">the portal's query parameter(s) the filter maps to</param>
        public sealed record FilterKind(string Key, string Label, FilterShape Shape, string Query);

        /// <summary>
        /// The filters the window offers, in menu order. A kind added here (with its filter in <see cref="ReadFilters"/>) appears in the window.
        /// </summary>
        public static readonly IReadOnlyList<FilterKind> FilterKinds = new[]
        {
            new FilterKind("name", "Name", FilterShape.Text, "q"),
            new FilterKind("category", "Category", FilterShape.Choice, "type"),
            new FilterKind("price", "Price", FilterShape.Range, "minPrice,maxPrice"),
            new FilterKind("seller", "Seller", FilterShape.Text, "seller"),
            new FilterKind("level", "Wield level", FilterShape.Minimum, "minLevel"),
            new FilterKind("workmanship", "Workmanship", FilterShape.Minimum, "minWorkmanship"),
            new FilterKind("arcane", "Arcane lore", FilterShape.Minimum, "minArcane"),
        };

        /// <summary>The filters a browse request can carry: a count, then each filter's key, low and high values</summary>
        public const int MaxFilters = 16;

        public static void Register()
        {
            ServerChannel.Register(Browse, HandleBrowse, MarketChannelActions.Station);
            ServerChannel.Register(Suggest, HandleSuggest, MarketChannelActions.Station);
            ServerChannel.Register(Facets, HandleFacets, MarketChannelActions.Station);
            ServerChannel.Register(Detail, HandleDetail, MarketChannelActions.Station);
        }

        /// <summary>
        /// One page of visible listings, after the filters, in the sort, from the cursor. Body: sort, descending (byte), cursor ("" for the first page),
        /// limit, filter count, then each filter's key, low and high. Reply: count, each listing (see WriteListing), next cursor ("" on the last page).
        /// </summary>
        private static void HandleBrowse(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context) || !TryReadBrowse(context, out var request))
                return;

            var viewer = context.Player.Character.AccountId;
            var now = DateTime.UtcNow;

            Answer(context, () =>
            {
                using var shard = MarketChannelActions.Database.CreateShard();

                var rows = ListingCatalog.Filter(ListingCatalog.VisibleNow(shard, MarketChannelActions.Database, now), request.Filters);
                if (!ListingCatalog.TryPage(rows, request.Sort.Value, request.Descending, request.After, request.Limit, out var page, out var nextCursor))
                {
                    context.Fail(ChannelStatus.BadRequest, "That page is not from this search. Start again from the first page.");
                    return;
                }

                var items = AppraisalItem.Load(shard, PortalAppraisalNames.Instance, page.Select(r => (uint)r.Listing.ItemGuid).ToList());

                context.Reply(ChannelWire.Body(w =>
                {
                    w.Write(page.Count);
                    foreach (var row in page)
                        WriteListing(w, row, items[(uint)row.Listing.ItemGuid], viewer);
                    ChannelWire.WriteString(w, nextCursor ?? string.Empty);
                }));
            });
        }

        /// <summary>
        /// Up to 10 distinct names of visible listings that start with the text. Body: the text. Reply: count, then each name.
        /// </summary>
        private static void HandleSuggest(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context))
                return;

            string text;
            try
            {
                using (var body = context.Body())
                    text = ChannelWire.ReadString(body).Trim();
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, "Some text is required.");
                return;
            }

            var now = DateTime.UtcNow;

            Answer(context, () =>
            {
                using var shard = MarketChannelActions.Database.CreateShard();

                var names = text.Length == 0 ? new List<string>() : ListingCatalog.Suggest(ListingCatalog.VisibleNow(shard, MarketChannelActions.Database, now), text);

                context.Reply(ChannelWire.Body(w =>
                {
                    w.Write(names.Count);
                    foreach (var name in names)
                        ChannelWire.WriteString(w, name);
                }));
            });
        }

        /// <summary>
        /// What the window draws its menus from: the sort orders and the filter kinds. Reply: count, then each sort (value, label, descending byte);
        /// count, then each filter kind (key, label, shape byte, query, option count, then each option: value, label, count). A choice's options are
        /// the categories on sale with their counts.
        /// </summary>
        private static void HandleFacets(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context))
                return;

            var now = DateTime.UtcNow;

            Answer(context, () =>
            {
                using var shard = MarketChannelActions.Database.CreateShard();

                var categories = ListingCatalog.ItemTypeCounts(ListingCatalog.VisibleNow(shard, MarketChannelActions.Database, now));

                context.Reply(ChannelWire.Body(w =>
                {
                    w.Write(ListingCatalog.Sorts.Count);
                    foreach (var sort in ListingCatalog.Sorts)
                    {
                        ChannelWire.WriteString(w, sort.Value);
                        ChannelWire.WriteString(w, sort.Label);
                        w.Write((byte)(sort.DescendingByDefault ? 1 : 0));
                    }

                    w.Write(FilterKinds.Count);
                    foreach (var kind in FilterKinds)
                    {
                        ChannelWire.WriteString(w, kind.Key);
                        ChannelWire.WriteString(w, kind.Label);
                        w.Write((byte)kind.Shape);
                        ChannelWire.WriteString(w, kind.Query);
                        // a choice's options come from the catalog; each kind carries its own
                        var options = kind.Key == "category" ? categories.Select(c => (Value: c.Name, Label: ListingCatalog.TryParseItemType(c.Name, out var type) ? ListingCatalog.ItemTypeLabel(type) : c.Name, c.Count)).ToList()
                            : new List<(string Value, string Label, int Count)>();
                        w.Write(options.Count);
                        foreach (var option in options)
                        {
                            ChannelWire.WriteString(w, option.Value);
                            ChannelWire.WriteString(w, option.Label);
                            w.Write(option.Count);
                        }
                    }
                }));
            });
        }

        /// <summary>
        /// One listing with its appraisal lines and spells, as the portal's listing page shows it. Body: the listing id. Reply: the listing (see
        /// WriteListing), the line count and lines, the spell count and each spell (name, cantrip byte). Refused once the listing is no longer for sale.
        /// </summary>
        private static void HandleDetail(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context))
                return;

            long id;
            try
            {
                using (var body = context.Body())
                    id = body.ReadInt64();
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, "A listing id is required.");
                return;
            }

            var viewer = context.Player.Character.AccountId;
            var now = DateTime.UtcNow;

            Answer(context, () =>
            {
                using var shard = MarketChannelActions.Database.CreateShard();

                var row = ListingCatalog.VisibleNow(shard, MarketChannelActions.Database, now).FirstOrDefault(r => r.Listing.Id == id);
                if (row == null)
                {
                    context.Fail(ChannelStatus.BadRequest, GoneMessage);
                    return;
                }

                var item = AppraisalItem.Load(shard, PortalAppraisalNames.Instance, new[] { (uint)row.Listing.ItemGuid })[(uint)row.Listing.ItemGuid];
                var lines = AppraisalRules.Default.Format(item);
                var spells = AppraisalRules.Spells(item);

                context.Reply(ChannelWire.Body(w =>
                {
                    WriteListing(w, row, item, viewer);
                    w.Write(lines.Count);
                    foreach (var line in lines)
                        ChannelWire.WriteString(w, line);
                    w.Write(spells.Count);
                    foreach (var spell in spells)
                    {
                        ChannelWire.WriteString(w, spell.Name);
                        w.Write((byte)(spell.Cantrip ? 1 : 0));
                    }
                }));
            });
        }

        /// <summary>
        /// A listing's card facts, in order: id (int64), item guid (uint32), name, type, material, workmanship, level, arcane lore (int32 each, -1 for none),
        /// summary, stack size (int32), price (int64), seller, listed time (unix seconds, int64), your listing (byte), plate, underlay, icon, overlay,
        /// overlay secondary, ui effects (uint32 each).
        /// </summary>
        internal static void WriteListing(System.IO.BinaryWriter w, ListingCatalog.Row row, AppraisalItem item, uint viewerAccountId)
        {
            w.Write(row.Listing.Id);
            w.Write((uint)row.Listing.ItemGuid);
            ChannelWire.WriteString(w, row.Item.Name);
            ChannelWire.WriteString(w, ListingCatalog.ItemTypeName(row.Item.ItemType));
            ChannelWire.WriteString(w, ListingCatalog.ItemTypeLabel(row.Item.ItemType));
            ChannelWire.WriteString(w, AppraisalRules.MaterialName(item, row.Item.MaterialType) ?? string.Empty);
            w.Write(row.Item.Workmanship ?? NoStat);
            w.Write(ListingCatalog.LevelOf(row.Item) ?? NoStat);
            w.Write(row.Item.ArcaneLore ?? NoStat);
            ChannelWire.WriteString(w, AppraisalRules.Summary(item) ?? string.Empty);
            w.Write(row.Item.StackSize);
            w.Write(row.Listing.Price);
            ChannelWire.WriteString(w, row.Seller ?? string.Empty);
            w.Write(MarketSellActions.UnixSeconds(row.Listing.CreatedTime));
            w.Write((byte)(row.Listing.SellerAccountId == viewerAccountId ? 1 : 0));
            w.Write(ListingCatalog.Plate(row.Item.ItemType));
            w.Write(row.Item.IconUnderlay ?? 0);
            w.Write(row.Item.Icon ?? 0);
            w.Write(row.Item.IconOverlay ?? 0);
            w.Write(row.Item.IconOverlaySecondary ?? 0);
            w.Write(unchecked((uint)(row.Item.UiEffects ?? 0)));
        }

        /// <summary>Runs a catalog read off the world thread; a read that throws is logged and answered as a failure</summary>
        private static void Answer(ChannelContext context, Action read)
        {
            Task.Run(() =>
            {
                try
                {
                    read();
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] Channel {context.Request.Action} for {context.Player.Name} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "The market could not be read.");
                }
            });
        }

        private sealed record BrowseRequest(ListingCatalog.SortOption Sort, bool Descending, ListingCatalog.Cursor After, int Limit, ListingCatalog.Filters Filters);

        private static bool TryReadBrowse(ChannelContext context, out BrowseRequest request)
        {
            request = null;

            try
            {
                using var body = context.Body();

                var sortValue = ChannelWire.ReadString(body);
                var descending = body.ReadByte() != 0;
                var cursorText = ChannelWire.ReadString(body);
                var limit = body.ReadInt32();
                var count = body.ReadInt32();

                if (count < 0 || count > MaxFilters)
                    return BadRequest(context, $"Choose at most {MaxFilters} filters.");

                var entries = new List<(string Key, string Low, string High)>(count);
                for (var index = 0; index < count; index++)
                    entries.Add((ChannelWire.ReadString(body), ChannelWire.ReadString(body), ChannelWire.ReadString(body)));

                var sort = ListingCatalog.Sorts.FirstOrDefault(s => s.Value == sortValue);
                if (sort == null)
                    return BadRequest(context, "Choose a sort from the list.");

                if (limit < 1 || limit > MaxPageSize)
                    return BadRequest(context, $"A page holds between 1 and {MaxPageSize} listings.");

                ListingCatalog.Cursor after = null;
                if (cursorText.Length > 0 && !ListingCatalog.Cursor.TryDecode(cursorText, out after))
                    return BadRequest(context, "That page is not from this search. Start again from the first page.");

                if (!TryReadFilters(entries, out var filters, out var problem))
                    return BadRequest(context, problem);

                request = new BrowseRequest(sort, descending, after, limit, filters);
                return true;
            }
            catch (EndOfStreamException)
            {
                return BadRequest(context, "A sort, a cursor, a page size and the filters are required.");
            }
        }

        /// <summary>
        /// The shared catalog's filters from the window's (key, low, high) entries. A blank value means no filter. False, with the reason, for a key the
        /// market doesn't have or a value it can't read.
        /// </summary>
        private static bool TryReadFilters(IEnumerable<(string Key, string Low, string High)> entries, out ListingCatalog.Filters filters, out string problem)
        {
            string text = null, seller = null;
            int? type = null, level = null, workmanship = null, arcane = null;
            long? minPrice = null, maxPrice = null;
            problem = null;
            filters = null;

            foreach (var (key, low, high) in entries)
            {
                switch (key)
                {
                    case "name":
                        text = Trimmed(low);
                        break;

                    case "category":
                        if (Trimmed(low) is string category)
                        {
                            if (!ListingCatalog.TryParseItemType(category, out var parsed))
                            {
                                problem = $"There is no category called {category}.";
                                return false;
                            }
                            type = parsed;
                        }
                        break;

                    case "price":
                        if (!TryNumber(low, out minPrice) || !TryNumber(high, out maxPrice))
                        {
                            problem = "Prices are whole MMD.";
                            return false;
                        }
                        break;

                    case "seller":
                        seller = Trimmed(low);
                        break;

                    case "level":
                        if (!TryMinimum(low, out level))
                        {
                            problem = "A wield level is a whole number.";
                            return false;
                        }
                        break;

                    case "workmanship":
                        if (!TryMinimum(low, out workmanship))
                        {
                            problem = "Workmanship is a whole number.";
                            return false;
                        }
                        break;

                    case "arcane":
                        if (!TryMinimum(low, out arcane))
                        {
                            problem = "Arcane lore is a whole number.";
                            return false;
                        }
                        break;

                    default:
                        problem = $"There is no filter called {key}.";
                        return false;
                }
            }

            filters = new ListingCatalog.Filters(text, type, minPrice, maxPrice, seller, level, workmanship, arcane);
            return true;
        }

        private static string Trimmed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static bool TryNumber(string text, out long? value)
        {
            value = null;
            if (Trimmed(text) is not string trimmed)
                return true;
            if (!long.TryParse(trimmed, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return false;
            value = parsed;
            return true;
        }

        private static bool TryMinimum(string text, out int? value)
        {
            value = null;
            if (Trimmed(text) is not string trimmed)
                return true;
            if (!int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return false;
            value = parsed;
            return true;
        }

        private static bool BadRequest(ChannelContext context, string message)
        {
            context.Fail(ChannelStatus.BadRequest, message);
            return false;
        }

    }
}
