using System;
using System.Collections.Generic;
using System.IO;
using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Market;

/// <summary>The account as the server describes it for the Market window. Must match ACE.Server.Market.MarketChannelActions.</summary>
public sealed class MarketAccount
{
    public MarketAccount(long balance, bool frozen, bool paused, int vaultCount, int vaultCapacity, int listingCount, int listingCap, string character)
    {
        Balance = balance; Frozen = frozen; Paused = paused; VaultCount = vaultCount; VaultCapacity = vaultCapacity;
        ListingCount = listingCount; ListingCap = listingCap; Character = character;
    }

    /// <summary>The MMD balance</summary>
    public long Balance { get; }
    /// <summary>The account is banned, so its balance and Vault are frozen</summary>
    public bool Frozen { get; }
    /// <summary>The ledger audit paused the market: purchases and MMD withdrawals stop</summary>
    public bool Paused { get; }
    public int VaultCount { get; }
    public int VaultCapacity { get; }
    public int ListingCount { get; }
    public int ListingCap { get; }
    /// <summary>The character the window acts for: whose pack receives purchases</summary>
    public string Character { get; }
}

/// <summary>One item's answer from market.sellable: whether it can be listed, and why not when it can't.</summary>
public readonly record struct SellAnswer(uint Guid, bool Sellable, string Reason);

/// <summary>
/// The outcome of market.list and market.delist: whether it went through, whether the item is in the Vault now, and the server's message.
/// </summary>
public readonly record struct SellOutcome(bool Done, bool InVault, string Message);

/// <summary>One of the seller's items sold, as market.sold tells it.</summary>
public sealed record SaleNotice(long ListingId, string Item, string Buyer, long Price, long BalanceAfter);

/// <summary>One of the account's listings, as market.listings describes it. Times are when the server saw them.</summary>
public sealed record ListingRow(long Id, uint ItemGuid, string Name, long Price, string Status, DateTimeOffset Created, DateTimeOffset? Closed);

/// <summary>One tab of the account's listings: the server's clock and listing lifetime, the tab's total, and the rows shown.</summary>
public sealed record ListingsPage(DateTimeOffset ServerNow, int LifetimeDays, int Total, IReadOnlyList<ListingRow> Rows);

/// <summary>One Vault item for the Sell screen: its guid, name, stack, how it is drawn, and its Vault state (held, listed or withdrawing).</summary>
public sealed record SellVaultItem(uint Guid, string Name, int StackSize, string State, ItemVisual Visual, uint OverlaySecondary);

/// <summary>A ledger line: its sequence (the cursor), signed amount, balance after it, when, and what happened.</summary>
public sealed class LedgerLine
{
    public LedgerLine(long sequence, long amount, long balanceAfter, DateTimeOffset time, string text)
    {
        Sequence = sequence; Amount = amount; BalanceAfter = balanceAfter; Time = time; Text = text;
    }

    public long Sequence { get; }
    public long Amount { get; }
    public long BalanceAfter { get; }
    public DateTimeOffset Time { get; }
    public string Text { get; }
}

/// <summary>An item movement: its id (the cursor), when, its kind (<see cref="MarketProtocol.SoldMovement"/> and so on), and what happened.</summary>
public sealed class MovementLine
{
    public MovementLine(long id, DateTimeOffset time, string kind, string text)
    {
        Id = id; Time = time; Kind = kind; Text = text;
    }

    public long Id { get; }
    public DateTimeOffset Time { get; }
    public string Kind { get; }
    public string Text { get; }
}

/// <summary>One page of the account's history: the week's sales and purchases (on the newest page only), the ledger lines and item movements, and the cursors of the older pages.</summary>
public sealed class HistoryPage
{
    public HistoryPage(long balance, long earned, long spent, int itemsSold, int itemsBought, IReadOnlyList<LedgerLine> lines, long nextLines,
        IReadOnlyList<MovementLine> movements, long nextMovements)
    {
        Balance = balance; Earned = earned; Spent = spent; ItemsSold = itemsSold; ItemsBought = itemsBought;
        Lines = lines; NextLines = nextLines; Movements = movements; NextMovements = nextMovements;
    }

    public long Balance { get; }
    /// <summary>The MMD earned from sales this week, from the ledger</summary>
    public long Earned { get; }
    /// <summary>The MMD spent on purchases this week, from the ledger</summary>
    public long Spent { get; }
    public int ItemsSold { get; }
    public int ItemsBought { get; }
    public IReadOnlyList<LedgerLine> Lines { get; }
    /// <summary>The cursor of the next older ledger page, or <see cref="MarketProtocol.Newest"/> when there is none</summary>
    public long NextLines { get; }
    public IReadOnlyList<MovementLine> Movements { get; }
    /// <summary>The cursor of the next older movement page, or <see cref="MarketProtocol.Newest"/> when there is none</summary>
    public long NextMovements { get; }
}

