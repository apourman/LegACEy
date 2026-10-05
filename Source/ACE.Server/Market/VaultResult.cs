namespace ACE.Server.Market
{
    public enum VaultOutcome
    {
        Deposited,
        Withdrawn,

        // the item is the player's and the database has it in their pack, but the pack had no room by the time the save finished: it appears at the next login
        WithdrawnAtLogin,

        // refusals, each with its own message
        NotAvailable,
        Busy,
        InTrade,
        NotInPack,
        Worn,
        Attuned,
        ContainsAttuned,
        PetOut,
        ContainerNotEmpty,
        BlockedWcid,
        VaultFull,
        NotInVault,
        Listed,
        Withdrawing,
        NoPackSpace,
        UniqueLimit,

        // the channel
        RecentPlayerFight,
        Trading,
        Channelling,
        Interrupted,

        // the database job failed; the item is where it was
        SaveFailed,

        // the save failed in a way that may still have committed, and the database couldn't be read to tell: nothing is put back in the world,
        // and the next login shows where the item or notes are
        Unconfirmed,

        // the account is banned, which freezes the Vault and the balance
        Banned,

        // trade notes (MMD): instant, never through the channel
        NotesDeposited,
        NotesWithdrawn,
        NotesWithdrawnAtLogin,
        NoNotes,
        InvalidAmount,
        InsufficientFunds,

        // the market is paused (a failed ledger audit): MMD withdrawals are refused until /market resume
        Paused,
    }

    public sealed class VaultResult
    {
        public VaultOutcome Outcome { get; }

        /// <summary>
        /// What the player is told
        /// </summary>
        public string Message { get; }

        public uint ItemGuid { get; }

        /// <summary>
        /// The MMD moved, for trade note deposits and withdrawals
        /// </summary>
        public long Amount { get; }

        /// <summary>
        /// The account balance after a trade note deposit or withdrawal
        /// </summary>
        public long Balance { get; }

        public bool Success => Outcome == VaultOutcome.Deposited || Outcome == VaultOutcome.Withdrawn || Outcome == VaultOutcome.WithdrawnAtLogin
            || Outcome == VaultOutcome.NotesDeposited || Outcome == VaultOutcome.NotesWithdrawn || Outcome == VaultOutcome.NotesWithdrawnAtLogin;

        public VaultResult(VaultOutcome outcome, string message, uint itemGuid)
        {
            Outcome = outcome;
            Message = message;
            ItemGuid = itemGuid;
        }

        public VaultResult(VaultOutcome outcome, string message, long amount, long balance)
        {
            Outcome = outcome;
            Message = message;
            Amount = amount;
            Balance = balance;
        }

        public override string ToString() => $"{Outcome}: {Message}";
    }
}
