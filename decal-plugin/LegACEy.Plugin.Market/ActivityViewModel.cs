using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LegACEy.Plugin.Market;

/// <summary>The ledger's tabs: every event, the MMD lines only, or the item movements only.</summary>
public enum LedgerTab { All, Mmd, Items }

/// <summary>One row of the ledger: when, what happened, and for a ledger line its signed amount and the balance after it.</summary>
public sealed record ActivityRow(DateTimeOffset Time, string Text, long? Amount, long? BalanceAfter);

/// <summary>
/// The Activity screen's state: the history pages loaded so far, the tab, the week's figures and the trade note withdrawal. The pages are the
/// server's, newest first; each older page is asked for by the oldest cursor the window holds, so a stream with no older page asks for nothing.
/// </summary>
public sealed class ActivityViewModel
{
    private readonly MarketClient _client;
    private readonly List<LedgerLine> _lines = new();
    private readonly List<MovementLine> _movements = new();
    private long _linesBefore = MarketProtocol.Newest;
    private long _movementsBefore = MarketProtocol.Newest;
    private bool _linesMore;
    private bool _movementsMore;
    private bool _loaded;
    private bool _requesting;
    private bool _withdrawing;
    private bool _keepNotice;
    private int _generation;

    public ActivityViewModel(MarketClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Raised when anything the screen shows has changed.</summary>
    public event EventHandler? Changed;

    public LedgerTab Tab { get; private set; } = LedgerTab.All;
    /// <summary>The MMD earned from sales this week, from the ledger</summary>
    public long Earned { get; private set; }
    /// <summary>The MMD spent on purchases this week, from the ledger</summary>
    public long Spent { get; private set; }
    public int ItemsSold { get; private set; }
    public int ItemsBought { get; private set; }
    /// <summary>The server's plain message from the last withdrawal, or from a history request that failed</summary>
    public string Notice { get; private set; } = string.Empty;
    /// <summary>The notice says a withdrawal went through, not why it didn't</summary>
    public bool NoticeGood { get; private set; }
    /// <summary>The account the tiles show: its balance and listings. Null until the server has answered.</summary>
    public MarketAccount? Account => _client.Account;
    public bool Requesting => _requesting;
    /// <summary>Whether a withdrawal is waiting for the server, so the button is held</summary>
    public bool Withdrawing => _withdrawing;

    /// <summary>
    /// The rows for the tab, newest first. The All tab shows the ledger's lines and the item movements except a sale's or purchase's, which the ledger
    /// already has as a line. It also shows an entry only from the time both streams are complete back to: the oldest loaded time of any stream that
    /// has an older page. So an older page can never put an entry out of order.
    /// </summary>
    public IReadOnlyList<ActivityRow> Rows
    {
        get
        {
            var floor = Floor();
            var lines = Tab == LedgerTab.Items ? Enumerable.Empty<ActivityRow>() : _lines.Select(l => new ActivityRow(l.Time, l.Text, l.Amount, l.BalanceAfter));
            var movements = Tab == LedgerTab.Mmd ? Enumerable.Empty<ActivityRow>() : _movements
                .Where(m => Tab == LedgerTab.Items || (m.Kind != MarketProtocol.SoldMovement && m.Kind != MarketProtocol.BoughtMovement))
                .Select(m => new ActivityRow(m.Time, m.Text, null, null));

            var rows = lines.Concat(movements);
            if (Tab == LedgerTab.All && floor is DateTimeOffset from) rows = rows.Where(row => row.Time >= from);

            return rows.OrderByDescending(row => row.Time).ToList();
        }
    }

    /// <summary>The balance after each loaded ledger line with its time, oldest first, for the chart</summary>
    public IReadOnlyList<(DateTimeOffset Time, long Balance)> BalanceSeries =>
        _lines.AsEnumerable().Reverse().Select(l => (l.Time, l.BalanceAfter)).ToList();

    /// <summary>Loads the newest page, from the top. Called when the Activity screen opens.</summary>
    public void Load() => Load(keepNotice: false);

    /// <summary>Loads the next older page of both streams, if either has one. Nothing is asked while a page is still on its way.</summary>
    public void LoadOlder()
    {
        if (HasOlder()) Request();
    }

    public void SelectTab(LedgerTab tab)
    {
        if (tab == Tab) return;
        Tab = tab;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Withdraws the amount typed as trade notes. The server's message is the notice; a withdrawal that moved reloads the history and the account.
    /// A second press while one is waiting for the server sends nothing.
    /// </summary>
    public void WithdrawNotes(string amountText)
    {
        if (_withdrawing) return;

        if (!long.TryParse(amountText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
        {
            Notice = "Type how many MMD to withdraw.";
            NoticeGood = false;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _withdrawing = true;
        Changed?.Invoke(this, EventArgs.Empty);

        _client.WithdrawNotes(amount, (accepted, message) =>
        {
            _withdrawing = false;
            Notice = message;
            NoticeGood = accepted;
            if (accepted) Load(keepNotice: true);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private bool HasOlder() => _loaded && !_requesting && (_linesMore || _movementsMore);

    private void Load(bool keepNotice)
    {
        _generation++;
        _lines.Clear();
        _movements.Clear();
        _linesBefore = MarketProtocol.Newest;
        _movementsBefore = MarketProtocol.Newest;
        _loaded = false;
        _keepNotice = keepNotice;
        Request();
    }

    private DateTimeOffset? Floor()
    {
        DateTimeOffset? floor = null;
        if (_linesMore && _lines.Count > 0) floor = _lines[_lines.Count - 1].Time;
        if (_movementsMore && _movements.Count > 0 && (floor == null || _movements[_movements.Count - 1].Time > floor)) floor = _movements[_movements.Count - 1].Time;
        return floor;
    }

    private void Request()
    {
        var generation = _generation;
        var newest = _linesBefore == MarketProtocol.Newest && _movementsBefore == MarketProtocol.Newest;
        var keepNotice = _keepNotice;
        _keepNotice = false;
        _requesting = true;
        Changed?.Invoke(this, EventArgs.Empty);

        _client.LoadHistory(_linesBefore, _movementsBefore, (page, message) =>
        {
            // a page asked for before Load started over is not one the window shows
            if (generation != _generation) return;

            _requesting = false;

            if (page == null)
            {
                Notice = message;
                NoticeGood = false;
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!keepNotice) Notice = string.Empty;

            // the week's figures come with the newest page only
            if (newest)
            {
                Earned = page.Earned;
                Spent = page.Spent;
                ItemsSold = page.ItemsSold;
                ItemsBought = page.ItemsBought;
            }

            _lines.AddRange(page.Lines);
            _movements.AddRange(page.Movements);
            if (page.Lines.Count > 0) _linesBefore = page.Lines[page.Lines.Count - 1].Sequence;
            if (page.Movements.Count > 0) _movementsBefore = page.Movements[page.Movements.Count - 1].Id;
            _linesMore = page.NextLines != MarketProtocol.Newest;
            _movementsMore = page.NextMovements != MarketProtocol.Newest;
            _loaded = true;

            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}
