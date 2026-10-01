using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Market
{
    /// <summary>
    /// Trade notes (WCID 20630, 1 MMD each) in and out of the account balance. Instant, never through the channel: notes are bonded and never drop on death anyway.
    /// Like items, the notes are changed in memory on the world thread and then saved once together with their ledger transfer on the save queue.
    /// </summary>
    public static partial class Vault
    {
        /// <summary>
        /// The most notes in one stack: the spec's 1,000, which is also the trade note weenie's MaxStackSize
        /// </summary>
        public const int NoteStackSize = 1000;

        /// <summary>
        /// The account's MMD balance
        /// </summary>
        public static long Balance(Player player)
        {
            return Available ? Ledger.GetBalance(player.Character.AccountId) : 0;
        }

        /// <summary>
        /// Banks every whole stack of trade notes in the player's packs (not ones in the trade window). The stacks leave the packs in memory,
        /// then one save deletes their rows and credits the balance. On success the objects are forgotten; on failure they go back to the pack.
        /// </summary>
        public static void DepositNotes(Player player, Action<VaultResult> completed = null)
        {
            if (!Available)
            {
                FinishNotes(player, VaultOutcome.NotAvailable, 0, 0, completed);
                return;
            }

            if (inFlight.Contains(player.Guid.Full))
            {
                FinishNotes(player, VaultOutcome.Busy, 0, 0, completed);
                return;
            }

            var notes = new List<WorldObject>();

            foreach (var note in player.GetTradeNotes().Where(n => n.WeenieClassId == ShardDatabase.TradeNoteWcid && !player.ItemsInTradeWindow.Contains(n.Guid)))
            {
                if (player.TryRemoveFromInventoryForVault(note.Guid, out var taken))
                    notes.Add(taken);
            }

            if (notes.Count == 0)
            {
                FinishNotes(player, VaultOutcome.NoNotes, 0, 0, completed);
                return;
            }

            if (player.CurrentAppraisalTarget != null && notes.Any(n => n.Guid.Full == player.CurrentAppraisalTarget))
                player.CurrentAppraisalTarget = null;

            var amount = notes.Sum(n => (long)(n.StackSize ?? 1));
            var stacks = notes.Select(n => new NoteStack(n.Guid.Full, n.StackSize ?? 1)).ToList();

            inFlight.Add(player.Guid.Full);

            DatabaseManager.Shard.DepositNotes(player.Character.AccountId, player.Guid.Full, stacks, (result, balance) =>
            {
                // this runs on the save thread
                WorldManager.EnqueueAction(new ActionEventDelegate(() => OnNotesDeposited(player, notes, amount, result, balance, completed)));
            });
        }

        /// <summary>
        /// Pays out amount MMD as trade notes, in stacks of up to 1,000. The notes are created in memory and pointed at the player, but only added to the pack
        /// once the save has inserted their rows and debited the balance, so that no save of the pack can write them before the ledger does.
        /// A game bridge ticket given as ticketId is marked done in the same save, and the transfer names it.
        /// </summary>
        public static void WithdrawNotes(Player player, long amount, Action<VaultResult> completed = null, long? ticketId = null)
        {
            if (!Available)
            {
                FinishNotes(player, VaultOutcome.NotAvailable, amount, 0, completed);
                return;
            }

            if (inFlight.Contains(player.Guid.Full))
            {
                FinishNotes(player, VaultOutcome.Busy, amount, 0, completed);
                return;
            }

            if (amount < 1)
            {
                FinishNotes(player, VaultOutcome.InvalidAmount, amount, 0, completed);
                return;
            }

            var accountId = player.Character.AccountId;
            long balance;
            bool paused;

            using (var context = new ShardDbContext())
            {
                balance = Ledger.GetBalance(context, accountId);
                paused = MarketPause.IsPaused(context);
            }

            // a failed ledger audit stops money leaving the market until an admin resumes it; deposits go on
            if (paused)
            {
                FinishNotes(player, VaultOutcome.Paused, amount, balance, completed);
                return;
            }

            if (amount > balance)
            {
                FinishNotes(player, VaultOutcome.InsufficientFunds, amount, balance, completed);
                return;
            }

            // count the slots before creating anything, so a huge amount doesn't create thousands of objects just to refuse them
            var stackCount = (amount + NoteStackSize - 1) / NoteStackSize;

            if (stackCount > player.GetFreeInventorySlots())
            {
                FinishNotes(player, VaultOutcome.NoPackSpace, amount, balance, completed);
                return;
            }

            var notes = new List<WorldObject>();

            for (var left = amount; left > 0; left -= NoteStackSize)
            {
                var note = WorldObjectFactory.CreateNewWorldObject(ShardDatabase.TradeNoteWcid);
                note.SetStackSize((int)Math.Min(left, NoteStackSize));
                notes.Add(note);
            }

            if (!player.CanAddToInventory(notes))
            {
                FinishNotes(player, VaultOutcome.NoPackSpace, amount, balance, completed);
                return;
            }

            foreach (var note in notes)
            {
                note.OwnerId = player.Guid.Full;
                note.ContainerId = player.Guid.Full;
                note.PlacementPosition = 0;
            }

            inFlight.Add(player.Guid.Full);

            var ticket = ticketId == null ? null : new TicketCompletion(ticketId.Value, VaultMessages.WithdrawnByTicket(VaultMessages.TradeNotes(amount), player.Name));

            DatabaseManager.Shard.WithdrawNotes(accountId, player.Guid.Full, notes.Select(n => (n.Biota, n.BiotaDatabaseLock)).ToList(), amount, (result, balanceAfter) =>
            {
                // this runs on the save thread
                WorldManager.EnqueueAction(new ActionEventDelegate(() => OnNotesWithdrawn(player, notes, amount, result, balanceAfter, completed)));
            }, ticket);
        }

        private static void OnNotesDeposited(Player player, List<WorldObject> notes, long amount, MarketJobResult result, long balance, Action<VaultResult> completed)
        {
            inFlight.Remove(player.Guid.Full);

            if (result == MarketJobResult.Saved)
            {
                // the objects are forgotten, never saved or destroyed: their rows are gone, and a save would write them back
                FinishNotes(player, VaultOutcome.NotesDeposited, amount, balance, completed);
                return;
            }

            log.Warn($"[VAULT] Note deposit of {amount:N0} MMD for {player.Name} failed ({result}); nothing was saved");

            // the database still has the notes in the pack. A player who has gone gets them back when they log in, so the objects are just discarded.
            if (!player.IsLoggingOut)
            {
                foreach (var note in notes)
                {
                    if (!player.TryCreateInInventoryWithNetworking(note))
                        log.Warn($"[VAULT] Note deposit for {player.Name} failed and the pack has no room for 0x{note.Guid.Full:X8}; the database has it in the pack for the next login");
                }
            }

            FinishNotes(player, VaultOutcome.SaveFailed, amount, balance, completed);
        }

        private static void OnNotesWithdrawn(Player player, List<WorldObject> notes, long amount, MarketJobResult result, long balance, Action<VaultResult> completed)
        {
            inFlight.Remove(player.Guid.Full);

            if (result != MarketJobResult.Saved)
            {
                // no row was written and the objects were never added anywhere: forget them
                var outcome = result switch
                {
                    MarketJobResult.InsufficientFunds => VaultOutcome.InsufficientFunds,
                    MarketJobResult.Paused => VaultOutcome.Paused,
                    _ => VaultOutcome.SaveFailed,
                };

                FinishNotes(player, outcome, amount, balance, completed);
                return;
            }

            // the database now has the notes in this character's pack, so a player who has gone gets them at the next login
            var allInPack = true;

            if (!player.IsLoggingOut)
            {
                foreach (var note in notes)
                {
                    if (!player.TryCreateInInventoryWithNetworking(note))
                    {
                        allInPack = false;
                        log.Warn($"[VAULT] Withdrawn note stack 0x{note.Guid.Full:X8} for {player.Name} could not be added to the pack; the database has it in the pack for the next login");
                    }
                }
            }

            FinishNotes(player, allInPack ? VaultOutcome.NotesWithdrawn : VaultOutcome.NotesWithdrawnAtLogin, amount, balance, completed);
        }

        /// <summary>
        /// Tells the player the trade note outcome and reports it to the caller
        /// </summary>
        private static void FinishNotes(Player player, VaultOutcome outcome, long amount, long balance, Action<VaultResult> completed)
        {
            var result = new VaultResult(outcome, VaultMessages.ForNotes(outcome, amount, balance), amount, balance);

            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(result.Message, ChatMessageType.Broadcast));

            completed?.Invoke(result);
        }
    }
}
