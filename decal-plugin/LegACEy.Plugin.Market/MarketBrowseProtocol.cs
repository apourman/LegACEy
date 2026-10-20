using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Market;

/// <summary>How a filter takes its value: text, a number minimum, a number range, or one of the kind's options.</summary>
public enum FilterShape : byte { Text = 0, Minimum = 1, Range = 2, Choice = 3 }

/// <summary>One choice a filter kind offers, with the listings it matches.</summary>
public sealed record FilterOption(string Value, string Label, int Count);

/// <summary>A filter the server offers. The window draws its chip, its Add filter entry and, for a choice, its options from this.</summary>
public sealed record FilterKind(string Key, string Label, FilterShape Shape, string Query, IReadOnlyList<FilterOption> Options);

/// <summary>A sort order the server offers. Descending is its default direction.</summary>
public sealed record SortOption(string Value, string Label, bool Descending);

/// <summary>The menus the server describes: the sort orders and the filter kinds.</summary>
public sealed record MarketFacets(IReadOnlyList<SortOption> Sorts, IReadOnlyList<FilterKind> Kinds)
{
    /// <summary>The category kind's options, which the side bar and the category menu list with their counts</summary>
    public IReadOnlyList<FilterOption> Categories => Kinds.FirstOrDefault(k => k.Key == MarketBrowse.CategoryKey)?.Options ?? Array.Empty<FilterOption>();
}

/// <summary>A filter the search has, with the values it was given. A range uses both; the others use Low.</summary>
public sealed record ActiveFilter(string Key, string Low, string High = "");

/// <summary>A listing as the browse results and the detail panel show it. Stats the listing doesn't have are null.</summary>
public sealed record ListingCard(long Id, uint ItemGuid, string Name, string ItemType, string TypeLabel, string Material, int? Workmanship, int? Level,
    int? ArcaneLore, string Summary, int Quantity, long Price, string Seller, DateTimeOffset Listed, bool Own, uint Plate, uint Underlay, uint Icon,
    uint Overlay, uint OverlaySecondary, uint UiEffects);

/// <summary>A listing with its appraisal lines and spells, as the portal's listing page shows them.</summary>
public sealed record ListingDetail(ListingCard Listing, IReadOnlyList<string> Lines, IReadOnlyList<(string Name, bool Cantrip)> Spells);

/// <summary>
/// Bodies of the Market's browse actions. They must match ACE.Server.Market.MarketBrowseActions.
/// </summary>
public static class MarketBrowseProtocol
{
    public const string Browse = "market.browse";
    public const string Suggest = "market.suggest";
    public const string Facets = "market.facets";
    public const string Detail = "market.detail";

    /// <summary>The listings a page asks for</summary>
    public const int PageSize = 50;

    /// <summary>What a listing's stat reads as when the listing doesn't have it on the wire</summary>
    private const int NoStat = -1;

    /// <summary>A page request: the sort, its direction, the cursor ("" for the first page), the limit and the filters.</summary>
    public static byte[] BrowseRequest(string sort, bool descending, string cursor, IReadOnlyList<ActiveFilter> filters, int limit = PageSize) => ChannelWire.Body(w =>
    {
        ChannelWire.WriteString(w, sort);
        w.Write((byte)(descending ? 1 : 0));
        ChannelWire.WriteString(w, cursor);
        w.Write(limit);
        w.Write(filters.Count);
        foreach (var filter in filters)
        {
            ChannelWire.WriteString(w, filter.Key);
            ChannelWire.WriteString(w, filter.Low);
            ChannelWire.WriteString(w, filter.High);
        }
    });

    public static (IReadOnlyList<ListingCard> Items, string NextCursor) ReadBrowse(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var count = reader.ReadInt32();
        if (count < 0 || count > PageSize * 2) throw new InvalidDataException($"A browse page of {count} listings is not plausible.");
        var items = new List<ListingCard>(count);
        for (var i = 0; i < count; i++) items.Add(ReadListing(reader));
        return (items, ChannelWire.ReadString(reader));
    }

    public static byte[] SuggestRequest(string text) => ChannelWire.Body(w => ChannelWire.WriteString(w, text));

    public static IReadOnlyList<string> ReadSuggest(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return Enumerable.Range(0, reader.ReadInt32()).Select(_ => ChannelWire.ReadString(reader)).ToList();
    }