/// <summary>Bodies of the Market's channel actions.</summary>
public static class MarketProtocol
{
    /// <summary>The account summary the window's top bar and Activity read. Needs the market station.</summary>
    public const string Account = "market.account";

    /// <summary>The ledger and item history page. Needs the market station.</summary>
    public const string History = "market.history";

    /// <summary>Trade notes withdrawn into the pack. Needs the market station.</summary>
    public const string WithdrawNotes = "market.withdraw_notes";

    /// <summary>The cursor that asks for the newest page. Must match ACE.Server.Market.MarketActivityActions.Newest.</summary>
    public const long Newest = -1;

    /// <summary>The lines per ledger and movement page. Must match ACE.Database.Market.MarketHistory.DefaultLimit.</summary>
    public const int HistoryPageSize = 50;

    /// <summary>The movement kinds of a sale and a purchase. Each is also a ledger line, so the All tab leaves these movements out.</summary>
    public const string SoldMovement = "sold";
    public const string BoughtMovement = "bought";

    public static byte[] HistoryRequest(long linesBefore, long movementsBefore) => ChannelWire.Body(w =>
    {
        w.Write(linesBefore);
        w.Write(movementsBefore);
    });

    public static HistoryPage ReadHistory(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var balance = reader.ReadInt64();
        var earned = reader.ReadInt64();
        var spent = reader.ReadInt64();
        var itemsSold = reader.ReadInt32();
        var itemsBought = reader.ReadInt32();

        var lines = new LedgerLine[reader.ReadInt32()];
        for (var index = 0; index < lines.Length; index++)
        {
            var sequence = reader.ReadInt64();
            var amount = reader.ReadInt64();
            var balanceAfter = reader.ReadInt64();
            var time = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
            lines[index] = new LedgerLine(sequence, amount, balanceAfter, time, ChannelWire.ReadString(reader));
        }
        var nextLines = reader.ReadInt64();

        var movements = new MovementLine[reader.ReadInt32()];
        for (var index = 0; index < movements.Length; index++)
        {
            var id = reader.ReadInt64();
            var time = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
            var kind = ChannelWire.ReadString(reader);
            movements[index] = new MovementLine(id, time, kind, ChannelWire.ReadString(reader));
        }
        var nextMovements = reader.ReadInt64();

        return new HistoryPage(balance, earned, spent, itemsSold, itemsBought, lines, nextLines, movements, nextMovements);
    }

    public static byte[] WriteHistory(HistoryPage page) => ChannelWire.Body(w =>
    {
        w.Write(page.Balance);
        w.Write(page.Earned);
        w.Write(page.Spent);
        w.Write(page.ItemsSold);
        w.Write(page.ItemsBought);
        w.Write(page.Lines.Count);
        foreach (var line in page.Lines)
        {
            w.Write(line.Sequence);
            w.Write(line.Amount);
            w.Write(line.BalanceAfter);
            w.Write(line.Time.ToUnixTimeSeconds());
            ChannelWire.WriteString(w, line.Text);
        }
        w.Write(page.NextLines);
        w.Write(page.Movements.Count);
        foreach (var movement in page.Movements)
        {
            w.Write(movement.Id);
            w.Write(movement.Time.ToUnixTimeSeconds());
            ChannelWire.WriteString(w, movement.Kind);
            ChannelWire.WriteString(w, movement.Text);
        }
        w.Write(page.NextMovements);
    });

    /// <summary>Pushed to the seller when one of their items sells, by the game or the website. Needs no station. Must match ACE.Server.Market.SaleNotices.</summary>
    public const string Sold = "market.sold";

