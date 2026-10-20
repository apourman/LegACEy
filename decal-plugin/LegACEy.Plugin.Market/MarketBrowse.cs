using System;
using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Market;

/// <summary>The results' two views: cards, or the table with the portal's columns.</summary>
public enum BrowseView { Cards, Table }

/// <summary>
/// The Browse screen's state and its channel requests: the filters, the sort, the pages loaded so far, the selected listing and the suggestions.
/// It holds no view. A reply for a search, or a suggestion, that has since changed is dropped, so an old answer can't reach the screen.
/// </summary>
public sealed class MarketBrowse : IDisposable
{
    /// <summary>The sort the window opens with</summary>
    public const string DefaultSort = "newest";

    /// <summary>The filter kind whose options are the categories</summary>
    public const string CategoryKey = "category";

    // the requests go through a client of their own, so disposing the screen drops its replies
    private readonly MarketClient _client;
    private readonly List<ListingCard> _results = new();
    private readonly HashSet<long> _resultIds = new();
    private List<ActiveFilter> _filters = new();
    private int _generation;
    private int _suggestGeneration;
    private string? _nextCursor;
    private bool _disposed;

    public MarketBrowse(IServerChannel channel)
    {
        _client = new MarketClient(channel);
    }

    /// <summary>The sort orders and filter kinds; null until they arrive, and asked again on the next refresh while they haven't</summary>
    public MarketFacets? Facets { get; private set; }
    public IReadOnlyList<ActiveFilter> Filters => _filters;
    public string Sort { get; private set; } = DefaultSort;
    public bool Descending { get; private set; } = true;
    public BrowseView View { get; private set; } = BrowseView.Cards;
    /// <summary>The listings loaded so far, in the order the server gave them, with no repeats</summary>
    public IReadOnlyList<ListingCard> Results => _results;
    public bool HasMore => _nextCursor != null;
    public bool Loading { get; private set; }
    /// <summary>The listing the detail panel shows, or null when none is picked</summary>
    public long? SelectedId { get; private set; }
    public ListingDetail? Detail { get; private set; }
    /// <summary>The plain message for the last failure: a refusal, or a listing that's gone</summary>
    public string Notice { get; private set; } = string.Empty;
    public IReadOnlyList<string> Suggestions { get; private set; } = Array.Empty<string>();

    /// <summary>Raised whenever anything on the screen changes</summary>
    public event EventHandler? Changed;

    public void Start() => Refresh();

    /// <summary>The first page again, for the filters and sort on screen</summary>
    public void Refresh()
    {
        if (Facets == null) RequestFacets();
        var generation = ++_generation;
        _nextCursor = null;
        _results.Clear();
        _resultIds.Clear();
        Load(generation, string.Empty);
    }

    /// <summary>Loads the next page of the current search, if there is one. Ignored while a page is already loading.</summary>
    public void LoadMore()
    {
        if (!HasMore || Loading) return;
        Load(_generation, _nextCursor!);
    }

    private void RequestFacets()
    {
        Request(MarketBrowseProtocol.Facets, Array.Empty<byte>(), reply =>
        {
            if (reply.Ok) Facets = MarketBrowseProtocol.ReadFacets(reply.Body);
            Raise();
        });
    }

    private void Load(int generation, string cursor)
    {
        Loading = true;
        Notice = string.Empty;
        Raise();
        Request(MarketBrowseProtocol.Browse, MarketBrowseProtocol.BrowseRequest(Sort, Descending, cursor, _filters), reply =>
        {
            // a page for a search the window has since changed is dropped
            if (generation != _generation) return;
            Loading = false;
            if (!reply.Ok)
            {
                Notice = reply.Message;
                Raise();
                return;
            }
            var (items, next) = MarketBrowseProtocol.ReadBrowse(reply.Body);
            foreach (var item in items)
            {
                if (!_resultIds.Add(item.Id)) continue;
                _results.Add(item);
            }
            _nextCursor = next.Length > 0 ? next : null;
            Raise();
        });
    }

    /// <summary>Puts a filter on, replacing any filter with its key</summary>
    public void AddFilter(string key, string low, string high = "")
    {
        _filters = _filters.Where(f => f.Key != key).Append(new ActiveFilter(key, low, high)).ToList();
        Refresh();
    }

    public void RemoveFilter(string key)
    {
        if (!_filters.Any(f => f.Key == key)) return;
        _filters = _filters.Where(f => f.Key != key).ToList();
        Refresh();
    }

