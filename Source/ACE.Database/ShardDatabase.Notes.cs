using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.EntityFrameworkCore;

using MySqlConnector;

using ACE.Common.Extensions;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum.Properties;

namespace ACE.Database
{
    /// <summary>
    /// Trade notes (MMD, 1 each) in and out of the account balance. Each job changes the note rows and writes its ledger transfer in one SaveChanges (no explicit transaction).
    /// Run them on the serialized shard save queue (SerializedShardDatabase), which orders them after any earlier save of the notes.
    /// </summary>
    public partial class ShardDatabase
    {
        public const uint TradeNoteWcid = 20630;

        private const int LedgerJobAttempts = 3;

        /// <summary>
        /// Banks notes the world thread has taken out of a character's packs: evicts them from the cache, deletes their rows, and writes a note_deposit transfer
        /// (player +n, NOTES -n) with a deposit item event per destroyed note GUID (its quantity and the transfer id), all in one save.
        /// The stack sizes are the world's, which is the truth: a note row that was never saved has nothing to delete.
        /// Refuses, saving nothing, if a GUID's row is something other than a trade note.
        /// </summary>
        public MarketJobResult DepositNotes(uint accountId, uint characterId, IReadOnlyList<NoteStack> notes, out long balanceAfter)
        {
            balanceAfter = 0;

            if (notes.Count == 0 || notes.Any(n => n.StackSize < 1))
            {
                log.Warn($"[DATABASE][VAULT] DepositNotes for account {accountId} refused: {notes.Count} stacks, stack sizes {string.Join(",", notes.Select(n => n.StackSize))}");
                return MarketJobResult.Refused;
            }

            var guids = notes.Select(n => n.Guid).ToList();
            var amount = notes.Sum(n => (long)n.StackSize);

            // GUIDs of deleted notes can come back after a restart, so only an event written since this job started is this job's
            var started = ListingStore.Truncate(DateTime.UtcNow);
            var firstGuid = guids[0];

            return SaveLedgerJob(nameof(DepositNotes), accountId, context =>
            {
                foreach (var guid in guids)
                    EvictBiota(guid);

                var rows = context.Biota.AsNoTracking().Where(r => guids.Contains(r.Id)).Select(r => new { r.Id, r.WeenieClassId }).ToList();

                var notANote = rows.FirstOrDefault(r => r.WeenieClassId != TradeNoteWcid);

                if (notANote != null)
                {
                    log.Warn($"[DATABASE][VAULT] DepositNotes for account {accountId} refused: 0x{notANote.Id:X8} is weenie {notANote.WeenieClassId}, not a trade note");
                    return null;
                }

                // the item rows' children go with them (ON DELETE CASCADE), as in RemoveBiota
                foreach (var row in rows)
                    context.Biota.Remove(new Biota { Id = row.Id });

                var transfer = NewTransfer(TransferKind.NoteDeposit, accountId, characterId, Ledger.PlayerEntry(accountId, amount), Ledger.SystemEntry(SystemAccount.Notes, -amount));

                foreach (var note in notes)
                {
                    context.MarketItemEvents.Add(new ItemEvent
                    {
                        ItemGuid = note.Guid,
                        AccountId = accountId,
                        CharacterId = characterId,
                        Kind = ItemEventKind.Deposit,
                        Transfer = transfer,
                        Quantity = note.StackSize,
                        EventTime = transfer.CreatedTime,
                    });
                }

                return transfer;
            },
            context => context.MarketItemEvents.Any(e => e.ItemGuid == firstGuid && e.Kind == ItemEventKind.Deposit && e.TransferId != null && e.EventTime >= started),
            out balanceAfter);
        }

        /// <summary>
        /// Pays out notes the world thread has created (not in any pack yet, pointed at the character): writes a note_withdraw transfer (player -n, NOTES +n)
        /// and inserts the note rows, in one save. The notes' stack sizes must add up to amount.
        /// Returns InsufficientFunds, saving nothing, if the balance is less than amount (balanceAfter is then the current balance),
        /// Paused, saving nothing, if the market was paused by the time the job started (a pause landing during the job's own save isn't seen),
        /// and Banned, saving nothing, if the account is banned then (a ban freezes the balance, and it may have landed after the request was made).
        /// A game bridge ticket passed as ticket is marked done in the same save, and the transfer names it.
        /// </summary>
        public MarketJobResult WithdrawNotes(uint accountId, uint characterId, IReadOnlyList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> notes, long amount, out long balanceAfter, TicketCompletion ticket = null)
        {
            balanceAfter = 0;

            long stacked = 0;

            foreach (var (biota, rwLock) in notes)
            {
                rwLock.EnterReadLock();
                try
                {
                    if (biota.WeenieClassId != TradeNoteWcid || biota.PropertiesIID == null || !biota.PropertiesIID.ContainsKey(PropertyInstanceId.Container))
                    {
                        log.Warn($"[DATABASE][VAULT] WithdrawNotes for account {accountId} refused: 0x{biota.Id:X8} is not a trade note pointed at a container");
                        return MarketJobResult.Refused;
                    }

                    stacked += biota.PropertiesInt != null && biota.PropertiesInt.TryGetValue(PropertyInt.StackSize, out var stackSize) ? stackSize : 1;
                }
                finally
                {
                    rwLock.ExitReadLock();
                }
            }

            if (amount < 1 || stacked != amount)
            {
                log.Warn($"[DATABASE][VAULT] WithdrawNotes for account {accountId} refused: {amount} MMD asked, {stacked} in {notes.Count} stacks");
                return MarketJobResult.Refused;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    if (MarketPause.IsPaused(context))
                    {
                        log.Warn($"[DATABASE][VAULT] WithdrawNotes for account {accountId} refused: the market is paused");
                        balanceAfter = Ledger.GetBalance(context, accountId);
                        return MarketJobResult.Paused;
                    }

                    if (IsAccountBanned(accountId))
                    {
                        log.Warn($"[DATABASE][VAULT] WithdrawNotes for account {accountId} refused: the account is banned");
                        balanceAfter = Ledger.GetBalance(context, accountId);
                        return MarketJobResult.Banned;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][VAULT] WithdrawNotes for account {accountId} failed before saving, nothing was saved: {ex.GetFullMessage()}");
                return MarketJobResult.Failed;
            }

            // the notes' GUIDs are new, and nothing but this job saves them before it succeeds, so a note row means the job committed
            var firstGuid = notes[0].biota.Id;

            return SaveLedgerJob(nameof(WithdrawNotes), accountId, context =>
            {
                var transfer = NewTransfer(TransferKind.NoteWithdraw, accountId, characterId, Ledger.PlayerEntry(accountId, -amount), Ledger.SystemEntry(SystemAccount.Notes, amount));

                if (ticket != null)
                {
                    transfer.TicketId = ticket.TicketId;
                    TicketStore.Complete(context, ticket, transfer.CreatedTime);
                }

                foreach (var (biota, rwLock) in notes)
                {
                    Biota row;

                    rwLock.EnterReadLock();
                    try
                    {
                        row = ACE.Database.Adapter.BiotaConverter.ConvertFromEntityBiota(biota);
                    }
                    finally
                    {
                        rwLock.ExitReadLock();
                    }

                    SetBiotaPopulatedCollections(row);

                    context.Biota.Add(row);
                }

                return transfer;
            },
            context => context.Biota.Any(b => b.Id == firstGuid),
            out balanceAfter);
        }

