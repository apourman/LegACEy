namespace ACE.Database.Market
{
    /// <summary>
    /// What a market save job did. Only Saved changed anything, and Unknown may have.
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

        // the account was banned when the save was about to happen: a ban freezes the Vault and the balance
        Banned,

        // the save failed, and the database shows nothing of it
        Failed,

        // the save failed in a way that may still have committed (a lost connection during the commit), and the database couldn't be read to tell.
        // The database is the truth: callers must not put anything back into the world, or they could duplicate what was saved.
        Unknown,
    }
}
