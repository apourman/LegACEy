namespace ACE.MarketApi
{
    /// <summary>
    /// Whether purchases (and MMD withdrawals) are paused server-wide, as a failed ledger audit does
    /// </summary>
    public interface IMarketPause
    {
        bool IsPaused { get; }
    }

    /// <summary>
    /// Never paused. A stand-in until the ledger audit (ticket 10) keeps the real pause.
    /// </summary>
    public sealed class NoMarketPause : IMarketPause
    {
        public bool IsPaused => false;
    }
}
