using System;
using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Market;

/// <summary>
/// The seller's sale notices for the session: each market.sold push shows a toast over the game and a green [Market] chat line, and counts on
/// the Sell badge until the player looks at the Sold tab, or the session ends. It listens whether or not the Market window is open. A sale made
/// while the player was offline is not pushed; the ledger has it.
/// </summary>
public sealed class MarketSales : IDisposable
{
    private readonly Action<string, string> _toast;
    private readonly Action<string> _chat;
    private readonly IDisposable _subscription;

    /// <param name="toast">shows a toast over the game: a title and a line</param>
    /// <param name="chat">writes a green line to the chat</param>
    public MarketSales(IServerChannel channel, Action<string, string> toast, Action<string> chat)
    {
        if (channel == null) throw new ArgumentNullException(nameof(channel));
        _toast = toast ?? throw new ArgumentNullException(nameof(toast));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _subscription = channel.Subscribe(MarketProtocol.Sold, OnSold);
    }

    /// <summary>Sales the player hasn't seen on the Sold tab.</summary>
    public int Unseen { get; private set; }

    /// <summary>Raised when <see cref="Unseen"/> changes. On the render thread.</summary>
    public event Action? Changed;

    /// <summary>The player looked at the Sold tab, or the session ended: another character or account starts with none.</summary>
    public void Seen()
    {
        if (Unseen == 0) return;
        Unseen = 0;
        Changed?.Invoke();
    }

    private void OnSold(byte[] body)
    {
        SaleNotice sale;
        // a malformed push is dropped, rather than turning the Market off
        try { sale = MarketProtocol.ReadSold(body); }
        catch (Exception error) when (error is System.IO.EndOfStreamException || error is System.IO.InvalidDataException) { return; }
        Unseen++;
        _toast($"Sold {sale.Item}", $"{sale.Buyer} paid {sale.Price:N0} MMD. Your balance is {sale.BalanceAfter:N0} MMD.");
        _chat($"[Market] Your {sale.Item} sold to {sale.Buyer} for {sale.Price:N0} MMD.");
        Changed?.Invoke();
    }

    public void Dispose() => _subscription.Dispose();
}