    /// <summary>A market.sold body: the listing, the item, the buyer, the price, then the seller's balance after it.</summary>
    public static SaleNotice ReadSold(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return new SaleNotice(reader.ReadInt64(), ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), reader.ReadInt64(), reader.ReadInt64());
    }

    public static byte[] WriteSold(SaleNotice sale) => ChannelWire.Body(w =>
    {
        w.Write(sale.ListingId);
        ChannelWire.WriteString(w, sale.Item);
        ChannelWire.WriteString(w, sale.Buyer);
        w.Write(sale.Price);
        w.Write(sale.BalanceAfter);
    });

    public static byte[] WithdrawRequest(long amount) => ChannelWire.Body(w => w.Write(amount));

    /// <summary>The reply: whether the notes moved, and the message the player is told.</summary>
    public static (bool Accepted, string Message) ReadWithdraw(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return (reader.ReadByte() != 0, ChannelWire.ReadString(reader));
    }

    public static byte[] WriteWithdraw(bool accepted, string message) => ChannelWire.Body(w =>
    {
        w.Write((byte)(accepted ? 1 : 0));
        ChannelWire.WriteString(w, message);
    });

    public static MarketAccount ReadAccount(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return new MarketAccount(reader.ReadInt64(), reader.ReadByte() != 0, reader.ReadByte() != 0, reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt32(), reader.ReadInt32(), ChannelWire.ReadString(reader));
    }

    /// <summary>The most items one market.sellable names. Must match ACE.Server.Market.MarketSellActions.SellablePageSize.</summary>
    public const int SellablePageSize = 100;
    /// <summary>The tabs of market.listings, the first byte of its request. Must match ACE.Server.Market.MarketSellActions.</summary>
    public const byte ActiveTab = 0, SoldTab = 1, ExpiredTab = 2;

    public const string Sellable = "market.sellable";
    public const string List = "market.list";
    public const string Delist = "market.delist";
    public const string Reprice = "market.reprice";
    public const string Listings = "market.listings";
    /// <summary>
    /// The Vault's page, which the Sell screen's Vault tab reads: vault.list's body and reply, at the market station (vault.list needs the Vault
    /// chest's). Must match ACE.Server.Market.MarketSellActions.VaultPage.
    /// </summary>
    public const string VaultList = "market.vault";

    /// <summary>A count, then the item guids (pack or Vault), at most SellablePageSize.</summary>
    public static byte[] SellableRequest(IReadOnlyList<uint> guids) => ChannelWire.Body(w =>
    {
        w.Write(guids.Count);
        foreach (var guid in guids) w.Write(guid);
    });

    public static IReadOnlyList<SellAnswer> ReadSellable(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var count = reader.ReadInt32();
        var answers = new List<SellAnswer>(count);
        for (var index = 0; index < count; index++)
            answers.Add(new SellAnswer(reader.ReadUInt32(), reader.ReadByte() != 0, ChannelWire.ReadString(reader)));
        return answers;
    }

    public static byte[] ListRequest(uint guid, long price) => ChannelWire.Body(w => { w.Write(guid); w.Write(price); });

    /// <summary>The reply of market.list and market.delist: a byte done, a byte for the item being in the Vault, then the message.</summary>
    public static SellOutcome ReadOutcome(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return new SellOutcome(reader.ReadByte() != 0, reader.ReadByte() != 0, ChannelWire.ReadString(reader));
    }

    public static byte[] DelistRequest(long listingId) => ChannelWire.Body(w => w.Write(listingId));

    /// <summary>A listing id, then its new whole-MMD price.</summary>
    public static byte[] RepriceRequest(long listingId, long price) => ChannelWire.Body(w => { w.Write(listingId); w.Write(price); });

    /// <summary>A tab (ActiveTab, SoldTab or ExpiredTab) and the most listings to show.</summary>
    public static byte[] ListingsRequest(byte tab, int count) => ChannelWire.Body(w => { w.Write(tab); w.Write(count); });

    /// <summary>
    /// The most listings one tab asks for: the default active cap, so the Active tab shows every one. Must match ACE.Server.Market.MarketSellActions.ListingsPageSize.
    /// </summary>
    public const int ListingsPageSize = 200;

    public static ListingsPage ReadListings(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var now = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
        var lifetime = reader.ReadInt32();
        var total = reader.ReadInt32();
        var shown = reader.ReadInt32();
        var rows = new List<ListingRow>(shown);
        for (var index = 0; index < shown; index++)
        {
            var id = reader.ReadInt64();
            var guid = reader.ReadUInt32();
            var name = ChannelWire.ReadString(reader);
            var price = reader.ReadInt64();
            var status = ChannelWire.ReadString(reader);
            var created = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
            var closed = reader.ReadInt64();
            rows.Add(new ListingRow(id, guid, name, price, status, created, closed == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(closed)));
        }
        return new ListingsPage(now, lifetime, total, rows);
    }

    /// <summary>The first page of the Vault, every item whatever the search, as the Vault's own list asks.</summary>
    public static byte[] VaultListRequest(int offset, int count) => ChannelWire.Body(w =>
    {
        ChannelWire.WriteString(w, string.Empty);
        w.Write(offset);
        w.Write(count);
    });

    /// <summary>The Vault's items on the page, or null when the Vault is not available. Reads only what the Sell screen draws.</summary>
    public static IReadOnlyList<SellVaultItem>? ReadVaultPage(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        if (reader.ReadByte() == 0) return null;
        reader.ReadInt64(); // balance
        reader.ReadInt32(); // capacity
        reader.ReadInt32(); // the Vault's count
        reader.ReadInt32(); // the total matching
        var count = reader.ReadInt32();
        var items = new List<SellVaultItem>(count);
        for (var index = 0; index < count; index++)
        {
            var guid = reader.ReadUInt32();
            var name = ChannelWire.ReadString(reader);
            reader.ReadUInt32(); // item type
            var stack = reader.ReadInt32();
            reader.ReadInt32(); // value
            var state = ChannelWire.ReadString(reader);
            ChannelWire.ReadString(reader); // deposited by
            reader.ReadInt64(); // deposited time
            var plate = reader.ReadUInt32();
            var underlay = reader.ReadUInt32();
            var icon = reader.ReadUInt32();
            var overlay = reader.ReadUInt32();
            var overlaySecondary = reader.ReadUInt32();
            var uiEffects = reader.ReadInt32();
            items.Add(new SellVaultItem(guid, name, stack, state, new ItemVisual(icon, underlay, overlay, unchecked((uint)uiEffects), plate), overlaySecondary));
        }
        return items;
    }

    public static byte[] WriteAccount(MarketAccount account) => ChannelWire.Body(w =>
    {
        w.Write(account.Balance);
        w.Write((byte)(account.Frozen ? 1 : 0));
        w.Write((byte)(account.Paused ? 1 : 0));
        w.Write(account.VaultCount);
        w.Write(account.VaultCapacity);
        w.Write(account.ListingCount);
        w.Write(account.ListingCap);
        ChannelWire.WriteString(w, account.Character);
    });
}

