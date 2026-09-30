namespace ACE.Database.Market
{
    /// <summary>
    /// What a market save job did. Only Saved changed anything.
    /// </summary>
    public enum MarketJobResult
    {
        Saved,

        // the job's own checks refused the request (not the job's kind of item, amounts that don't match)
        Refused,

        // a player's balance would have fallen below zero
        InsufficientFunds,

        // the market is paused (a failed ledger audit), which stops MMD withdrawals
        Paused,

        // the save failed
        Failed,
    }
}
