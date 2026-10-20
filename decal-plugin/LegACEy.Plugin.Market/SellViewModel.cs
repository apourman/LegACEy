using System;
using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Market;

/// <summary>Which item tab of the Sell screen is shown.</summary>
public enum SellTab { Pack, Vault }

/// <summary>Which of the account's listing tabs is shown.</summary>
public enum SellListingTab { Active, Sold, Expired }

/// <summary>
/// One item in the Sell screen's grid. Greyed means it can't be listed, and <see cref="Reason"/> says why (the tooltip).
/// Listed is a Vault item already for sale: it carries the gold dot and is never greyed.
/// </summary>
public sealed record SellItem(uint Guid, string Name, int StackSize, ItemVisual Visual, uint OverlaySecondary, bool Listed, bool Sellable, string Reason)
{
    public bool Greyed => !Sellable && !Listed;
}

/// <summary>
/// The Sell screen's state and actions: the pack and Vault items with whether each can be listed, the picked item and its price, and the
/// account's listings. It talks to the server only through the Market client and reads the pack from the inventory port. The view draws
/// <see cref="Changed"/> and never measures anything itself.
/// </summary>
public sealed class SellViewModel : IDisposable
{
    private readonly MarketClient _client;
    private readonly IInventoryPort _inventory;
    private readonly Dictionary<uint, SellAnswer> _answers = new();
    private IReadOnlyList<SellVaultItem> _vault = Array.Empty<SellVaultItem>();
    // an item to pick once its tab has loaded: a pack listing the server left in the Vault
    private uint? _pendingPick;
    private bool _disposed;

    private readonly MarketSales? _sales;

    /// <param name="sales">the unseen sales, which looking at the Sold tab clears</param>
    public SellViewModel(MarketClient client, IInventoryPort inventory, MarketSales? sales = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _sales = sales;
        _inventory.Changed += OnInventoryChanged;
    }

    /// <summary>Raised when anything the view shows has changed. On the render thread.</summary>
    public event Action? Changed;

    public SellTab Tab { get; private set; } = SellTab.Pack;
    /// <summary>The items of the shown tab, in the order the pack or the Vault lists them.</summary>
    public IReadOnlyList<SellItem> Items { get; private set; } = Array.Empty<SellItem>();
    /// <summary>The picked item, or null: the price panel shows nothing then.</summary>
    public SellItem? Selected { get; private set; }
    /// <summary>The price as typed: whole MMD only.</summary>
    public string PriceText { get; private set; } = string.Empty;
    public bool Busy { get; private set; }
    /// <summary>The last server message: a listing's outcome, a delisting's, or why the account can't be read.</summary>
    public string Message { get; private set; } = string.Empty;

    public SellListingTab ListingTab { get; private set; } = SellListingTab.Active;
    /// <summary>The shown listing tab's page, or null before the server has answered it.</summary>
    public ListingsPage? Listings { get; private set; }
    /// <summary>The account's active listings, from the Active tab's total; null before it has been read.</summary>
    public int? ActiveCount { get; private set; }
    /// <summary>The most active listings the account can have, from the account summary; null while the account is unknown.</summary>
    public int? ActiveCap => _client.Account?.ListingCap;
    /// <summary>The listing lifetime in days, as the server's settings have it (sent with every listings reply); null until the server has said.</summary>
    public int? LifetimeDays { get; private set; }

    /// <summary>The price as a whole number of MMD, or null when the text isn't one.</summary>
    public long? Price => WholePrice(PriceText);

    /// <summary>The active listing whose price is being changed, or null: its row shows the price field and the confirm.</summary>
    public long? RepriceListingId { get; private set; }
    /// <summary>The new price as typed for <see cref="RepriceListingId"/>: whole MMD only.</summary>
    public string RepriceText { get; private set; } = string.Empty;

    /// <summary>The line under the price: how long the listing lasts, from the server's setting. Empty until it is known.</summary>
    public string LifetimeLine => LifetimeDays is not int days ? string.Empty
        : $"Listed for {days:N0} day{(days == 1 ? string.Empty : "s")}, then back to your Vault."
          + (Tab == SellTab.Pack && Selected != null && !Selected.Listed ? " It moves from your pack into the Vault when you list it." : string.Empty);

    /// <summary>Why "List for N" is off, or null when it can be pressed.</summary>
    public string? ListBlocker
    {
        get
        {
            if (Busy) return "Listing…";
            if (Selected == null) return "Pick an item to list it.";
            if (Selected.Greyed) return Selected.Reason;
            if (Selected.Listed) return "It is already listed.";
            if (Price is not long price || price < 1) return "Set a whole price of at least 1 MMD.";
            if (ActiveCount is int count && ActiveCap is int cap && count >= cap)
                return $"You have {cap:N0} active listing{(cap == 1 ? string.Empty : "s")}, the most. Delist one first.";
            return null;
        }
    }

    /// <summary>
    /// Opens the screen's data: the Vault, the active listings and the sellability of what is shown. Called when the screen is shown.
    /// </summary>
    public void Open()
    {
        LoadVault();
        LoadListings(SellListingTab.Active);
    }

