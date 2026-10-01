namespace ACE.Database.Models.Shard.Market;

// The string values the market tables' CHECK constraints allow

public static class VaultItemState
{
    public const string Held = "held";
    public const string Listed = "listed";
    public const string Withdrawing = "withdrawing";
}

public static class ListingStatus
{
    public const string Active = "active";
    public const string Sold = "sold";
    public const string Delisted = "delisted";
    public const string Expired = "expired";
    public const string BanReturned = "ban_returned";
}

public static class TransferKind
{
    public const string NoteDeposit = "note_deposit";
    public const string NoteWithdraw = "note_withdraw";
    public const string Purchase = "purchase";
    public const string AdminAdjust = "admin_adjust";
    public const string Reversal = "reversal";
}

public static class SystemAccount
{
    public const string Notes = "NOTES";
    public const string Fees = "FEES";
    public const string Admin = "ADMIN";
}

public static class ItemEventKind
{
    public const string Deposit = "deposit";
    public const string Withdraw = "withdraw";
    public const string List = "list";
    public const string Delist = "delist";
    public const string Expire = "expire";
    public const string Sold = "sold";
    public const string Bought = "bought";
    public const string BanReturn = "ban_return";
    public const string Admin = "admin";
}

public static class TicketStatus
{
    public const string Waiting = "WAITING";
    public const string Claimed = "CLAIMED";
    public const string Done = "DONE";
    public const string Failed = "FAILED";
}

/// <summary>
/// The game bridge's work: the website and plugin ask for these, and the game server does them
/// </summary>
public static class TicketKind
{
    public const string VaultWithdraw = "vault_withdraw";
    public const string MmdWithdraw = "mmd_withdraw";

    // in the contract, but asking for one needs a live-inventory picker that the in-game UI work will build: the game fails it as unsupported
    public const string VaultDeposit = "vault_deposit";
}
