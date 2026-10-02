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

        private sealed record FailureExample(string Code, string Message);

        private static readonly FailureExample[] FailureExamples =
        {
            new("offline", "That character is not online. Log in with it and ask again."),
            new("invalid_character", "That character is not on your account."),
            new("unsupported_kind", "The game server cannot do that kind of request yet."),
            new("invalid_ticket", "The game server could not read that request."),
            new("server_restart", "The game server restarted before this finished. Nothing was moved; ask again."),
            new("abandoned", "The game server stopped working on this before it finished. Nothing was moved; ask again."),
            new("not_available", "The Vault is not available right now."),
            new("busy", "Wait for your last Vault action to finish."),
            new("not_in_vault", "That item is not in your Vault. Use /vault list to see its id."),
            new("listed", "That item is listed for sale. Delist it before you withdraw it."),
            new("withdrawing", "That item is already being withdrawn."),
            new("no_pack_space", "You do not have room in your pack for the item or trade notes."),
            new("unique_limit", "You cannot carry any more of this item."),
            new("recent_player_fight", "You have been in a player fight too recently to use the Vault. Try again in a couple of minutes."),
            new("trading", "Close the trade window before you use the Vault."),
            new("channelling", "You are already moving an item to or from your Vault."),
            new("interrupted", "Your Vault channel was interrupted. The item did not move."),
            new("save_failed", "The Vault could not save your item or trade notes. Nothing was changed."),
            new("unconfirmed", "The Vault could not confirm whether the item or trade notes moved. Log out and back in, then check your pack and Vault balance."),
            new("banned", "Your account is banned, so its Vault and balance are frozen."),
            new("invalid_amount", "Withdraw at least 1 MMD."),
            new("insufficient_funds", "You do not have enough MMD for this withdrawal."),
            new("paused", "The market is paused, so MMD withdrawals are stopped for now."),
        };

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

            Console.WriteLine("Ticket fixture created presentation-only tickets (all writes were limited to market_ticket):");
            Console.WriteLine($"  WAITING: #{waiting}");
            Console.WriteLine($"  CLAIMED / Working: #{working}");
            Console.WriteLine($"  CLAIMED / Channelling: #{channelling}");
            Console.WriteLine($"  DONE: #{done}");
            foreach (var failure in FailureExamples)
            {
                var failed = Create(accountId, characterId, FailureKind(failure.Code), "fixture-" + failure.Code + "-" + Guid.NewGuid().ToString("N"));
                Claim(failed, now);
                Fail(failed, failure.Code, failure.Message, now);
                Console.WriteLine($"  FAILED / {failure.Code}: #{failed}");
            }

            return true;
        }

        private static string FailureKind(string code) => code is "invalid_amount" or "insufficient_funds" or "paused"
            ? TicketKind.MmdWithdraw
            : TicketKind.VaultWithdraw;

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