    public void ShowTab(SellTab tab)
    {
        if (tab == Tab) return;
        Tab = tab;
        Selected = null;
        _pendingPick = null;
        Rebuild();
        RefreshAnswers();
    }

    /// <summary>Picks an item of the shown tab, or clears the pick with zero.</summary>
    public void Pick(uint guid)
    {
        Selected = Items.FirstOrDefault(item => item.Guid == guid);
        Changed?.Invoke();
    }

    /// <summary>Leaves the price panel (a narrow window's Back).</summary>
    public void Back()
    {
        Selected = null;
        Changed?.Invoke();
    }

    /// <summary>Takes the typed price: digits only, so the field holds a whole number of MMD.</summary>
    public void SetPrice(string text)
    {
        PriceText = new string((text ?? string.Empty).Where(char.IsDigit).ToArray());
        Changed?.Invoke();
    }

    /// <summary>Lists the picked item at the price. A pack item is deposited first, by the server, and then listed.</summary>
    public void List()
    {
        if (ListBlocker != null || Selected is not { } item || Price is not long price) return;

        var fromPack = Tab == SellTab.Pack;
        Busy = true;
        Message = string.Empty;
        Changed?.Invoke();
        _client.Send(MarketProtocol.List, MarketProtocol.ListRequest(item.Guid, price), reply =>
        {
            Busy = false;
            if (!reply.Ok)
            {
                Message = reply.Message;
                Changed?.Invoke();
                return;
            }

            var result = MarketProtocol.ReadOutcome(reply.Body);
            Message = result.Message;
            if (result.Done) Selected = null;
            if (result.Done) PriceText = string.Empty;

            // a pack item the server deposited and then left in the Vault: show it there, picked, with the reason
            if (!result.Done && result.InVault && fromPack)
            {
                _pendingPick = item.Guid;
                ShowTabWithoutRefresh(SellTab.Vault);
            }

            // the item moved (listed, or deposited and left in the Vault): both lists change
            if (result.Done || result.InVault)
            {
                LoadVault();
                ReloadListings();
            }
            else
            {
                Changed?.Invoke();
            }
        });
    }

    /// <summary>Takes an active listing down; the item goes back to the Vault.</summary>
    public void Delist(long listingId)
    {
        Message = string.Empty;
        _client.Send(MarketProtocol.Delist, MarketProtocol.DelistRequest(listingId), reply =>
        {
            if (!reply.Ok)
            {
                Message = reply.Message;
                Changed?.Invoke();
                return;
            }

            Message = MarketProtocol.ReadOutcome(reply.Body).Message;
            LoadVault();
            ReloadListings();
        });
    }

    /// <summary>Shows one of the account's listing tabs. Looking at Sold clears the unseen sales.</summary>
    public void ShowListingTab(SellListingTab tab)
    {
        ListingTab = tab;
        RepriceListingId = null;
        LoadListings(tab);
        if (tab == SellListingTab.Sold) _sales?.Seen();
    }

    /// <summary>Opens the price field on an active listing, with its current price.</summary>
    public void StartReprice(long listingId)
    {
        if (Listings?.Rows.FirstOrDefault(row => row.Id == listingId) is not { } row) return;
        RepriceListingId = listingId;
        RepriceText = row.Price.ToString();
        Message = string.Empty;
        Changed?.Invoke();
    }

    /// <summary>Takes the typed new price as it is typed; it is checked when Set price is pressed. Silent, so the row's field keeps its focus.</summary>
    public void SetRepriceText(string text) => RepriceText = text ?? string.Empty;

