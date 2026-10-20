using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Plugin.Market;

namespace LegACEy.Client.Tests;

/// <summary>
/// An in-process stand-in for ACE's channel: market.account, the market browse actions and their catalog, the Sell screen's actions with the
/// Vault's page (market.vault), and the Activity actions (history and withdrawing notes). It decodes the real request payloads and answers with real
/// reply payloads, so the client channel is exercised end to end. Replies wait in a queue until Pump. The state is the server's: the pack and
/// Vault items, the account's listings, which the actions change, and the ledger and movements the history reads. The browse catalog is the
/// test's own: the fake filters, sorts and pages it the way the server's catalog does, so a test asserts what the window shows for a search.
/// </summary>
public sealed class FakeMarketServer : IServerChannelTransport
{
    private static readonly SortOption[] Sorts =
    {
        new("newest", "Newest", true), new("name", "Name", false), new("workmanship", "Workmanship", true), new("level", "Level", true),
        new("arcane", "Arcane Lore", true), new("price", "Price", false), new("seller", "Seller", false),
    };

    private sealed record Kind(string Key, string Label, FilterShape Shape, string Query);

    private static readonly Kind[] Kinds =
    {
        new("name", "Name", FilterShape.Text, "q"), new("category", "Category", FilterShape.Choice, "type"),
        new("price", "Price", FilterShape.Range, "minPrice,maxPrice"), new("seller", "Seller", FilterShape.Text, "seller"),
        new("level", "Wield level", FilterShape.Minimum, "minLevel"), new("workmanship", "Workmanship", FilterShape.Minimum, "minWorkmanship"),
        new("arcane", "Arcane lore", FilterShape.Minimum, "minArcane"),
    };

    private readonly Queue<(DateTime Due, byte[] Payload)> _outbox = new();
    private readonly Func<DateTime> _clock;
    private long _nextListingId = 100;