        /// <summary>
        /// Builds a job's changes in a fresh context (build adds everything but the transfer it returns), adds the transfer to the ledger, and saves once.
        /// build returns null to refuse, and then nothing is saved; so does a transfer the balance can't cover (InsufficientFunds). A save that lost a race for the balance row (a stale row version, or two first writes)
        /// wrote nothing, so it is rebuilt from a fresh read and tried again, up to LedgerJobAttempts times.
        /// A save can also fail after its commit (the acknowledgement was lost, and the retry strategy's second try then fails on the rows the first wrote),
        /// which looks like a lost race. So before any retry, and after any other failure while saving, committed asks the database whether the job's changes are there:
        /// rebuilding a job that already committed would apply it twice. No exception escapes, so the caller's callback always runs.
        /// </summary>
        private MarketJobResult SaveLedgerJob(string job, uint accountId, Func<ShardDbContext, Transfer> build, Func<ShardDbContext, bool> committed, out long balanceAfter)
        {
            balanceAfter = 0;

            for (var attempt = 1; ; attempt++)
            {
                var saving = false;

                try
                {
                    if (attempt > 1)
                    {
                        using (var check = new ShardDbContext())
                        {
                            if (committed(check))
                            {
                                log.Warn($"[DATABASE][VAULT] {job} for account {accountId} committed although its save reported a lost race; treating it as saved");
                                balanceAfter = Ledger.GetBalance(check, accountId);
                                return MarketJobResult.Saved;
                            }
                        }
                    }

                    using (var context = new ShardDbContext())
                    {
                        var transfer = build(context);

                        if (transfer == null)
                        {
                            balanceAfter = Ledger.GetBalance(context, accountId);
                            return MarketJobResult.Refused;
                        }

                        if (!Ledger.TryAdd(context, transfer))
                        {
                            log.Warn($"[DATABASE][VAULT] {job} for account {accountId} refused: the balance is too low");
                            balanceAfter = Ledger.GetBalance(context, accountId);
                            return MarketJobResult.InsufficientFunds;
                        }

                        saving = true;
                        context.SaveChanges();

                        balanceAfter = transfer.Entries.Last(e => e.AccountId == accountId).BalanceAfter ?? 0;
                        return MarketJobResult.Saved;
                    }
                }
                catch (DbUpdateException ex) when (saving && attempt < LedgerJobAttempts && Ledger.IsLostRace(ex))
                {
                    log.Warn($"[DATABASE][VAULT] {job} for account {accountId} lost a race for the balance row, trying again: {ex.GetFullMessage()}");
                }
                catch (Exception ex) when (!saving)
                {
                    log.Error($"[DATABASE][VAULT] {job} for account {accountId} failed before saving, nothing was saved: {ex.GetFullMessage()}");
                    return MarketJobResult.Failed;
                }
                catch (Exception ex)
                {
                    log.Error($"[DATABASE][VAULT] {job} for account {accountId} failed while saving: {ex.GetFullMessage()}");

                    var result = Reconcile(job, $"for account {accountId}", committed);

                    if (result == MarketJobResult.Saved)
                    {
                        try
                        {
                            balanceAfter = Ledger.GetBalance(accountId);
                        }
                        catch (Exception)
                        {
                            // only the message's balance is lost
                        }
                    }

                    return result;
                }
            }
        }

        private static Transfer NewTransfer(string kind, uint accountId, uint characterId, params LedgerEntry[] entries)
        {
            var transfer = new Transfer { Kind = kind, ActorAccountId = accountId, ActorCharacterId = characterId, CreatedTime = DateTime.UtcNow };

            foreach (var entry in entries)
                transfer.Entries.Add(entry);

            return transfer;
        }
    }
}
