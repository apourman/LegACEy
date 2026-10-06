using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.EntityFrameworkCore;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Server.Market;

namespace ACE.MarketDev
{
    /// <summary>
    /// Ticket presentation fixtures for the local website, run with the game server stopped (fixture mode). Every durable write is a market_ticket row only:
    /// Vault rows, balances, the ledger and items are only read. Tickets only move forward (WAITING, CLAIMED with any progress, then DONE or FAILED),
    /// through the same TicketStore updates the game uses, with the game's own result codes and messages.
    /// </summary>
    internal static class TicketFixture
    {
        private const string UnknownKind = "marketdev_unknown_probe";

        /// <summary>
        /// A running game claims and fails any ticket within about a second
        /// </summary>
        private static readonly TimeSpan ProbeWait = TimeSpan.FromSeconds(5);

        /// <summary>
        /// ACE's confirmation popup timeout, for an awaiting_confirmation countdown
        /// </summary>
        private const int ConfirmationSeconds = 30;

        private static readonly string[] BridgeCodes =
        {
            GameBridge.Offline, GameBridge.InvalidCharacter, GameBridge.UnsupportedKind, GameBridge.InvalidTicket, TicketStore.ServerRestart, TicketStore.Abandoned,
        };

        /// <summary>
        /// The failures the examples show, each on the kind of ticket that meets it
        /// </summary>
        private static readonly (string Kind, string Code)[] FailureExamples =
            BridgeCodes.Select(code => (code == GameBridge.UnsupportedKind ? TicketKind.VaultDeposit : TicketKind.VaultWithdraw, code))
            .Concat(new[]
            {
                VaultOutcome.NotAvailable, VaultOutcome.Busy, VaultOutcome.NotInVault, VaultOutcome.Listed, VaultOutcome.Withdrawing, VaultOutcome.NoPackSpace,
                VaultOutcome.UniqueLimit, VaultOutcome.RecentPlayerFight, VaultOutcome.Trading, VaultOutcome.Channelling, VaultOutcome.Interrupted,
                VaultOutcome.SaveFailed, VaultOutcome.Unconfirmed, VaultOutcome.Banned,
            }.Select(outcome => (TicketKind.VaultWithdraw, GameBridge.ResultCode(outcome))))
            .Concat(new[] { VaultOutcome.InvalidAmount, VaultOutcome.InsufficientFunds, VaultOutcome.NoPackSpace, VaultOutcome.Paused }
                .Select(outcome => (TicketKind.MmdWithdraw, GameBridge.ResultCode(outcome))))
            .ToArray();

        private const long ExampleAmount = 25;

        /// <summary>
        /// The fixture's view of one account: whose tickets it writes, and what their messages name
        /// </summary>
        private sealed record Owner(uint AccountId, uint CharacterId, string CharacterName, uint? ItemGuid, string ItemName, long Balance);

        /// <summary>
        /// Creates one ticket in every state and with every failure reason for the character's account. False if the game server is running.
        /// </summary>
        public static bool CreateExamples(string characterName)
        {
            var owner = FindOwner(characterName);

            if (GameIsRunning(owner.AccountId, owner.CharacterId))
                return false;

            if (owner.ItemGuid == null)
                Console.WriteLine($"{characterName}'s account has no Vault item, so item withdrawal examples name no item. Run scripts/market/seed.sh for one.");

            Console.WriteLine("Ticket fixture created presentation-only tickets (all writes were limited to market_ticket):");

            var waiting = Create(owner, TicketKind.MmdWithdraw, "waiting");
            Console.WriteLine($"  WAITING: #{waiting.Id}");

            var working = Create(owner, TicketKind.MmdWithdraw, "working");
            Advance(owner, working, "claimed", null, null);
            Console.WriteLine($"  CLAIMED: #{working.Id}");

            var confirming = Create(owner, TicketKind.VaultWithdraw, "confirming");
            Advance(owner, confirming, TicketProgress.AwaitingConfirmation, null, null);
            Console.WriteLine($"  CLAIMED / {TicketProgress.AwaitingConfirmation}: #{confirming.Id}");

            var channelling = Create(owner, TicketKind.VaultWithdraw, "channelling");
            Advance(owner, channelling, TicketProgress.Channelling, null, null);
            Console.WriteLine($"  CLAIMED / {TicketProgress.Channelling}: #{channelling.Id}");

            foreach (var kind in new[] { TicketKind.VaultWithdraw, TicketKind.MmdWithdraw })
            {
                var done = Create(owner, kind, "done");
                Advance(owner, done, "done", null, null);
                Console.WriteLine($"  DONE / {kind}: #{done.Id}");
            }

            foreach (var (kind, code) in FailureExamples)
            {
                var failed = Create(owner, kind, code);
                Advance(owner, failed, "failed", code, null);
                Console.WriteLine($"  FAILED / {kind} / {code}: #{failed.Id}");
            }

            Console.WriteLine($"Move a ticket forward with: fixture --ticket <id> --to <claimed|{TicketProgress.AwaitingConfirmation}|{TicketProgress.Channelling}|done|failed> [--code <reason>] [--seconds <countdown>]");
            return true;
        }

