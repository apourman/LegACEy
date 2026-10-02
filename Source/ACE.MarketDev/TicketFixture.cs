using System;
using System.Linq;
using System.Threading;

using Microsoft.EntityFrameworkCore;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.MarketDev
{
    /// <summary>
    /// Ticket presentation fixtures for the local website. Every durable write is a market_ticket row only.
    /// </summary>
    internal static class TicketFixture
    {
        private const string UnknownKind = "marketdev_unknown_probe";

        public static bool Run(string characterName)
        {
            uint accountId;
            uint characterId;

            using (var shard = new ShardDbContext())
            {
                var character = shard.Character.AsNoTracking()
                    .Where(c => c.Name == characterName && !c.IsDeleted)
                    .Select(c => new { c.Id, c.AccountId })
                    .SingleOrDefault();

                if (character == null)
                    throw new InvalidOperationException($"No live character named '{characterName}' exists in the configured shard.");

                accountId = character.AccountId;
                characterId = character.Id;
            }

            var probeId = Create(accountId, characterId, UnknownKind, "fixture-probe-" + Guid.NewGuid().ToString("N"));
            var gameIsRunning = false;

            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    using var shard = new ShardDbContext();
                    var probe = shard.MarketTickets.AsNoTracking().Single(t => t.Id == probeId);

                    if (probe.ClaimedTime != null || probe.Status != TicketStatus.Waiting)
                    {
                        gameIsRunning = true;
                        break;
                    }

                    Thread.Sleep(200);
                }

                if (!gameIsRunning)
                {
                    using var shard = new ShardDbContext();
                    var probe = shard.MarketTickets.AsNoTracking().Single(t => t.Id == probeId);
                    gameIsRunning = probe.ClaimedTime != null || probe.Status != TicketStatus.Waiting;
                }
            }
            finally
            {
                using var shard = new ShardDbContext();
                shard.MarketTickets.Where(t => t.Id == probeId).ExecuteDelete();
            }

            if (gameIsRunning)
            {
                Console.Error.WriteLine("Ticket fixture refused: the game server claimed its probe ticket. Stop the game server, then retry.");
                return false;
            }

            var now = DateTime.UtcNow;
            var waiting = Create(accountId, characterId, TicketKind.MmdWithdraw, "fixture-waiting-" + Guid.NewGuid().ToString("N"));
            var working = Create(accountId, characterId, TicketKind.MmdWithdraw, "fixture-working-" + Guid.NewGuid().ToString("N"));
            Claim(working, now);

            var channelling = Create(accountId, characterId, TicketKind.VaultWithdraw, "fixture-channel-" + Guid.NewGuid().ToString("N"));
            Claim(channelling, now);
            Progress(channelling, "channelling", now, now.AddSeconds(60));

            var done = Create(accountId, characterId, TicketKind.MmdWithdraw, "fixture-done-" + Guid.NewGuid().ToString("N"));
            Claim(done, now);
            Finish(done, TicketStatus.Done, TicketStore.Ok, "Fixture complete. No MMD or item was moved.", now,
                "{\"message\":\"Fixture complete. No MMD or item was moved.\"}");

            var offline = Create(accountId, characterId, TicketKind.VaultWithdraw, "fixture-offline-" + Guid.NewGuid().ToString("N"));
            Claim(offline, now);
            Fail(offline, "offline", "Your character is offline. This fixture did not move an item.", now);

            var paused = Create(accountId, characterId, TicketKind.MmdWithdraw, "fixture-paused-" + Guid.NewGuid().ToString("N"));
            Claim(paused, now);
            Fail(paused, "paused", "The market is paused. This fixture did not change your balance.", now);

            Console.WriteLine("Ticket fixture created presentation-only tickets (all writes were limited to market_ticket):");
            Console.WriteLine($"  WAITING: #{waiting}");
            Console.WriteLine($"  CLAIMED / Working: #{working}");
            Console.WriteLine($"  CLAIMED / Channelling: #{channelling}");
            Console.WriteLine($"  DONE: #{done}");
            Console.WriteLine($"  FAILED / offline: #{offline}");
            Console.WriteLine($"  FAILED / paused: #{paused}");
            return true;
        }

        private static long Create(uint accountId, uint characterId, string kind, string key)
        {
            using var shard = new ShardDbContext();
            var ticket = new Ticket
            {
                Kind = kind,
                AccountId = accountId,
                CharacterId = characterId,
                Payload = new TicketPayload(Amount: 25).ToJson(),
                Status = TicketStatus.Waiting,
                IdempotencyKey = key,
                CreatedTime = DateTime.UtcNow,
            };

            shard.MarketTickets.Add(ticket);
            shard.SaveChanges();
            return ticket.Id;
        }

        private static void Claim(long ticketId, DateTime now)
        {
            using var shard = new ShardDbContext();
            var changed = shard.MarketTickets.Where(t => t.Id == ticketId && t.Status == TicketStatus.Waiting)
                .ExecuteUpdate(s => s.SetProperty(t => t.Status, TicketStatus.Claimed).SetProperty(t => t.ClaimedTime, now));
            EnsureOneRow(changed, ticketId, "claim");
        }

        private static void Progress(long ticketId, string progress, DateTime now, DateTime until)
        {
            using var shard = new ShardDbContext();
            if (!TicketStore.SetProgress(shard, ticketId, progress, now, until))
                throw new InvalidOperationException($"Ticket {ticketId} could not advance to {progress}.");
        }

        private static void Finish(long ticketId, string status, string code, string message, DateTime now, string result)
        {
            using var shard = new ShardDbContext();
            var changed = shard.MarketTickets.Where(t => t.Id == ticketId && t.Status == TicketStatus.Claimed)
                .ExecuteUpdate(s => s.SetProperty(t => t.Status, status)
                    .SetProperty(t => t.ResultCode, code)
                    .SetProperty(t => t.ResultMessage, message)
                    .SetProperty(t => t.Result, result)
                    .SetProperty(t => t.FinishedTime, now));
            EnsureOneRow(changed, ticketId, status);
        }

        private static void Fail(long ticketId, string code, string message, DateTime now)
        {
            using var shard = new ShardDbContext();
            if (!TicketStore.Fail(shard, ticketId, code, message, now))
                throw new InvalidOperationException($"Ticket {ticketId} could not advance to FAILED.");
        }

        private static void EnsureOneRow(int changed, long ticketId, string action)
        {
            if (changed != 1)
                throw new InvalidOperationException($"Ticket {ticketId} could not advance to {action}.");
        }
    }
}
