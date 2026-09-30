using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    public enum CorrectionOutcome
    {
        Done,

        // refusals: nothing was written
        InvalidAmount,
        InvalidMemo,
        UnknownTransfer,
        AlreadyReversed,
        Unbalanced,
        InsufficientFunds,

        // lost the race for a balance row every time
        Busy,
    }

    /// <param name="TransferId">the admin_adjust or reversal transfer written</param>
    /// <param name="Balance">for an adjustment, the account's balance after it</param>
    public sealed record CorrectionResult(CorrectionOutcome Outcome, long TransferId = 0, long Balance = 0);

    /// <summary>
    /// Admin corrections to the append-only ledger: each is a new transfer, recorded with the admin and a required memo, added through Ledger.TryAdd and saved once.
    /// A save that lost a race for a balance row (or for reversing the same transfer) wrote nothing, and is rebuilt from a fresh read.
    /// </summary>
    public static class LedgerCorrections
    {
        /// <summary>
        /// market_transfer.memo's length
        /// </summary>
        public const int MaxMemoLength = 512;

        private const int Attempts = 5;

        /// <summary>
        /// Writes an admin_adjust transfer: the account +amount (which may be negative) and ADMIN -amount. Refused if the balance would fall below zero.
        /// </summary>
        /// <param name="newContext">a fresh shard context for each attempt</param>
        public static CorrectionResult Adjust(Func<ShardDbContext> newContext, uint accountId, long amount, string memo, uint adminAccountId, uint? adminCharacterId, DateTime now)
        {
            if (amount == 0)
                return new CorrectionResult(CorrectionOutcome.InvalidAmount);

            if (!TryMemo(ref memo))
                return new CorrectionResult(CorrectionOutcome.InvalidMemo);

            var (result, transfer) = SaveTransfer(newContext, context => (null, NewTransfer(TransferKind.AdminAdjust, adminAccountId, adminCharacterId, memo, now,
                Ledger.PlayerEntry(accountId, amount), Ledger.SystemEntry(SystemAccount.Admin, -amount))));

            return result.Outcome == CorrectionOutcome.Done ? result with { Balance = transfer.Entries.Single(e => e.AccountId == accountId).BalanceAfter ?? 0 } : result;
        }

        /// <summary>
        /// Writes a reversal of the transfer: every entry negated, undone in reverse order so that no balance dips below zero on the way.
        /// It moves MMD only: a reversed purchase leaves the item with the buyer, and a reversed note withdrawal leaves the notes in the world.
        /// A transfer can be reversed only once (market_transfer.reverses_Transfer_Id is unique). A reversal is a transfer too, so it can itself be reversed once.
        /// Refused if a balance would fall below zero, for example when the MMD a reversal takes back has already been spent.
        /// </summary>
        /// <param name="newContext">a fresh shard context for each attempt</param>
        public static CorrectionResult Reverse(Func<ShardDbContext> newContext, long transferId, string memo, uint adminAccountId, uint? adminCharacterId, DateTime now)
        {
            if (!TryMemo(ref memo))
                return new CorrectionResult(CorrectionOutcome.InvalidMemo);

            return SaveTransfer(newContext, context =>
            {
                var original = context.MarketTransfers.AsNoTracking().Include(t => t.Entries).FirstOrDefault(t => t.Id == transferId);

                if (original == null)
                    return (CorrectionOutcome.UnknownTransfer, null);

                if (context.MarketTransfers.Any(t => t.ReversesTransferId == transferId))
                    return (CorrectionOutcome.AlreadyReversed, null);

                // a transfer written past the ledger service can't be undone by a balanced one
                if (original.Entries.Count < 2 || original.Entries.Sum(e => e.Amount) != 0)
                    return (CorrectionOutcome.Unbalanced, null);

                var entries = original.Entries.OrderByDescending(e => e.Id)
                    .Select(e => e.AccountId != null ? Ledger.PlayerEntry(e.AccountId.Value, -e.Amount) : Ledger.SystemEntry(e.SystemAccount, -e.Amount))
                    .ToArray();

                var reversal = NewTransfer(TransferKind.Reversal, adminAccountId, adminCharacterId, memo, now, entries);
                reversal.ReversesTransferId = transferId;

                return (null, reversal);
            }).result;
        }

        private static bool TryMemo(ref string memo)
        {
            memo = memo?.Trim();

            return !string.IsNullOrEmpty(memo) && memo.Length <= MaxMemoLength;
        }

        /// <param name="build">reads through the context and returns a refusal, or the transfer to write</param>
        private static (CorrectionResult result, Transfer transfer) SaveTransfer(Func<ShardDbContext> newContext, Func<ShardDbContext, (CorrectionOutcome? refusal, Transfer transfer)> build)
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                using var context = newContext();

                var (refusal, transfer) = build(context);

                if (refusal != null)
                    return (new CorrectionResult(refusal.Value), null);

                if (!Ledger.TryAdd(context, transfer))
                    return (new CorrectionResult(CorrectionOutcome.InsufficientFunds), null);

                try
                {
                    context.SaveChanges();

                    return (new CorrectionResult(CorrectionOutcome.Done, transfer.Id), transfer);
                }
                catch (DbUpdateException ex) when (Ledger.IsLostRace(ex))
                {
                    // another writer changed a balance row, or reversed the same transfer, first: read again
                }
            }

            return (new CorrectionResult(CorrectionOutcome.Busy), null);
        }

        private static Transfer NewTransfer(string kind, uint adminAccountId, uint? adminCharacterId, string memo, DateTime now, params LedgerEntry[] entries)
        {
            var transfer = new Transfer { Kind = kind, ActorAccountId = adminAccountId, ActorCharacterId = adminCharacterId, Memo = memo, CreatedTime = now };

            foreach (var entry in entries)
                transfer.Entries.Add(entry);

            return transfer;
        }
    }
}
