namespace ACE.Server.Market
{
    public enum VaultOutcome
    {
        Deposited,
        Withdrawn,

        // refusals, each with its own message
        NotAvailable,
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

        public bool Success => Outcome == VaultOutcome.Deposited || Outcome == VaultOutcome.Withdrawn;

        public VaultResult(VaultOutcome outcome, string message, uint itemGuid)
        {
            Outcome = outcome;
            Message = message;
            ItemGuid = itemGuid;
        }

        public override string ToString() => $"{Outcome}: {Message}";
    }
}