    /// <summary>The search box's text, as the name filter. Blank takes the filter off.</summary>
    public void Search(string text)
    {
        ClearSuggestions();
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) RemoveFilter("name");
        else AddFilter("name", trimmed);
    }

    /// <summary>The name suggestions for the text as typed. A reply for text that has since changed is dropped.</summary>
    public void SuggestFor(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        var generation = ++_suggestGeneration;
        if (trimmed.Length == 0)
        {
            ClearSuggestions();
            return;
        }
        Request(MarketBrowseProtocol.Suggest, MarketBrowseProtocol.SuggestRequest(trimmed), reply =>
        {
            if (generation != _suggestGeneration) return;
            Suggestions = reply.Ok ? MarketBrowseProtocol.ReadSuggest(reply.Body) : Array.Empty<string>();
            Raise();
        });
    }

    public void ClearSuggestions()
    {
        _suggestGeneration++;
        if (Suggestions.Count == 0) return;
        Suggestions = Array.Empty<string>();
        Raise();
    }

    /// <summary>A category from the side bar or the chip; null for all categories</summary>
    public void SetCategory(string? name)
    {
        if (name == null) RemoveFilter(CategoryKey);
        else AddFilter(CategoryKey, name);
    }

    /// <summary>"More from" a seller: the seller filter, replacing any other. The detail closes with the new search.</summary>
    public void ShowSeller(string seller)
    {
        SelectedId = null;
        Detail = null;
        _filters = _filters.Where(f => f.Key != "seller").Append(new ActiveFilter("seller", seller)).ToList();
        Refresh();
    }

    /// <summary>
    /// Sorts by the value. The sort the window is on flips its direction; any other takes its default direction.
    /// </summary>
    public void SetSort(string value)
    {
        var option = Facets?.Sorts.FirstOrDefault(s => s.Value == value);
        if (option == null) return;
        if (value == Sort) Descending = !Descending;
        else
        {
            Sort = value;
            Descending = option.Descending;
        }
        Refresh();
    }

    public void SetView(BrowseView view)
    {
        if (View == view) return;
        View = view;
        Raise();
    }

    /// <summary>Picks a listing for the detail panel</summary>
    public void Select(long id)
    {
        SelectedId = id;
        Detail = null;
        Notice = string.Empty;
        Raise();
        Request(MarketBrowseProtocol.Detail, MarketBrowseProtocol.DetailRequest(id), reply =>
        {
            if (SelectedId != id) return;
            if (reply.Ok)
            {
                Detail = MarketBrowseProtocol.ReadDetail(reply.Body);
            }
            else
            {
                // a listing that has gone is no longer on screen, nor in the results
                Notice = reply.Message;
                SelectedId = null;
                if (reply.Status == ChannelStatus.BadRequest) _results.RemoveAll(card => card.Id == id);
            }
            Raise();
        });
    }

    /// <summary>Takes a listing out of the results: it was sold or has gone. Its detail closes, and a later page can't bring it back.</summary>
    public void Remove(long id)
    {
        _results.RemoveAll(card => card.Id == id);
        if (SelectedId == id)
        {
            SelectedId = null;
            Detail = null;
        }
        Raise();
    }

    /// <summary>The buy dialog's attempt, or null while the dialog is closed</summary>
    public BuyAttempt? Buying { get; private set; }

    /// <summary>Raised when a purchase went through, with the listing and the receipt. The listing has left the results by then.</summary>
    public event Action<ListingCard, BuyReply>? Bought;

    /// <summary>
    /// Opens the buy dialog for a listing in the results, with pack delivery, and asks the server whether the item fits in the pack. An answer
    /// that it doesn't greys the pack out and chooses the Vault. Your own listings are not bought.
    /// </summary>
    public void OpenBuy(long id)
    {
        var listing = Detail?.Listing.Id == id ? Detail.Listing : _results.FirstOrDefault(card => card.Id == id);
        if (listing == null || listing.Own) return;
        var attempt = new BuyAttempt(listing);
        Buying = attempt;
        Raise();
        Request(MarketBrowseProtocol.Fits, MarketBrowseProtocol.DetailRequest(id), reply =>
        {
            if (Buying != attempt) return;
            // a fit the server could not check leaves the pack open: if the item then doesn't fit, the server keeps it in the Vault and the purchase stands
            attempt.Fit = reply.Ok ? MarketBrowseProtocol.ReadFits(reply.Body) : null;
            if (attempt.Fit is { Fits: false }) attempt.ToPack = false;
            Raise();
        });
    }

    /// <summary>Delivery to the pack or the Vault. The pack can't be chosen when the server says the item won't fit.</summary>
    public void ChooseDelivery(bool toPack)
    {
        if (Buying is not { Busy: false } attempt || (toPack && !attempt.PackAllowed)) return;
        attempt.ToPack = toPack;
        Raise();
    }

    /// <summary>
    /// Buys at the dialog's price. A click while the purchase is in flight is ignored, and a retry sends the attempt's same key, so the server charges once.
    /// Bought: the dialog closes, the listing leaves the results and <see cref="Bought"/> is raised. Gone: the listing leaves the results.
    /// </summary>
    public void ConfirmBuy()
    {
        if (Buying is not { CanConfirm: true } attempt) return;
        attempt.Busy = true;
        attempt.Notice = string.Empty;
        Raise();
        var listing = attempt.Listing;
        Request(MarketBrowseProtocol.Buy, MarketBrowseProtocol.BuyRequest(listing.Id, listing.Price, listing.Quantity, attempt.Key, attempt.ToPack), reply =>
        {
            attempt.Busy = false;
            if (!reply.Ok)
            {
                // no answer to the purchase: a retry with the same key finds out whether it went through
                attempt.Code = string.Empty;
                attempt.Notice = reply.Message;
                Raise();
                return;
            }
            var answer = MarketBrowseProtocol.ReadBuy(reply.Body);
            if (answer.Code == MarketBrowseProtocol.Bought)
            {
                if (Buying == attempt) Buying = null;
                Remove(listing.Id);
                Bought?.Invoke(listing, answer);
                return;
            }
            attempt.Code = answer.Code;
            attempt.Notice = answer.Message;
            if (answer.Code == BuyAttempt.PriceChanged) attempt.NewPrice = answer.Price;
            if (answer.Code == BuyAttempt.Gone) Remove(listing.Id);
            else Raise();
        });
    }

    /// <summary>After a price change: a new attempt at the new price, with a new key, as the portal's Review new price does.</summary>
    public void AcceptNewPrice()
    {
        if (Buying is not { NewPrice: long price, Busy: false } attempt) return;
        var listing = attempt.Listing with { Price = price };
        var index = _results.FindIndex(card => card.Id == listing.Id);
        if (index >= 0) _results[index] = listing;
        Buying = new BuyAttempt(listing) { Fit = attempt.Fit, ToPack = attempt.ToPack };
        Raise();
    }

    public void CloseBuy()
    {
        if (Buying is not { Busy: false }) return;
        Buying = null;
        Raise();
    }

    /// <summary>Closes the detail panel, back to the results</summary>
    public void Back()
    {
        if (SelectedId == null) return;
        SelectedId = null;
        Detail = null;
        Raise();
    }

    private void Request(string action, byte[] body, Action<ChannelReply> completed) => _client.Send(action, body, completed);

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        Changed = null;
        Bought = null;
    }
}