        /// <summary>
        /// Moves one existing ticket forward to the stage named by to: claimed, a progress stage (with a countdown of seconds), done, or failed with code.
        /// False if the game server is running, the move would go backwards, or the code isn't one the game answers.
        /// </summary>
        public static bool Move(long ticketId, string to, string code, int? seconds)
        {
            Ticket ticket;
            using (var shard = new ShardDbContext())
                ticket = shard.MarketTickets.AsNoTracking().SingleOrDefault(t => t.Id == ticketId);

            if (ticket == null || ticket.CharacterId == null)
            {
                Console.Error.WriteLine($"Ticket fixture refused: there is no ticket #{ticketId} with a character.");
                return false;
            }

            if (to == "failed" && !IsKnownCode(code))
            {
                Console.Error.WriteLine($"Ticket fixture refused: --code must be one of {string.Join(", ", KnownCodes())}.");
                return false;
            }

            var owner = ReadOwner(ticket.AccountId, ticket.CharacterId.Value, TicketPayload.FromJson(ticket.Payload)?.ItemGuid);

            if (GameIsRunning(owner.AccountId, owner.CharacterId))
                return false;

            try
            {
                Advance(owner, ticket, to, code, seconds);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Ticket fixture refused: {ex.Message}");
                return false;
            }

            Console.WriteLine($"Ticket #{ticketId} moved to {to}{(code == null ? "" : " / " + code)}. Only its market_ticket row changed.");
            return true;
        }

        /// <summary>
        /// One forward move. A WAITING ticket is claimed first, as the game would; anything else that isn't CLAIMED can't move, and throws.
        /// </summary>
        private static void Advance(Owner owner, Ticket ticket, string to, string code, int? seconds)
        {
            var now = DateTime.UtcNow;

            using var shard = new ShardDbContext();
            var status = shard.MarketTickets.AsNoTracking().Where(t => t.Id == ticket.Id).Select(t => t.Status).Single();

            if (status == TicketStatus.Waiting && !TicketStore.ClaimOne(shard, ticket.Id, now))
                throw new InvalidOperationException($"ticket #{ticket.Id} could not be claimed.");

            if (status != TicketStatus.Waiting && status != TicketStatus.Claimed)
                throw new InvalidOperationException($"ticket #{ticket.Id} is already {status}; tickets only move forward.");

            switch (to)
            {
                case "claimed":
                    if (status != TicketStatus.Waiting)
                        throw new InvalidOperationException($"ticket #{ticket.Id} is already CLAIMED.");
                    break;

                case TicketProgress.AwaitingConfirmation:
                case TicketProgress.Channelling:
                    var countdown = seconds ?? (to == TicketProgress.Channelling ? (int)MarketSettings.Get(shard, MarketSettings.ChannelSeconds) : ConfirmationSeconds);
                    if (!TicketStore.SetProgress(shard, ticket.Id, to, now, now.AddSeconds(countdown)))
                        throw new InvalidOperationException($"ticket #{ticket.Id} could not move to {to}.");
                    break;

                case "done":
                    TicketStore.Complete(shard, new TicketCompletion(ticket.Id, DoneMessage(owner, ticket)), now);
                    try
                    {
                        shard.SaveChanges();
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        throw new InvalidOperationException($"ticket #{ticket.Id} could not move to DONE.");
                    }
                    break;

                case "failed":
                    if (!TicketStore.Fail(shard, ticket.Id, code, FailureMessage(owner, ticket, code), now))
                        throw new InvalidOperationException($"ticket #{ticket.Id} could not move to FAILED.");
                    break;

                default:
                    throw new InvalidOperationException($"'{to}' is not a stage; use claimed, {TicketProgress.AwaitingConfirmation}, {TicketProgress.Channelling}, done or failed.");
            }
        }