    public static MarketFacets ReadFacets(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var sorts = Enumerable.Range(0, reader.ReadInt32())
            .Select(_ => new SortOption(ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), reader.ReadByte() != 0)).ToList();
        var kinds = Enumerable.Range(0, reader.ReadInt32()).Select(_ => ReadKind(reader)).ToList();
        return new MarketFacets(sorts, kinds);
    }

    private static FilterKind ReadKind(BinaryReader reader)
    {
        var key = ChannelWire.ReadString(reader);
        var label = ChannelWire.ReadString(reader);
        var shape = (FilterShape)reader.ReadByte();
        var query = ChannelWire.ReadString(reader);
        var options = Enumerable.Range(0, reader.ReadInt32())
            .Select(_ => new FilterOption(ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), reader.ReadInt32())).ToList();
        return new FilterKind(key, label, shape, query, options);
    }

    public static byte[] DetailRequest(long id) => ChannelWire.Body(w => w.Write(id));

    public static ListingDetail ReadDetail(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var listing = ReadListing(reader);
        var lines = Enumerable.Range(0, reader.ReadInt32()).Select(_ => ChannelWire.ReadString(reader)).ToList();
        var spells = Enumerable.Range(0, reader.ReadInt32()).Select(_ => (Name: ChannelWire.ReadString(reader), Cantrip: reader.ReadByte() != 0)).ToList();
        return new ListingDetail(listing, lines, spells);
    }

    private static ListingCard ReadListing(BinaryReader reader)
    {
        var id = reader.ReadInt64();
        var guid = reader.ReadUInt32();
        var name = ChannelWire.ReadString(reader);
        var type = ChannelWire.ReadString(reader);
        var typeLabel = ChannelWire.ReadString(reader);
        var material = ChannelWire.ReadString(reader);
        var workmanship = reader.ReadInt32();
        var level = reader.ReadInt32();
        var arcane = reader.ReadInt32();
        var summary = ChannelWire.ReadString(reader);
        var quantity = reader.ReadInt32();
        var price = reader.ReadInt64();
        var seller = ChannelWire.ReadString(reader);
        var listed = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
        var own = reader.ReadByte() != 0;
        var plate = reader.ReadUInt32();
        var underlay = reader.ReadUInt32();
        var icon = reader.ReadUInt32();
        var overlay = reader.ReadUInt32();
        var overlaySecondary = reader.ReadUInt32();
        var uiEffects = reader.ReadUInt32();
        return new ListingCard(id, guid, name, type, typeLabel, material, Stat(workmanship), Stat(level), Stat(arcane), summary, quantity, price,
            seller, listed, own, plate, underlay, icon, overlay, overlaySecondary, uiEffects);
    }

    private static int? Stat(int value) => value == NoStat ? null : value;

    public const string Buy = "market.buy";
    public const string Fits = "market.fits";

    /// <summary>The buy reply's code for a purchase that went through. The refusals are the portal's codes ("price_changed", "gone" and so on).</summary>
    public const string Bought = "ok";

    /// <summary>A buy: the listing, the price the buyer saw, the whole stack's count, the attempt's idempotency key, and pack or Vault delivery.</summary>
    public static byte[] BuyRequest(long id, long price, int count, string key, bool toPack) => ChannelWire.Body(w =>
    {
        w.Write(id);
        w.Write(price);
        w.Write(count);
        ChannelWire.WriteString(w, key);
        w.Write((byte)(toPack ? 1 : 0));
    });

    public static BuyReply ReadBuy(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return new BuyReply(ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), reader.ReadInt64(), reader.ReadInt64(), reader.ReadByte() != 0);
    }

    public static byte[] WriteBuy(BuyReply reply) => ChannelWire.Body(w =>
    {
        ChannelWire.WriteString(w, reply.Code);
        ChannelWire.WriteString(w, reply.Message);
        w.Write(reply.Price);
        w.Write(reply.Balance);
        w.Write((byte)(reply.InPack ? 1 : 0));
    });

    public static PackFit ReadFits(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return new PackFit(reader.ReadByte() != 0, reader.ReadInt32(), reader.ReadInt32(), ChannelWire.ReadString(reader));
    }

    public static byte[] WriteFits(PackFit fit) => ChannelWire.Body(w =>
    {
        w.Write((byte)(fit.Fits ? 1 : 0));
        w.Write(fit.FreeSlots);
        w.Write(fit.Slots);
        ChannelWire.WriteString(w, fit.Reason);
    });
}

/// <summary>
/// A buy's answer: the code ("ok" or the portal's refusal code), the player's message, the price (paid, or the new one when it changed), the balance
/// after (bought only) and whether the item is in the pack now.
/// </summary>
public sealed record BuyReply(string Code, string Message, long Price, long Balance, bool InPack);

/// <summary>Whether a listing's item would go into the pack now, the pack's free slots and all its slots, and why not when it wouldn't.</summary>
public sealed record PackFit(bool Fits, int FreeSlots, int Slots, string Reason);
