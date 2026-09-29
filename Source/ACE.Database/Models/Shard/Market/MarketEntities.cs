using System;
using System.Collections.Generic;

namespace ACE.Database.Models.Shard.Market;

public class Listing
{
    public long Id { get; set; }

    public uint ItemGuid { get; set; }

    public uint SellerAccountId { get; set; }

    public uint SellerCharacterId { get; set; }

    /// <summary>
    /// Whole MMD for the whole stack, at least 1
    /// </summary>
    public long Price { get; set; }

    /// <summary>
    /// One of <see cref="ListingStatus"/>
    /// </summary>
    public string Status { get; set; }

    public uint? BuyerAccountId { get; set; }

    public uint? BuyerCharacterId { get; set; }

    public DateTime CreatedTime { get; set; }

    public DateTime? ClosedTime { get; set; }

    /// <summary>
    /// Optimistic concurrency token: increment it on every change
    /// </summary>
    public uint RowVersion { get; set; }
}

/// <summary>
/// A player account's MMD balance. System accounts have no balance row; their totals are summed from their ledger entries.
/// </summary>
public class AccountBalance
{
    public uint AccountId { get; set; }

    /// <summary>
    /// MySQL keeps this at or above 0
    /// </summary>
    public long Balance { get; set; }

    /// <summary>
    /// The last per-account ledger sequence number used; the history cursor
    /// </summary>
    public long LastSequence { get; set; }

    /// <summary>
    /// Optimistic concurrency token: increment it on every change
    /// </summary>
    public uint RowVersion { get; set; }
}

/// <summary>
/// A double-entry ledger transfer. Append-only: MySQL triggers reject UPDATE and DELETE. Corrections are new reversal or admin_adjust transfers.
/// </summary>
public class Transfer
{
    public long Id { get; set; }

    /// <summary>
    /// One of <see cref="TransferKind"/>
    /// </summary>
    public string Kind { get; set; }

    public uint? ActorAccountId { get; set; }

    public uint? ActorCharacterId { get; set; }

    public long? ListingId { get; set; }

    public long? TicketId { get; set; }

    /// <summary>
    /// The idempotency key of the request that made this transfer (the request's account is the actor)
    /// </summary>
    public string RequestKey { get; set; }

    /// <summary>
    /// The transfer this one reverses; a transfer can be reversed once
    /// </summary>
    public long? ReversesTransferId { get; set; }

    /// <summary>
    /// Required for admin_adjust and reversal
    /// </summary>
    public string Memo { get; set; }

    public DateTime CreatedTime { get; set; }

    public virtual ICollection<LedgerEntry> Entries { get; set; } = new List<LedgerEntry>();
}

/// <summary>
/// One leg of a transfer: either a player account (with a sequence number and the balance after) or a system account. A transfer's entries add up to zero.
/// </summary>
public class LedgerEntry
{
    public long Id { get; set; }

    public long TransferId { get; set; }

    public uint? AccountId { get; set; }

    /// <summary>
    /// One of <see cref="Market.SystemAccount"/>, when AccountId is null
    /// </summary>
    public string SystemAccount { get; set; }

    public long Amount { get; set; }

    public long? Sequence { get; set; }

    public long? BalanceAfter { get; set; }

    /// <summary>
    /// Optional note on this leg, such as the fee policy's reason
    /// </summary>
    public string Memo { get; set; }

    public virtual Transfer Transfer { get; set; }
}

public class ItemEvent
{
    public long Id { get; set; }

    public uint ItemGuid { get; set; }

    public uint AccountId { get; set; }

    public uint? CharacterId { get; set; }

    /// <summary>
    /// One of <see cref="ItemEventKind"/>
    /// </summary>
    public string Kind { get; set; }

    public long? ListingId { get; set; }

    /// <summary>
    /// The ledger transfer this event belongs to, such as the note_deposit that destroyed a stack of notes
    /// </summary>
    public long? TransferId { get; set; }

    /// <summary>
    /// The stack size moved, where it matters (destroyed notes)
    /// </summary>
    public int? Quantity { get; set; }

    public DateTime EventTime { get; set; }
}

/// <summary>
/// A game bridge work item that the game server claims and completes
/// </summary>
public class Ticket
{
    public long Id { get; set; }

    public string Kind { get; set; }

    public uint AccountId { get; set; }

    public uint? CharacterId { get; set; }

    /// <summary>
    /// JSON
    /// </summary>
    public string Payload { get; set; }

    /// <summary>
    /// One of <see cref="TicketStatus"/>
    /// </summary>
    public string Status { get; set; }

    public string ResultCode { get; set; }

    public string ResultMessage { get; set; }

    /// <summary>
    /// Unique per account
    /// </summary>
    public string IdempotencyKey { get; set; }

    public DateTime CreatedTime { get; set; }

    public DateTime? ClaimedTime { get; set; }

    public DateTime? FinishedTime { get; set; }
}

/// <summary>
/// A stored API result, so a replayed request returns the same answer
/// </summary>
public class Request
{
    public uint AccountId { get; set; }

    public string IdempotencyKey { get; set; }

    public string Kind { get; set; }

    /// <summary>
    /// JSON
    /// </summary>
    public string Result { get; set; }

    public DateTime CreatedTime { get; set; }
}

public class LinkCode
{
    public byte[] CodeHash { get; set; }

    public uint AccountId { get; set; }

    public uint CharacterId { get; set; }

    public DateTime ExpiresTime { get; set; }

    public DateTime? UsedTime { get; set; }
}

public class PluginToken
{
    public long Id { get; set; }

    public byte[] TokenHash { get; set; }

    public uint AccountId { get; set; }

    public string Label { get; set; }

    public DateTime CreatedTime { get; set; }

    public DateTime? LastUsedTime { get; set; }

    public DateTime ExpiresTime { get; set; }

    public DateTime? RevokedTime { get; set; }

    /// <summary>
    /// A fingerprint of the account's password hash at issue; a password change revokes the token
    /// </summary>
    public byte[] PasswordFingerprint { get; set; }
}

/// <summary>
/// An item type (WCID) the Vault refuses
/// </summary>
public class BlockedWcid
{
    public uint Wcid { get; set; }

    public string Reason { get; set; }

    public uint AddedByAccountId { get; set; }

    public DateTime AddedTime { get; set; }
}