        /// <summary>
        /// Proves no bridge is polling: writes a probe ticket of an unknown kind and watches it. A running game claims it within about a second.
        /// The probe is deleted either way.
        /// </summary>
        private static bool GameIsRunning(uint accountId, uint characterId)
        {
            long probeId;
            using (var shard = new ShardDbContext())
                probeId = TicketStore.Create(shard, accountId, characterId, UnknownKind, new TicketPayload(), "fixture-probe-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow).Ticket.Id;

            try
            {
                var deadline = DateTime.UtcNow + ProbeWait;

                while (true)
                {
                    using (var shard = new ShardDbContext())
                    {
                        if (shard.MarketTickets.AsNoTracking().Where(t => t.Id == probeId).Select(t => t.Status).Single() != TicketStatus.Waiting)
                        {
                            Console.Error.WriteLine("Ticket fixture refused: the game server claimed its probe ticket. Stop the game server, then retry.");
                            return true;
                        }
                    }

                    if (DateTime.UtcNow >= deadline)
                        return false;

                    Thread.Sleep(200);
                }
            }
            finally
            {
                using var shard = new ShardDbContext();
                shard.MarketTickets.Where(t => t.Id == probeId).ExecuteDelete();
            }
        }

        private static Owner FindOwner(string characterName)
        {
            using var shard = new ShardDbContext();

            var character = shard.Character.AsNoTracking()
                .Where(c => c.Name == characterName && !c.IsDeleted)
                .Select(c => new { c.Id, c.AccountId })
                .SingleOrDefault() ?? throw new InvalidOperationException($"No live character named '{characterName}' exists in the configured shard.");

            return ReadOwner(character.AccountId, character.Id, null);
        }

        /// <summary>
        /// Reads (never writes) what the examples' messages name: the character, a Vault item of the account (itemGuid's, or any), and the balance
        /// </summary>
        private static Owner ReadOwner(uint accountId, uint characterId, uint? itemGuid)
        {
            using var shard = new ShardDbContext();

            var characterName = shard.Character.AsNoTracking().Where(c => c.Id == characterId).Select(c => c.Name).Single();
            var item = shard.MarketVaultItems.AsNoTracking()
                .Where(v => v.AccountId == accountId && (itemGuid == null || v.ItemGuid == itemGuid))
                .OrderBy(v => v.ItemGuid)
                .Select(v => new { v.ItemGuid, v.Name })
                .FirstOrDefault();

            return new Owner(accountId, characterId, characterName, item?.ItemGuid ?? itemGuid, item?.Name, Ledger.GetBalance(shard, accountId));
        }

        private static Ticket Create(Owner owner, string kind, string label)
        {
            var payload = kind == TicketKind.MmdWithdraw ? new TicketPayload(Amount: ExampleAmount)
                : kind == TicketKind.VaultWithdraw ? new TicketPayload(ItemGuid: owner.ItemGuid)
                : new TicketPayload();

            using var shard = new ShardDbContext();
            return TicketStore.Create(shard, owner.AccountId, owner.CharacterId, kind, payload, $"fixture-{label}-{Guid.NewGuid():N}", DateTime.UtcNow).Ticket;
        }

        private static IEnumerable<string> KnownCodes() =>
            BridgeCodes.Concat(Enum.GetValues<VaultOutcome>().Select(GameBridge.ResultCode).Where(code => code != "failed")).Distinct();

        private static bool IsKnownCode(string code) => code != null && KnownCodes().Contains(code);

        /// <summary>
        /// What the game tells the player for this code: the bridge's own message, or the Vault's for its outcome
        /// </summary>
        private static string FailureMessage(Owner owner, Ticket ticket, string code)
        {
            if (BridgeCodes.Contains(code))
                return GameBridge.Message(code);

            var outcome = Enum.GetValues<VaultOutcome>().First(o => GameBridge.ResultCode(o) == code);

            return ticket.Kind == TicketKind.MmdWithdraw
                ? VaultMessages.ForNotes(outcome, Amount(ticket), owner.Balance)
                : VaultMessages.For(outcome, owner.ItemName);
        }

        private static string DoneMessage(Owner owner, Ticket ticket) =>
            VaultMessages.WithdrawnByTicket(ticket.Kind == TicketKind.MmdWithdraw ? VaultMessages.TradeNotes(Amount(ticket)) : owner.ItemName ?? "That item", owner.CharacterName);

        private static long Amount(Ticket ticket) => TicketPayload.FromJson(ticket.Payload)?.Amount ?? ExampleAmount;
    }
}