/// <summary>
/// One attempt at buying a listing, as the buy dialog shows it: the listing at the price seen, the attempt's idempotency key, the delivery, the
/// server's pack fit, and the last refusal. Every confirm of the attempt sends its one key.
/// </summary>
public sealed class BuyAttempt
{
    public const string PriceChanged = "price_changed";
    public const string Gone = "gone";

    /// <summary>The refusals worth trying again with the same key, as the portal offers Retry for them</summary>
    private static readonly HashSet<string> Retryable = new() { "busy", "rate_limited" };

    internal BuyAttempt(ListingCard listing)
    {
        Listing = listing;
        Key = Guid.NewGuid().ToString("N");
    }

    public ListingCard Listing { get; }
    public string Key { get; }
    public bool ToPack { get; internal set; } = true;
    /// <summary>The server's answer to whether the item fits in the pack; null until it comes, or when it could not be checked</summary>
    public PackFit? Fit { get; internal set; }
    public bool PackAllowed => Fit?.Fits != false;
    public bool Busy { get; internal set; }
    /// <summary>The last refusal's code, empty when there is none</summary>
    public string Code { get; internal set; } = string.Empty;
    /// <summary>The player's message for the last refusal or failure</summary>
    public string Notice { get; internal set; } = string.Empty;
    /// <summary>The listing's price now, after a price_changed refusal</summary>
    public long? NewPrice { get; internal set; }
    public bool CanConfirm => !Busy && (Code.Length == 0 || Retryable.Contains(Code));
}
