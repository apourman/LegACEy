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

        // the channel (ticket 04)
        RecentPlayerFight,
        Trading,
        Channelling,
        Interrupted,

        // the database job failed; the item is where it was
        SaveFailed,
    }

    public sealed class VaultResult
    {
        public VaultOutcome Outcome { get; }

        /// <summary>
        /// What the player is told
        /// </summary>
        public string Message { get; }

        public uint ItemGuid { get; }

        public bool Success => Outcome == VaultOutcome.Deposited || Outcome == VaultOutcome.Withdrawn || Outcome == VaultOutcome.WithdrawnAtLogin;

        public VaultResult(VaultOutcome outcome, string message, uint itemGuid)
        {
            Outcome = outcome;
            Message = message;
            ItemGuid = itemGuid;
        }

        public override string ToString() => $"{Outcome}: {Message}";
    }
}