    /// <summary>Closes the price field without changing the price.</summary>
    public void CancelReprice()
    {
        RepriceListingId = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Changes the open listing's price to the typed one. The listing keeps its id and its lifetime. The server's answer, or its refusal, is the
    /// message, and the listings are read again so the row shows the price the server has.
    /// </summary>
    public void Reprice()
    {
        if (RepriceListingId is not long listingId) return;

        if (WholePrice(RepriceText) is not long price)
        {
            RejectReprice("Type a whole number of MMD.");
            return;
        }

        if (price < 1)
        {
            RejectReprice("Set a whole price of at least 1 MMD.");
            return;
        }

        Message = string.Empty;
        _client.Send(MarketProtocol.Reprice, MarketProtocol.RepriceRequest(listingId, price), reply =>
        {
            // the field closes only when the server has made the change; a refusal keeps the typed price for another try
            if (!reply.Ok)
            {
                Message = reply.Message;
            }
            else
            {
                var result = MarketProtocol.ReadOutcome(reply.Body);
                Message = result.Message;
                if (result.Done && RepriceListingId == listingId) RepriceListingId = null;
            }
            ReloadListings();
        });
    }

    private void RejectReprice(string message)
    {
        Message = message;
        Changed?.Invoke();
    }

    /// <summary>The text as a whole number of MMD (digits only, and in range), or null when it isn't one.</summary>
    private static long? WholePrice(string text) => text.Length > 0 && text.All(char.IsDigit) && long.TryParse(text, out var price) ? price : null;

    /// <summary>How much time an active listing has left, in the server's clock: "6 days left", "9 hours left", "40 min left", or "ended".</summary>
    public string TimeLeft(ListingRow row)
    {
        if (Listings is not { } page || LifetimeDays is not int days) return string.Empty;
        var left = row.Created.AddDays(days) - page.ServerNow;
        if (left <= TimeSpan.Zero) return "ended";
        if (left.TotalDays >= 1) return Count((int)left.TotalDays, "day");
        if (left.TotalHours >= 1) return Count((int)left.TotalHours, "hour");
        return $"{Math.Max(1, (int)left.TotalMinutes)} min left";
    }

    private static string Count(int amount, string unit) => $"{amount} {unit}{(amount == 1 ? string.Empty : "s")} left";

    private void ShowTabWithoutRefresh(SellTab tab)
    {
        Tab = tab;
        Selected = null;
    }

    private void LoadVault()
    {
        _client.Send(MarketProtocol.VaultList, MarketProtocol.VaultListRequest(0, MarketProtocol.SellablePageSize), reply =>
        {
            if (!reply.Ok)
            {
                Message = reply.Message;
                _vault = Array.Empty<SellVaultItem>();
            }
            else
            {
                _vault = MarketProtocol.ReadVaultPage(reply.Body) ?? Array.Empty<SellVaultItem>();
            }
            Rebuild();
            RefreshAnswers();
        });
    }

    /// <summary>After a listing or delisting: the Active count and the shown tab are both stale.</summary>
    private void ReloadListings()
    {
        LoadListings(SellListingTab.Active);
        if (ListingTab != SellListingTab.Active) LoadListings(ListingTab);
    }

    private void LoadListings(SellListingTab tab)
    {
        var wire = tab switch { SellListingTab.Sold => MarketProtocol.SoldTab, SellListingTab.Expired => MarketProtocol.ExpiredTab, _ => MarketProtocol.ActiveTab };
        _client.Send(MarketProtocol.Listings, MarketProtocol.ListingsRequest(wire, MarketProtocol.ListingsPageSize), reply =>
        {
            if (reply.Ok)
            {
                var page = MarketProtocol.ReadListings(reply.Body);
                LifetimeDays = page.LifetimeDays;
                if (tab == SellListingTab.Active) ActiveCount = page.Total;
                if (tab == ListingTab) Listings = page;
            }
            else
            {
                Message = reply.Message;
            }
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Asks the server whether each shown item can be listed. Answers are kept by guid; an item not yet answered is drawn as sellable.
    /// </summary>
    private void RefreshAnswers()
    {
        var guids = Items.Select(item => item.Guid).Distinct().ToList();
        for (var start = 0; start < guids.Count; start += MarketProtocol.SellablePageSize)
        {
            var page = guids.Skip(start).Take(MarketProtocol.SellablePageSize).ToList();
            _client.Send(MarketProtocol.Sellable, MarketProtocol.SellableRequest(page), reply =>
            {
                if (!reply.Ok) return;
                foreach (var answer in MarketProtocol.ReadSellable(reply.Body)) _answers[answer.Guid] = answer;
                Rebuild();
            });
        }
    }

    /// <summary>Rebuilds the shown items from the pack or the Vault and the answers the server has given. Keeps the pick, or makes the pending one.</summary>
    private void Rebuild()
    {
        var items = Tab == SellTab.Pack ? PackItems() : VaultItems();
        Items = items;

        var pick = _pendingPick ?? Selected?.Guid;
        if (pick is uint guid)
        {
            var found = items.FirstOrDefault(item => item.Guid == guid);
            if (found != null || _pendingPick == null) Selected = found;
            if (found != null) _pendingPick = null;
        }
        Changed?.Invoke();
    }

    private IReadOnlyList<SellItem> PackItems() => _inventory.Snapshot.Items
        .Select(item => new SellItem(item.Id, item.Name, item.StackCount, item.Visual, 0, false, Sellable(item.Id), Reason(item.Id)))
        .ToList();

    private IReadOnlyList<SellItem> VaultItems() => _vault
        .Select(item => new SellItem(item.Guid, item.Name, item.StackSize, item.Visual, item.OverlaySecondary, item.State == "listed", Sellable(item.Guid), Reason(item.Guid)))
        .ToList();

    /// <summary>An item the server hasn't answered for yet is drawn as sellable: the server still refuses what it must.</summary>
    private bool Sellable(uint guid) => !_answers.TryGetValue(guid, out var answer) || answer.Sellable;

    private string Reason(uint guid) => _answers.TryGetValue(guid, out var answer) ? answer.Reason : string.Empty;

    private void OnInventoryChanged()
    {
        if (_disposed) return;
        if (Tab == SellTab.Pack)
        {
            Rebuild();
            RefreshAnswers();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inventory.Changed -= OnInventoryChanged;
        Changed = null;
    }
}
