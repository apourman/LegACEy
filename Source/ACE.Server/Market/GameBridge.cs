using System;
using System.Text.RegularExpressions;

using log4net;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

namespace ACE.Server.Market
{
    /// <summary>
    /// The game server's side of the game bridge. About once a second the save thread claims waiting tickets (market_ticket), and the world thread
    /// runs each through the same Vault entry point as the /vault commands: an item withdrawal takes the channel, so the character must be online.
    /// Finished work marks its ticket DONE in the same save; anything that stops it marks the ticket FAILED with a result code and the player's message.
    /// </summary>
    public static class GameBridge
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // result codes of the bridge's own refusals; the Vault's refusals use their outcome's name (see ResultCode)
        public const string Offline = "offline";
        public const string InvalidCharacter = "invalid_character";
        public const string UnsupportedKind = "unsupported_kind";
        public const string InvalidTicket = "invalid_ticket";

        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// The most tickets one poll claims
        /// </summary>
        public const int ClaimLimit = 50;

        private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

        // only the world thread touches these
        private static bool polling;
        private static DateTime nextPoll;
        private static DateTime nextCleanup;

        /// <summary>
        /// Startup, before the world runs. A ticket still CLAIMED belonged to a server that stopped mid-work, and none of that work was saved
        /// (finished work marks its ticket DONE in the same save), so each becomes FAILED. Long-finished tickets are deleted.
        /// </summary>
        public static void Recover()
        {
            var now = DateTime.UtcNow;

            using (var context = new ShardDbContext())
            {
                var failed = TicketStore.FailAllClaimed(context, VaultMessages.ForTicket(TicketStore.ServerRestart), now);

                if (failed > 0)
                    log.Warn($"[BRIDGE] Failed {failed:N0} ticket(s) the last shutdown left claimed");

                var deleted = TicketStore.DeleteFinished(context, now);

                if (deleted > 0)
                    log.Info($"[BRIDGE] Deleted {deleted:N0} ticket(s) finished more than {TicketStore.KeepDays} days ago");
            }

            nextCleanup = now + CleanupInterval;
        }

        /// <summary>
        /// Every pass of the world loop: about once a second, asks the save thread to claim waiting tickets, then runs them on the world thread.
        /// One poll at a time, and the hourly cleanup rides on the same queue.
        /// </summary>
        public static void Tick()
        {
            if (!Vault.Available || polling)
                return;

            var now = DateTime.UtcNow;

            if (now < nextPoll)
                return;

            polling = true;
            nextPoll = now + PollInterval;

            if (now >= nextCleanup)
            {
                nextCleanup = now + CleanupInterval;

                DatabaseManager.Shard.DeleteFinishedTickets(deleted =>
                {
                    if (deleted > 0)
                        log.Info($"[BRIDGE] Deleted {deleted:N0} ticket(s) finished more than {TicketStore.KeepDays} days ago");
                });
            }

            DatabaseManager.Shard.ClaimTickets(ClaimLimit, tickets =>
            {
                // this runs on the save thread
                WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                {
                    polling = false;

                    foreach (var ticket in tickets)
                        Run(ticket);
                }));
            });
        }

        /// <summary>
        /// The result code for a Vault outcome that stopped a ticket: its name in snake case (NoPackSpace is no_pack_space)
        /// </summary>
        public static string ResultCode(VaultOutcome outcome) => Regex.Replace(outcome.ToString(), "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

        /// <summary>
        /// Runs a claimed ticket on the world thread
        /// </summary>
        private static void Run(Ticket ticket)
        {
            var payload = TicketPayload.FromJson(ticket.Payload);

            if (ticket.Kind != TicketKind.VaultWithdraw && ticket.Kind != TicketKind.MmdWithdraw)
            {
                // vault_deposit needs a live-inventory picker that isn't built yet
                Fail(ticket, UnsupportedKind);
                return;
            }

            if (payload == null || (ticket.Kind == TicketKind.VaultWithdraw ? payload.ItemGuid == null : payload.Amount == null))
            {
                Fail(ticket, InvalidTicket);
                return;
            }

            var player = ticket.CharacterId == null ? null : PlayerManager.GetOnlinePlayer(ticket.CharacterId.Value);

            if (player != null && player.Character.AccountId != ticket.AccountId)
            {
                Fail(ticket, InvalidCharacter);
                return;
            }

            if (player == null || player.IsLoggingOut)
            {
                Fail(ticket, Offline);
                return;
            }

            if (ticket.Kind == TicketKind.VaultWithdraw)
                VaultChannel.StartWithdraw(player, payload.ItemGuid.Value, result => Finished(ticket, result), ticket.Id);
            else
                Vault.WithdrawNotes(player, payload.Amount.Value, result => Finished(ticket, result), ticket.Id);
        }

        private static void Finished(Ticket ticket, VaultResult result)
        {
            // success was saved together with the ticket's DONE
            if (result.Success)
                return;

            Fail(ticket, ResultCode(result.Outcome), result.Message);
        }

        private static void Fail(Ticket ticket, string resultCode, string message = null)
        {
            message ??= VaultMessages.ForTicket(resultCode);

            DatabaseManager.Shard.FailTicket(ticket.Id, resultCode, message, failed =>
            {
                if (!failed)
                    log.Warn($"[BRIDGE] Could not mark ticket {ticket.Id} ({ticket.Kind}, account {ticket.AccountId}) failed with {resultCode}: it is no longer claimed, or the write failed");
            });
        }
    }
}