/// <summary>The Market window's view of the server: loads the account over the channel. Dispose stops its callbacks.</summary>
public sealed class MarketClient : IDisposable
{
    private readonly IServerChannel _channel;
    private readonly List<IDisposable> _requests = new();
    private bool _disposed;

    public MarketClient(IServerChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    /// <summary>The last account the server sent; null until the first answer.</summary>
    public MarketAccount? Account { get; private set; }
    /// <summary>The server's plain message when there is no account to show: a refusal or a failure.</summary>
    public string Notice { get; private set; } = string.Empty;
    public event EventHandler? Changed;

    /// <summary>Loads the account. Called when the window opens.</summary>
    public void Start()
    {
        if (!_channel.IsAvailable)
        {
            Set("The LegACEy server channel is not available in this client.");
            return;
        }
        Send(MarketProtocol.Account, Array.Empty<byte>(), reply =>
        {
            if (reply.Ok)
            {
                Account = MarketProtocol.ReadAccount(reply.Body);
                Set(string.Empty);
                return;
            }
            Set(reply.Message);
        });
    }

    /// <summary>Loads one page of the history, older than the cursors (<see cref="MarketProtocol.Newest"/> for the newest). The callback gets the page, or the server's message.</summary>
    public void LoadHistory(long linesBefore, long movementsBefore, Action<HistoryPage?, string> completed)
    {
        Send(MarketProtocol.History, MarketProtocol.HistoryRequest(linesBefore, movementsBefore), reply =>
            completed(reply.Ok ? MarketProtocol.ReadHistory(reply.Body) : null, reply.Ok ? string.Empty : reply.Message));
    }

    /// <summary>Withdraws MMD as trade notes into the pack. The callback gets whether they moved and the server's message. A withdrawal that moved reloads the account.</summary>
    public void WithdrawNotes(long amount, Action<bool, string> completed)
    {
        Send(MarketProtocol.WithdrawNotes, MarketProtocol.WithdrawRequest(amount), reply =>
        {
            if (!reply.Ok)
            {
                completed(false, reply.Message);
                return;
            }
            var (accepted, message) = MarketProtocol.ReadWithdraw(reply.Body);
            if (accepted) Start();
            completed(accepted, message);
        });
    }

    /// <summary>Sends one of the Market's channel actions and hands its reply to the callback, on the render thread.</summary>
    public void Send(string action, byte[] body, Action<ChannelReply> completed)
    {
        if (_disposed) return;
        IDisposable? request = null;
        var done = false;
        request = _channel.Request(action, body, reply =>
        {
            done = true;
            if (request != null) _requests.Remove(request);
            if (_disposed) return;
            completed(reply);
        });
        // A reply delivered synchronously (request not sent) has already run.
        if (!done && !_disposed) _requests.Add(request);
    }

    private void Set(string notice)
    {
        Notice = notice;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var request in _requests.ToArray()) request.Dispose();
        _requests.Clear();
        Changed = null;
    }
}
