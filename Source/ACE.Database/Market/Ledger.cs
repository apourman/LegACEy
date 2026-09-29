using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    /// <summary>
    /// The double-entry ledger. A transfer is added to the caller's context, next to the caller's other changes, and saved by the caller's one SaveChanges.
    /// There is no explicit transaction: the balance row's row version makes a save that read a stale balance fail with DbUpdateConcurrencyException,
    /// and a race to create an account's first balance row fails on its primary key. Either way the whole save writes nothing and the caller may re-read and retry.
    /// </summary>
    public static class Ledger
    {
        private static readonly HashSet<string> systemAccounts = new HashSet<string> { SystemAccount.Notes, SystemAccount.Fees, SystemAccount.Admin };

        /// <summary>
        /// The account's MMD balance, 0 if it has never had one
        /// </summary>
        public static long GetBalance(uint accountId)
        {
            using (var context = new ShardDbContext())
                return GetBalance(context, accountId);
        }

        public static long GetBalance(ShardDbContext context, uint accountId)
        {
            return context.MarketBalances.Where(r => r.AccountId == accountId).Select(r => r.Balance).FirstOrDefault();
        }

        /// <summary>
        /// A leg for a player account. Its sequence number and balance-after are filled in by TryAdd.
        /// </summary>
        public static LedgerEntry PlayerEntry(uint accountId, long amount, string memo = null)
        {
            return new LedgerEntry { AccountId = accountId, Amount = amount, Memo = memo };
        }

        /// <summary>
        /// A leg for a system account (NOTES, FEES, ADMIN), which has no balance row, sequence or balance-after
        /// </summary>
        public static LedgerEntry SystemEntry(string systemAccount, long amount, string memo = null)
        {
            return new LedgerEntry { SystemAccount = systemAccount, Amount = amount, Memo = memo };
        }

        /// <summary>
        /// Adds the transfer (with its entries in transfer.Entries) to the context, to be saved by the caller's SaveChanges together with the caller's other changes.
        /// Each player entry, in order, takes the account's next sequence number and records the balance after it; the balance row is changed and its row version bumped
        /// (a new balance row is inserted at row version 0). The balance rows are read through the context, so the save fails if another writer changed one since.
        /// Returns false, adding nothing, if a player's balance would fall below zero.
        /// Throws ArgumentException for a malformed transfer: fewer than two entries, entries that don't add up to zero, or an entry without exactly one known owner.
        /// </summary>
        public static bool TryAdd(ShardDbContext context, Transfer transfer)
        {
            Validate(transfer);

            var balances = new Dictionary<uint, AccountBalance>();

            foreach (var accountId in transfer.Entries.Where(e => e.AccountId != null).Select(e => e.AccountId.Value).Distinct())
                balances[accountId] = context.MarketBalances.Find(accountId);

            // check every account first, so a refusal leaves the context as it was
            foreach (var (accountId, row) in balances)
            {
                var balance = row?.Balance ?? 0;

                foreach (var entry in transfer.Entries.Where(e => e.AccountId == accountId))
                {
                    balance += entry.Amount;

                    if (balance < 0)
                        return false;
                }
            }

            foreach (var accountId in balances.Keys.ToList())
            {
                if (balances[accountId] == null)
                {
                    balances[accountId] = new AccountBalance { AccountId = accountId };
                    context.MarketBalances.Add(balances[accountId]);
                }
                else
                    balances[accountId].RowVersion++;
            }

            foreach (var entry in transfer.Entries)
            {
                if (entry.AccountId == null)
                {
                    entry.Sequence = null;
                    entry.BalanceAfter = null;
                    continue;
                }

                var row = balances[entry.AccountId.Value];

                row.Balance += entry.Amount;
                row.LastSequence++;

                entry.Sequence = row.LastSequence;
                entry.BalanceAfter = row.Balance;
            }

            if (transfer.CreatedTime == default)
                transfer.CreatedTime = DateTime.UtcNow;

            context.MarketTransfers.Add(transfer);

            return true;
        }

        private static void Validate(Transfer transfer)
        {
            if (transfer.Entries.Count < 2)
                throw new ArgumentException($"A {transfer.Kind} transfer needs two or more entries, not {transfer.Entries.Count}", nameof(transfer));

            foreach (var entry in transfer.Entries)
            {
                if ((entry.AccountId == null) == (entry.SystemAccount == null))
                    throw new ArgumentException("A ledger entry belongs to exactly one player account or system account", nameof(transfer));

                if (entry.SystemAccount != null && !systemAccounts.Contains(entry.SystemAccount))
                    throw new ArgumentException($"Unknown system account {entry.SystemAccount}", nameof(transfer));
            }

            var sum = transfer.Entries.Sum(e => e.Amount);

            if (sum != 0)
                throw new ArgumentException($"A {transfer.Kind} transfer's entries add up to {sum}, not zero", nameof(transfer));
        }
    }
}