    public FakeMarketServer(Func<DateTime> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Action<byte[]>? Deliver { get; set; }
    public bool IsAvailable { get; set; } = true;
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(30);
    /// <summary>The account the server answers market.account with. A withdrawal that moves notes takes its MMD off the balance.</summary>
    public MarketAccount Account { get; set; } = new(1250, false, false, 12, 1000, 3, 200, "Arwic Wanderer");
    /// <summary>When set, market.account is refused with this plain message, as a server with market_enabled off answers.</summary>
    public string? Refusal { get; set; }
    /// <summary>The listings on sale. The browse window's searches run over these.</summary>
    public List<ListingCard> Catalog { get; } = SampleListings().ToList();
    /// <summary>When set, every browse request gets this failure instead of a page.</summary>
    public ChannelStatus? BrowseStatus { get; set; }
    /// <summary>When set, every facets request gets this failure instead of the menus.</summary>
    public ChannelStatus? FacetsStatus { get; set; }
    /// <summary>When set, a withdrawal is refused with this message and nothing moves, as a full pack or a paused market answers.</summary>
    public string? WithdrawRefusal { get; set; }
    /// <summary>The ledger the history pages come from, oldest first.</summary>
    public List<LedgerLine> Ledger { get; } = new();
    /// <summary>The item movements the history pages come from, oldest first.</summary>
    public List<MovementLine> Movements { get; } = new();
    /// <summary>The MMD earned and spent this week, as the ledger sums them.</summary>
    public long EarnedThisWeek { get; set; }
    public long SpentThisWeek { get; set; }
    public int ItemsSold { get; set; }
    public int ItemsBought { get; set; }
    /// <summary>The cursors each history request asked for, in order.</summary>
    public List<(long Lines, long Movements)> HistoryRequests { get; } = new();
    /// <summary>The amounts withdrawals asked for, in order.</summary>
    public List<long> Withdrawals { get; } = new();
    /// <summary>The actions the server lists in its hello: every action it registers.</summary>
    public IReadOnlyList<string> RegisteredActions { get; set; } = new[]
    {
        ChannelHello.Action, MarketProtocol.Account, MarketProtocol.Sellable, MarketProtocol.List, MarketProtocol.Delist, MarketProtocol.Listings,
        MarketProtocol.VaultList, MarketProtocol.History, MarketProtocol.WithdrawNotes, MarketProtocol.Reprice,
        MarketBrowseProtocol.Browse, MarketBrowseProtocol.Suggest, MarketBrowseProtocol.Facets, MarketBrowseProtocol.Detail,
        MarketBrowseProtocol.Buy, MarketBrowseProtocol.Fits,
    };
    public List<string> Received { get; } = new();
    /// <summary>Every browse request, decoded as the server reads it</summary>
    public List<(string Sort, bool Descending, string Cursor, int Limit, IReadOnlyList<ActiveFilter> Filters)> BrowseRequests { get; } = new();
    /// <summary>The text of every tell the window sent through the chat, as the server would see it</summary>
    public List<string> Chat { get; } = new();

    /// <summary>The listing lifetime the server's settings give, in days. The Sell screen shows this, not a fixed figure.</summary>
    public int LifetimeDays { get; set; } = 14;
    /// <summary>The pack's items, as the server knows them: market.list deposits one of these into the Vault.</summary>
    public Dictionary<uint, SellVaultItem> Pack { get; } = new();
    /// <summary>The Vault's items, in the Vault's order. A listed one has the state "listed".</summary>
    public List<SellVaultItem> Vault { get; } = new();
    /// <summary>The account's listings of every status, as the listings tabs read them.</summary>
    public List<FakeListing> Listings { get; } = new();
    /// <summary>The reasons market.sellable gives for items that can't be listed, by guid. Other items are sellable.</summary>
    public Dictionary<uint, string> Unsellable { get; } = new();
    /// <summary>When set, market.list is refused with this message after any deposit, as the listing store refuses it.</summary>
    public string? ListRefusal { get; set; }
    /// <summary>When set, market.reprice is refused with this message and the price stays, as the listing store refuses it.</summary>
    public string? RepriceRefusal { get; set; }
    /// <summary>The last market.reprice the server decoded: the listing and the new price.</summary>
    public (long Id, long Price)? LastReprice { get; private set; }

    /// <summary>The last market.list the server decoded: the item and the price.</summary>
    public (uint Guid, long Price)? LastList { get; private set; }
    /// <summary>The last market.delist the server decoded: the listing id.</summary>
    public long? LastDelist { get; private set; }
    /// <summary>The last market.listings the server decoded: the tab and the most to show.</summary>
    public (byte Tab, int Count)? LastListings { get; private set; }

    /// <summary>What market.fits answers: whether the item would go into the pack, the free slots, and why not.</summary>
    public PackFit Fit { get; set; } = new(true, 38, 102, string.Empty);
    /// <summary>When set, a purchase to the pack goes through but the move into the pack is refused with this reason, as a pack that filled meanwhile.</summary>
    public string? PackMoveRefusal { get; set; }
    /// <summary>When set, market.buy is refused with this code, message and new price, and nothing moves. A "gone" refusal takes the listing off sale.</summary>
    public BuyReply? BuyRefusal { get; set; }
    /// <summary>Every market.buy the server decoded: the listing, the price, the count, the key and the delivery.</summary>
    public List<(long Id, long Price, int Count, string Key, bool ToPack)> BuyRequests { get; } = new();
    // the first answer to each idempotency key, as the purchase rules replay it
    private readonly Dictionary<string, BuyReply> _purchases = new();

    public bool Send(byte[] requestPayload)
    {
        if (!ChannelWire.TryDecodeRequest(requestPayload, out var id, out var action, out var body)) return false;
        Received.Add(action);
        switch (action)
        {
            case ChannelHello.Action:
                Reply(id, action, ChannelStatus.Ok, ChannelHello.Write("Arwic Wanderer", RegisteredActions));
                break;
            case MarketProtocol.Account when Refusal != null:
                Reply(id, action, ChannelStatus.Unavailable, ChannelWire.Body(w => ChannelWire.WriteString(w, Refusal)));
                break;
            case MarketProtocol.Account:
                Reply(id, action, ChannelStatus.Ok, MarketProtocol.WriteAccount(Account));
                break;
            case MarketProtocol.VaultList:
                Reply(id, action, ChannelStatus.Ok, VaultPage(body));
                break;
            case MarketProtocol.Sellable:
                Reply(id, action, ChannelStatus.Ok, Sellable(body));
                break;
            case MarketProtocol.List:
                Reply(id, action, ChannelStatus.Ok, List(body));
                break;
            case MarketProtocol.Delist:
                Reply(id, action, ChannelStatus.Ok, Delist(body));
                break;
            case MarketProtocol.Reprice:
                Reply(id, action, ChannelStatus.Ok, Reprice(body));
                break;
            case MarketProtocol.Listings:
                Reply(id, action, ChannelStatus.Ok, ListingsPage(body));
                break;
            case MarketProtocol.History:
                Reply(id, action, ChannelStatus.Ok, MarketProtocol.WriteHistory(History(body)));
                break;
            case MarketProtocol.WithdrawNotes:
                Withdraw(id, action, body);
                break;
            case MarketBrowseProtocol.Facets when FacetsStatus is { } failure:
                Reply(id, action, failure, ChannelWire.Body(w => ChannelWire.WriteString(w, "The market is busy.")));
                break;
            case MarketBrowseProtocol.Facets:
                Reply(id, action, ChannelStatus.Ok, Facets());
                break;
            case MarketBrowseProtocol.Suggest:
                Reply(id, action, ChannelStatus.Ok, Suggest(ChannelWire.ReadString(ChannelWire.Reader(body))));
                break;
            case MarketBrowseProtocol.Detail:
                Detail(id, action, ChannelWire.Reader(body).ReadInt64());
                break;
            case MarketBrowseProtocol.Browse:
                Browse(id, action, body);
                break;
            case MarketBrowseProtocol.Fits:
                Reply(id, action, ChannelStatus.Ok, MarketBrowseProtocol.WriteFits(Fit));
                break;
            case MarketBrowseProtocol.Buy:
                Reply(id, action, ChannelStatus.Ok, MarketBrowseProtocol.WriteBuy(Buy(body)));
                break;
            default:
                Reply(id, action, ChannelStatus.UnknownAction, ChannelWire.Body(w => ChannelWire.WriteString(w, $"The server has no '{action}' action.")));
                break;
        }
        return true;
    }

    /// <summary>Delivers the replies that are due.</summary>
    public void Pump()
    {
        var now = _clock();
        while (_outbox.Count > 0 && _outbox.Peek().Due <= now)
            Deliver?.Invoke(_outbox.Dequeue().Payload);
    }

    private void Browse(uint id, string action, byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var sort = ChannelWire.ReadString(reader);
        var descending = reader.ReadByte() != 0;
        var cursor = ChannelWire.ReadString(reader);
        var limit = reader.ReadInt32();
        var filters = Enumerable.Range(0, reader.ReadInt32())
            .Select(_ => new ActiveFilter(ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), ChannelWire.ReadString(reader))).ToList();
        BrowseRequests.Add((sort, descending, cursor, limit, filters));

        if (BrowseStatus is { } failure)
        {
            Reply(id, action, failure, ChannelWire.Body(w => ChannelWire.WriteString(w, "The market is busy.")));
            return;
        }

        var matches = Matching(filters);
        var ordered = Ordered(matches, sort, descending);
        // the cursor is the index of the page's first listing, as text
        var offset = cursor.Length == 0 ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var page = ordered.Skip(offset).Take(limit).ToList();
        var next = offset + page.Count < ordered.Count ? (offset + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        Reply(id, action, ChannelStatus.Ok, ChannelWire.Body(w =>
        {
            w.Write(page.Count);
            foreach (var card in page) WriteListing(w, card);
            ChannelWire.WriteString(w, next);
        }));
    }

    private IEnumerable<ListingCard> Matching(IReadOnlyList<ActiveFilter> filters)
    {
        IEnumerable<ListingCard> rows = Catalog;
        foreach (var filter in filters)
        {
            rows = filter.Key switch
            {
                "name" => rows.Where(r => r.Name.Contains(filter.Low, StringComparison.OrdinalIgnoreCase)),
                "category" => rows.Where(r => r.ItemType == filter.Low),
                "price" => rows.Where(r => (filter.Low.Length == 0 || r.Price >= long.Parse(filter.Low)) && (filter.High.Length == 0 || r.Price <= long.Parse(filter.High))),
                "seller" => rows.Where(r => r.Seller == filter.Low),
                "level" => rows.Where(r => r.Level >= int.Parse(filter.Low)),
                "workmanship" => rows.Where(r => r.Workmanship >= int.Parse(filter.Low)),
                "arcane" => rows.Where(r => r.ArcaneLore >= int.Parse(filter.Low)),
                _ => throw new InvalidOperationException($"The fake has no filter {filter.Key}."),
            };
        }
        return rows;
    }

    private static IList<ListingCard> Ordered(IEnumerable<ListingCard> rows, string sort, bool descending)
    {
        IOrderedEnumerable<ListingCard> ordered = sort switch
        {
            "name" => rows.OrderBy(r => r.Name, StringComparer.Ordinal),
            "price" => rows.OrderBy(r => r.Price),
            "seller" => rows.OrderBy(r => r.Seller, StringComparer.Ordinal),
            "workmanship" => rows.OrderBy(r => r.Workmanship ?? int.MinValue),
            "level" => rows.OrderBy(r => r.Level ?? int.MinValue),
            "arcane" => rows.OrderBy(r => r.ArcaneLore ?? int.MinValue),
            _ => rows.OrderBy(r => r.Listed),
        };
        if (descending) ordered = sort switch
        {
            "name" => rows.OrderByDescending(r => r.Name, StringComparer.Ordinal),
            "price" => rows.OrderByDescending(r => r.Price),
            "seller" => rows.OrderByDescending(r => r.Seller, StringComparer.Ordinal),
            "workmanship" => rows.OrderByDescending(r => r.Workmanship ?? int.MinValue),
            "level" => rows.OrderByDescending(r => r.Level ?? int.MinValue),
            "arcane" => rows.OrderByDescending(r => r.ArcaneLore ?? int.MinValue),
            _ => rows.OrderByDescending(r => r.Listed),
        };
        return ordered.ThenBy(r => r.Id).ToList();
    }

    private byte[] Facets() => ChannelWire.Body(w =>
    {
        w.Write(Sorts.Length);
        foreach (var sort in Sorts)
        {
            ChannelWire.WriteString(w, sort.Value);
            ChannelWire.WriteString(w, sort.Label);
            w.Write((byte)(sort.Descending ? 1 : 0));
        }
        w.Write(Kinds.Length);
        foreach (var kind in Kinds)
        {
            ChannelWire.WriteString(w, kind.Key);
            ChannelWire.WriteString(w, kind.Label);
            w.Write((byte)kind.Shape);
            ChannelWire.WriteString(w, kind.Query);
            // the category kind's options are the categories on sale, with their counts
            var options = kind.Key == "category"
                ? Catalog.GroupBy(r => r.ItemType).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (Value: g.Key, Label: Words(g.Key), Count: g.Count())).ToList()
                : new List<(string Value, string Label, int Count)>();
            w.Write(options.Count);
            foreach (var option in options)
            {
                ChannelWire.WriteString(w, option.Value);
                ChannelWire.WriteString(w, option.Label);
                w.Write(option.Count);
            }
        }
    });

