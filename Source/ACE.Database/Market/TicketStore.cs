using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    public enum TicketCreateOutcome
    {
        Created,

        // the account already used the key for a ticket of this kind: that ticket is the answer, and nothing new was written
        Existing,

        // the account already used the key for a ticket of another kind
        KeyReused,
    }

    public sealed record TicketCreateResult(TicketCreateOutcome Outcome, Ticket Ticket);

    /// <summary>
    /// What a ticket asks for, stored as its JSON payload: the item for an item withdrawal, the MMD for a note withdrawal
    /// </summary>
    public sealed record TicketPayload(uint? ItemGuid = null, long? Amount = null)
    {
        private static readonly JsonSerializerOptions json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        public string ToJson() => JsonSerializer.Serialize(this, json);

        /// <summary>
        /// Null when the text isn't a payload
        /// </summary>
        public static TicketPayload FromJson(string text)
        {
            try
            {
                return text == null ? null : JsonSerializer.Deserialize<TicketPayload>(text, json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A claimed ticket to mark done in the same save as its work, so a crash can never leave the work saved and the ticket unfinished
    /// </summary>
    /// <param name="Message">what the player is told</param>
    public sealed record TicketCompletion(long TicketId, string Message);

    /// <summary>
    /// The game bridge's queue (market_ticket). The Market API creates tickets; the game server claims them, does the work, and writes the result.
    /// Each change is one statement or one SaveChanges, never an explicit transaction.
    /// </summary>
    public static class TicketStore
    {
        /// <summary>
        /// The result code of a ticket whose work was done
        /// </summary>
        public const string Ok = "ok";

        /// <summary>
        /// The result code of a ticket the game server had claimed when it stopped
        /// </summary>
        public const string ServerRestart = "server_restart";

        /// <summary>
        /// Finished tickets are deleted after this many days
        /// </summary>
        public const int KeepDays = 30;

        /// <summary>
        /// Creates a WAITING ticket, unless the account already used the key: a ticket of the same kind is returned as it is, and one of another kind is refused.
        /// Two requests racing with one key both end up with the one ticket that was saved.
        /// </summary>
        public static TicketCreateResult Create(ShardDbContext context, uint accountId, uint characterId, string kind, TicketPayload payload, string idempotencyKey, DateTime now)
        {
            var existing = Existing(context, accountId, idempotencyKey, kind);

            if (existing != null)
                return existing;

            var ticket = new Ticket
            {
                Kind = kind,
                AccountId = accountId,
                CharacterId = characterId,
                Payload = payload.ToJson(),
                Status = TicketStatus.Waiting,
                IdempotencyKey = idempotencyKey,
                CreatedTime = ListingStore.Truncate(now),
            };

            context.MarketTickets.Add(ticket);

            try
            {
                context.SaveChanges();

                return new TicketCreateResult(TicketCreateOutcome.Created, ticket);
            }
            catch (DbUpdateException ex) when (Ledger.IsLostRace(ex))
            {
                // a parallel request with the same key saved first: its ticket is the answer
                context.ChangeTracker.Clear();

                return Existing(context, accountId, idempotencyKey, kind) ?? throw new InvalidOperationException($"ticket key {idempotencyKey} of account {accountId} lost a race but no ticket has it", ex);
            }
        }

        /// <summary>
        /// The account's ticket, or null if there's none with the id (another account's tickets are never found)
        /// </summary>
        public static Ticket Find(ShardDbContext context, uint accountId, long ticketId)
        {
            return context.MarketTickets.AsNoTracking().FirstOrDefault(t => t.Id == ticketId && t.AccountId == accountId);
        }

        /// <summary>
        /// Claims up to limit WAITING tickets, oldest first, and returns the ones this caller claimed.
        /// Each is claimed with its own conditional update from WAITING: one row changed means this caller owns it, so two pollers never both claim one.
        /// </summary>
        public static List<Ticket> Claim(ShardDbContext context, int limit, DateTime now)
        {
            now = ListingStore.Truncate(now);

            var waiting = context.MarketTickets.AsNoTracking().Where(t => t.Status == TicketStatus.Waiting).OrderBy(t => t.Id).Select(t => t.Id).Take(limit).ToList();

            var claimed = new List<long>();

            foreach (var id in waiting)
            {
                var changed = context.MarketTickets
                    .Where(t => t.Id == id && t.Status == TicketStatus.Waiting)
                    .ExecuteUpdate(s => s.SetProperty(t => t.Status, TicketStatus.Claimed).SetProperty(t => t.ClaimedTime, now));

                // 0 rows: another poller claimed it first
                if (changed == 1)
                    claimed.Add(id);
            }

            if (claimed.Count == 0)
                return new List<Ticket>();

            return context.MarketTickets.AsNoTracking().Where(t => claimed.Contains(t.Id)).OrderBy(t => t.Id).ToList();
        }

        /// <summary>
        /// Marks a claimed ticket FAILED with a result code and message. False if it isn't CLAIMED (already finished, or never claimed).
        /// </summary>
        public static bool Fail(ShardDbContext context, long ticketId, string resultCode, string message, DateTime now)
        {
            now = ListingStore.Truncate(now);
            message = Cap(message);

            return context.MarketTickets
                .Where(t => t.Id == ticketId && t.Status == TicketStatus.Claimed)
                .ExecuteUpdate(s => s.SetProperty(t => t.Status, TicketStatus.Failed).SetProperty(t => t.ResultCode, resultCode).SetProperty(t => t.ResultMessage, message).SetProperty(t => t.FinishedTime, now)) == 1;
        }

        /// <summary>
        /// Startup: every CLAIMED ticket was being worked on by a server that stopped, and that work was never saved
        /// (finished work marks its ticket DONE in the same save), so each becomes FAILED. Returns how many.
        /// </summary>
        public static int FailAllClaimed(ShardDbContext context, string message, DateTime now)
        {
            now = ListingStore.Truncate(now);
            message = Cap(message);

            return context.MarketTickets
                .Where(t => t.Status == TicketStatus.Claimed)
                .ExecuteUpdate(s => s.SetProperty(t => t.Status, TicketStatus.Failed).SetProperty(t => t.ResultCode, ServerRestart).SetProperty(t => t.ResultMessage, message).SetProperty(t => t.FinishedTime, now));
        }

        /// <summary>
        /// Deletes DONE and FAILED tickets that finished more than KeepDays ago. Returns how many.
        /// </summary>
        public static int DeleteFinished(ShardDbContext context, DateTime now)
        {
            var before = now - TimeSpan.FromDays(KeepDays);

            return context.MarketTickets
                .Where(t => (t.Status == TicketStatus.Done || t.Status == TicketStatus.Failed) && t.FinishedTime < before)
                .ExecuteDelete();
        }

        /// <summary>
        /// Adds marking a claimed ticket DONE to the caller's context, to be saved with the caller's other changes.
        /// The update only matches a ticket that is still CLAIMED (the status is a concurrency token), so otherwise the whole save fails.
        /// </summary>
        public static void Complete(ShardDbContext context, TicketCompletion completion, DateTime now)
        {
            var ticket = new Ticket { Id = completion.TicketId, Status = TicketStatus.Claimed };

            context.MarketTickets.Attach(ticket);

            ticket.Status = TicketStatus.Done;
            ticket.ResultCode = Ok;
            ticket.ResultMessage = Cap(completion.Message);
            ticket.FinishedTime = ListingStore.Truncate(now);
        }

        private static TicketCreateResult Existing(ShardDbContext context, uint accountId, string idempotencyKey, string kind)
        {
            var existing = context.MarketTickets.AsNoTracking().FirstOrDefault(t => t.AccountId == accountId && t.IdempotencyKey == idempotencyKey);

            if (existing == null)
                return null;

            return new TicketCreateResult(existing.Kind == kind ? TicketCreateOutcome.Existing : TicketCreateOutcome.KeyReused, existing);
        }

        /// <summary>
        /// The result message column holds 512 characters
        /// </summary>
        private static string Cap(string message) => message != null && message.Length > 512 ? message.Substring(0, 512) : message;
    }
}