    private byte[] Suggest(string text)
    {
        var names = text.Length == 0 ? new List<string>()
            : Catalog.Where(r => r.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Select(r => r.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).Take(10).ToList();
        return ChannelWire.Body(w =>
        {
            w.Write(names.Count);
            foreach (var name in names) ChannelWire.WriteString(w, name);
        });
    }

    private void Detail(uint id, string action, long listingId)
    {
        var card = Catalog.FirstOrDefault(r => r.Id == listingId);
        if (card == null)
        {
            Reply(id, action, ChannelStatus.BadRequest, ChannelWire.Body(w => ChannelWire.WriteString(w, "That listing is no longer for sale.")));
            return;
        }
        Reply(id, action, ChannelStatus.Ok, ChannelWire.Body(w =>
        {
            WriteListing(w, card);
            var lines = new List<string> { $"Value: {card.Price:N0}", $"Workmanship: {card.Workmanship}" };
            if (card.Level is int level) lines.Add($"Wield level: {level}");
            w.Write(lines.Count);
            foreach (var line in lines) ChannelWire.WriteString(w, line);
            w.Write(1);
            ChannelWire.WriteString(w, "Legendary Invulnerability");
            w.Write((byte)0);
        }));
    }

    /// <summary>
    /// A purchase as the server makes it: a repeated key gets its first answer and nothing else happens; a refusal moves nothing; a purchase takes
    /// the price off the balance and the listing off sale, and the item goes to the pack when it was chosen and fits.
    /// </summary>
    private BuyReply Buy(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var request = (Id: reader.ReadInt64(), Price: reader.ReadInt64(), Count: reader.ReadInt32(), Key: ChannelWire.ReadString(reader), ToPack: reader.ReadByte() != 0);
        BuyRequests.Add(request);
        if (_purchases.TryGetValue(request.Key, out var first)) return first;

        if (BuyRefusal is { } refusal)
        {
            if (refusal.Code == "gone") Catalog.RemoveAll(card => card.Id == request.Id);
            return refusal;
        }

        var card = Catalog.First(c => c.Id == request.Id);
        Catalog.Remove(card);
        var before = Account;
        Account = new MarketAccount(before.Balance - card.Price, before.Frozen, before.Paused, before.VaultCount, before.VaultCapacity, before.ListingCount, before.ListingCap, before.Character);
        var inPack = request.ToPack && Fit.Fits && PackMoveRefusal == null;
        var place = $"{card.Name} is in your {(inPack ? "pack" : "Vault")}.";
        var message = request.ToPack && PackMoveRefusal != null ? $"{PackMoveRefusal} {place}" : place;
        var reply = new BuyReply(MarketBrowseProtocol.Bought, message, card.Price, Account.Balance, inPack);
        _purchases[request.Key] = reply;
        return reply;
    }

    private static string Words(string name) => System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
    /// <summary>Adds a listing as if the seller listed it at the time: an active one, or any status the tests need.</summary>
    public FakeListing Seed(uint itemGuid, string name, long price, string status = ListingStatus.Active, DateTime? created = null, DateTime? closed = null)
    {
        var listing = new FakeListing(_nextListingId++, itemGuid, name, price, status, created ?? _clock(), closed);
        Listings.Add(listing);
        return listing;
    }

    /// <summary>The string the listings statuses are drawn with on the wire.</summary>
    public static class ListingStatus
    {
        public const string Active = "active";
        public const string Sold = "sold";
        public const string Expired = "expired";
        public const string Delisted = "delisted";
    }

    /// <summary>vault.list's reply: the page from the request's offset, at most its count</summary>
    private byte[] VaultPage(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        ChannelWire.ReadString(reader);
        var offset = reader.ReadInt32();
        var items = Vault.Skip(offset).Take(reader.ReadInt32()).ToList();
        return ChannelWire.Body(w =>
        {
            w.Write((byte)1);
            w.Write(Account.Balance);
            w.Write(Account.VaultCapacity);
            w.Write(Vault.Count);
            w.Write(Vault.Count);
            w.Write(items.Count);
            foreach (var item in items)
            {
                w.Write(item.Guid);
                ChannelWire.WriteString(w, item.Name);
                w.Write(0u);
                w.Write(item.StackSize);
                w.Write(0);
                ChannelWire.WriteString(w, item.State);
                ChannelWire.WriteString(w, "Arwic Wanderer");
                w.Write(UnixNow());
                w.Write(item.Visual.Plate);
                w.Write(item.Visual.Underlay);
                w.Write(item.Visual.Icon);
                w.Write(item.Visual.Overlay);
                w.Write(item.OverlaySecondary);
                w.Write(unchecked((int)item.Visual.UiEffects));
            }
        });
    }

    private byte[] Sellable(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var count = reader.ReadInt32();
        var guids = Enumerable.Range(0, count).Select(_ => reader.ReadUInt32()).ToList();
        return ChannelWire.Body(w =>
        {
            w.Write(guids.Count);
            foreach (var guid in guids)
            {
                var reason = ReasonFor(guid);
                w.Write(guid);
                w.Write((byte)(reason == null ? 1 : 0));
                ChannelWire.WriteString(w, reason ?? string.Empty);
            }
        });
    }

    /// <summary>The reason an item can't be listed: a seeded reason, a listed Vault item, or null when it can be listed.</summary>
    private string? ReasonFor(uint guid)
    {
        if (Unsellable.TryGetValue(guid, out var reason)) return reason;
        var vault = Vault.FirstOrDefault(item => item.Guid == guid);
        if (vault != null) return vault.State == "listed" ? "Already listed." : vault.State == "held" ? null : "is being withdrawn";
        return Pack.ContainsKey(guid) ? null : "That item is not in your pack or Vault.";
    }

    private byte[] List(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var guid = reader.ReadUInt32();
        var price = reader.ReadInt64();
        LastList = (guid, price);

        if (price < 1) return ListReply(false, Vault.Any(item => item.Guid == guid), "Set a whole price of at least 1 MMD.");

        if (!Vault.Any(item => item.Guid == guid) && Pack.TryGetValue(guid, out var packed))
        {
            // the deposit comes first, as the server does it: the item is in the Vault now, whether or not the listing goes through
            Vault.Add(new SellVaultItem(packed.Guid, packed.Name, packed.StackSize, "held", packed.Visual, packed.OverlaySecondary));
            Pack.Remove(guid);
        }

        var item = Vault.FirstOrDefault(entry => entry.Guid == guid);
        if (item == null) return ListReply(false, false, "That item is not in your Vault.");
        if (ListRefusal != null) return ListReply(false, true, $"{ListRefusal} {item.Name} is in your Vault.");

        Seed(guid, item.Name, price);
        Vault[Vault.IndexOf(item)] = new SellVaultItem(item.Guid, item.Name, item.StackSize, "listed", item.Visual, item.OverlaySecondary);
        return ListReply(true, true, $"{item.Name} is listed for {price:N0} MMD for {LifetimeDays} days. Unsold, it returns to your Vault.");
    }

    private static byte[] ListReply(bool listed, bool inVault, string message) => ChannelWire.Body(w =>
    {
        w.Write((byte)(listed ? 1 : 0));
        w.Write((byte)(inVault ? 1 : 0));
        ChannelWire.WriteString(w, message);
    });

    private byte[] Delist(byte[] body)
    {
        var listingId = ChannelWire.Reader(body).ReadInt64();
        LastDelist = listingId;

        var listing = Listings.FirstOrDefault(entry => entry.Id == listingId && entry.Status == ListingStatus.Active);
        if (listing == null)
            return ListReply(false, true, "That listing is no longer active.");

        listing.Status = ListingStatus.Delisted;
        var item = Vault.FirstOrDefault(entry => entry.Guid == listing.ItemGuid);
        if (item != null) Vault[Vault.IndexOf(item)] = new SellVaultItem(item.Guid, item.Name, item.StackSize, "held", item.Visual, item.OverlaySecondary);

        return ListReply(true, true, $"{listing.Name} is back in your Vault.");
    }

    private byte[] Reprice(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var listingId = reader.ReadInt64();
        var price = reader.ReadInt64();
        LastReprice = (listingId, price);

        var listing = Listings.FirstOrDefault(entry => entry.Id == listingId && entry.Status == ListingStatus.Active);
        if (listing == null) return ListReply(false, true, "That listing is no longer active.");
        if (RepriceRefusal != null) return ListReply(false, true, RepriceRefusal);
        if (price < 1) return ListReply(false, true, "Set a whole price of at least 1 MMD.");

        listing.Price = price;
        return ListReply(true, true, $"{listing.Name} is now listed for {price:N0} MMD.");
    }

    private byte[] ListingsPage(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var tab = reader.ReadByte();
        var count = reader.ReadInt32();
        LastListings = (tab, count);

        var status = tab switch { MarketProtocol.SoldTab => ListingStatus.Sold, MarketProtocol.ExpiredTab => ListingStatus.Expired, _ => ListingStatus.Active };
        var rows = Listings.Where(listing => listing.Status == status).Take(count).ToList();
        var total = Listings.Count(listing => listing.Status == status);
        return ChannelWire.Body(w =>
        {
            w.Write(UnixNow());
            w.Write(LifetimeDays);
            w.Write(total);
            w.Write(rows.Count);
            foreach (var row in rows)
            {
                w.Write(row.Id);
                w.Write(row.ItemGuid);
                ChannelWire.WriteString(w, row.Name);
                w.Write(row.Price);
                ChannelWire.WriteString(w, row.Status);
                w.Write(new DateTimeOffset(DateTime.SpecifyKind(row.Created, DateTimeKind.Utc)).ToUnixTimeSeconds());
                w.Write(row.Closed is DateTime closed ? new DateTimeOffset(DateTime.SpecifyKind(closed, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0L);
                // the icon from the item's Vault row, zeros once it is gone, as the server sends it
                var item = Vault.FirstOrDefault(v => v.Guid == row.ItemGuid);
                w.Write(item?.Visual.Plate ?? 0u);
                w.Write(item?.Visual.Underlay ?? 0u);
                w.Write(item?.Visual.Icon ?? 0u);
                w.Write(item?.Visual.Overlay ?? 0u);
                w.Write(item?.OverlaySecondary ?? 0u);
                w.Write(unchecked((int)(item?.Visual.UiEffects ?? 0u)));
            }
        });
    }

    private long UnixNow() => new DateTimeOffset(DateTime.SpecifyKind(_clock(), DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>One page as the server pages it: newest first, a page of <see cref="MarketProtocol.HistoryPageSize"/> older than each cursor.</summary>
    private HistoryPage History(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var linesBefore = reader.ReadInt64();
        var movementsBefore = reader.ReadInt64();
        HistoryRequests.Add((linesBefore, movementsBefore));

        var lines = Ledger.Where(line => linesBefore == MarketProtocol.Newest || line.Sequence < linesBefore).OrderByDescending(line => line.Sequence).ToList();
        var movements = Movements.Where(movement => movementsBefore == MarketProtocol.Newest || movement.Id < movementsBefore).OrderByDescending(movement => movement.Id).ToList();

        var linePage = lines.Take(MarketProtocol.HistoryPageSize).ToList();
        var movementPage = movements.Take(MarketProtocol.HistoryPageSize).ToList();

        // the week's figures come with the newest page only, as the server sends them
        var newest = linesBefore == MarketProtocol.Newest && movementsBefore == MarketProtocol.Newest;

        return new HistoryPage(Account.Balance, newest ? EarnedThisWeek : 0, newest ? SpentThisWeek : 0, newest ? ItemsSold : 0, newest ? ItemsBought : 0,
            linePage,
            lines.Count > linePage.Count ? linePage[^1].Sequence : MarketProtocol.Newest,
            movementPage,
            movements.Count > movementPage.Count ? movementPage[^1].Id : MarketProtocol.Newest);
    }

    private void Withdraw(uint id, string action, byte[] body)
    {
        var amount = ChannelWire.Reader(body).ReadInt64();
        Withdrawals.Add(amount);

        if (WithdrawRefusal != null)
        {
            Reply(id, action, ChannelStatus.Ok, MarketProtocol.WriteWithdraw(false, WithdrawRefusal));
            return;
        }

        var before = Account;
        Account = new MarketAccount(before.Balance - amount, before.Frozen, before.Paused, before.VaultCount, before.VaultCapacity, before.ListingCount, before.ListingCap, before.Character);
        Reply(id, action, ChannelStatus.Ok, MarketProtocol.WriteWithdraw(true, $"You withdraw {amount} trade notes. Your balance is {Account.Balance:N0} MMD."));
    }

    /// <summary>Pushes a topic to the client after the latency, as the server's own pushes come.</summary>
    public void Push(string topic, byte[] body) =>
        _outbox.Enqueue((_clock() + Latency, ChannelWire.EncodeEvent(ChannelEventKind.Push, ChannelStatus.Ok, 0, topic, body)));

    private void Reply(uint id, string action, ChannelStatus status, byte[] body) =>
        _outbox.Enqueue((_clock() + Latency, ChannelWire.EncodeEvent(ChannelEventKind.Reply, status, id, action, body)));

    /// <summary>
    /// Five listings from two sellers, of two types, with their stats. Each is listed a little earlier than the one before.
    /// </summary>
    public static IEnumerable<ListingCard> SampleListings()
    {
        var listed = new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
        yield return Card(1, "Nariyid Breastplate", "Armor", "Gold", 1200, 8, 180, 300, "Kaelin", listed.AddHours(-1));
        yield return Card(2, "Amuli Shadow Leggings", "Armor", "Silver", 2400, 9, 150, 280, "Saro", listed.AddHours(-2));
        yield return Card(3, "Atlan Sword", "MeleeWeapon", "Steel", 3500, 9, 180, null, "Arwic Wanderer", listed.AddHours(-3), own: true);
        yield return Card(4, "Weeping Ring", "Jewelry", "Gold", 5000, 10, null, 250, "Saro", listed.AddHours(-4));
        yield return Card(5, "Frost Spear", "MeleeWeapon", "Steel", 1450, 8, 150, null, "Kaelin", listed.AddHours(-5));
    }

    private static ListingCard Card(long id, string name, string type, string material, long price, int? workmanship, int? level, int? arcane,
        string seller, DateTimeOffset listed, bool own = false) => new(id, (uint)(0x80000000 + id), name, type, Words(type), material, workmanship, level,
        arcane, $"{name} summary", 1, price, seller, listed, own, 0x060011D2, 0, 0x06000001, 0, 0, 0);

    /// <summary>A listing as the server writes it, in the order the client reads it</summary>
    private static void WriteListing(BinaryWriter w, ListingCard card)
    {
        w.Write(card.Id);
        w.Write(card.ItemGuid);
        ChannelWire.WriteString(w, card.Name);
        ChannelWire.WriteString(w, card.ItemType);
        ChannelWire.WriteString(w, card.TypeLabel);
        ChannelWire.WriteString(w, card.Material);
        w.Write(card.Workmanship ?? -1);
        w.Write(card.Level ?? -1);
        w.Write(card.ArcaneLore ?? -1);
        ChannelWire.WriteString(w, card.Summary);
        w.Write(card.Quantity);
        w.Write(card.Price);
        ChannelWire.WriteString(w, card.Seller);
        w.Write(card.Listed.ToUnixTimeSeconds());
        w.Write((byte)(card.Own ? 1 : 0));
        w.Write(card.Plate);
        w.Write(card.Underlay);
        w.Write(card.Icon);
        w.Write(card.Overlay);
        w.Write(card.OverlaySecondary);
        w.Write(card.UiEffects);
    }
}

/// <summary>A listing the fake server holds: the item, the price, its status, and when it was made and closed.</summary>
public sealed class FakeListing
{
    public FakeListing(long id, uint itemGuid, string name, long price, string status, DateTime created, DateTime? closed)
    {
        Id = id; ItemGuid = itemGuid; Name = name; Price = price; Status = status; Created = created; Closed = closed;
    }

    public long Id { get; }
    public uint ItemGuid { get; }
    public string Name { get; }
    public long Price { get; set; }
    public string Status { get; set; }
    public DateTime Created { get; }
    public DateTime? Closed { get; }
}
