using System;

using log4net;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

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

        // result codes of the bridge's own refusals; the Vault's refusals have theirs in ResultCode
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
        /// (finished work marks its ticket DONE in the same save), so each becomes FAILED. Long-finished tickets are deleted, and so are market rows nothing reads any more (MarketCleanup).
        /// </summary>
        public static void Recover()
        {
            var now = DateTime.UtcNow;

            using (var context = new ShardDbContext())
            {
                var failed = TicketStore.FailAllClaimed(context, Message(TicketStore.ServerRestart), now);

                if (failed > 0)
                    log.Warn($"[BRIDGE] Failed {failed:N0} ticket(s) the last shutdown left claimed");

                LogDeleted(TicketStore.DeleteFinished(context, now));

                LogCleanup(MarketCleanup.Run(context, now));
            }

            nextCleanup = now + CleanupInterval;
        }

        /// <summary>
        /// Every pass of the world loop: about once a second, asks the save thread to claim waiting tickets, then runs them on the world thread.
        /// One poll at a time. The hourly cleanup (finished tickets, then MarketCleanup) rides on the same queue.
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

                DatabaseManager.Shard.DeleteFinishedTickets(LogDeleted);
                DatabaseManager.Shard.DeleteExpiredMarketRows(LogCleanup);
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
        /// The result code a ticket stopped by a Vault outcome fails with. Written out rather than derived from the names, so renaming an outcome
        /// can't change what the API answers. Successes never fail a ticket.
        /// </summary>
        public static string ResultCode(VaultOutcome outcome) => outcome switch
        {
            VaultOutcome.NotAvailable => "not_available",
            VaultOutcome.Busy => "busy",
            VaultOutcome.InTrade => "in_trade",
            VaultOutcome.NotInPack => "not_in_pack",
            VaultOutcome.Worn => "worn",
            VaultOutcome.Attuned => "attuned",
            VaultOutcome.ContainsAttuned => "contains_attuned",
            VaultOutcome.PetOut => "pet_out",
            VaultOutcome.ContainerNotEmpty => "container_not_empty",
            VaultOutcome.BlockedWcid => "blocked_wcid",
            VaultOutcome.VaultFull => "vault_full",
            VaultOutcome.NotInVault => "not_in_vault",
            VaultOutcome.Listed => "listed",
            VaultOutcome.Withdrawing => "withdrawing",
            VaultOutcome.NoPackSpace => "no_pack_space",
            VaultOutcome.UniqueLimit => "unique_limit",
            VaultOutcome.RecentPlayerFight => "recent_player_fight",
            VaultOutcome.Trading => "trading",
            VaultOutcome.Channelling => "channelling",
            VaultOutcome.Interrupted => "interrupted",
            VaultOutcome.SaveFailed => "save_failed",
            VaultOutcome.NoNotes => "no_notes",
            VaultOutcome.InvalidAmount => "invalid_amount",
            VaultOutcome.InsufficientFunds => "insufficient_funds",
            VaultOutcome.Paused => "paused",
            _ => "failed",
        };

        /// <summary>
        /// Runs a claimed ticket on the world thread
        /// </summary>
        private static void Run(Ticket ticket)
        {
            var payload = TicketPayload.FromJson(ticket.Payload);
            Action<Player> work;

            switch (ticket.Kind)
            {
                case TicketKind.VaultWithdraw when payload?.ItemGuid is uint itemGuid:
                    work = player => VaultChannel.StartWithdraw(player, itemGuid, result => Finished(ticket, result), ticket.Id);
                    break;

                case TicketKind.MmdWithdraw when payload?.Amount is long amount:
                    work = player => Vault.WithdrawNotes(player, amount, result => Finished(ticket, result), ticket.Id);
                    break;

                case TicketKind.VaultWithdraw:
                case TicketKind.MmdWithdraw:
                    Fail(ticket, InvalidTicket);
                    return;

                default:
                    // vault_deposit needs a live-inventory picker that isn't built yet
                    Fail(ticket, UnsupportedKind);
                    return;
            }

            var character = ticket.CharacterId == null ? null : PlayerManager.GetOnlinePlayer(ticket.CharacterId.Value);

            if (character != null && character.Character.AccountId != ticket.AccountId)
            {
                Fail(ticket, InvalidCharacter);
                return;
            }

            if (character == null || character.IsLoggingOut)
            {
                Fail(ticket, Offline);
                return;
            }

            work(character);
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
            message ??= Message(resultCode);

            DatabaseManager.Shard.FailTicket(ticket.Id, resultCode, message, failed =>
            {
                if (!failed)
                    log.Warn($"[BRIDGE] Could not mark ticket {ticket.Id} ({ticket.Kind}, account {ticket.AccountId}) failed with {resultCode}: it is no longer claimed, or the write failed");
            });
        }

        /// <summary>
        /// What the player is told when the bridge itself stops a ticket
        /// </summary>
        private static string Message(string resultCode) => resultCode switch
        {
            Offline => "That character is not online. Log in with it and ask again.",
            InvalidCharacter => "That character is not on your account.",
            UnsupportedKind => "The game server cannot do that kind of request yet.",
            InvalidTicket => "The game server could not read that request.",
            TicketStore.ServerRestart => "The game server restarted before this finished. Nothing was moved; ask again.",
            _ => resultCode,
        };

        private static void LogDeleted(int deleted)
        {
            if (deleted > 0)
                log.Info($"[BRIDGE] Deleted {deleted:N0} ticket(s) finished more than {TicketStore.KeepDays} days ago");
        }

        private static void LogCleanup(MarketCleanupReport deleted)
        {
            if (deleted?.Total > 0)
                log.Info($"[MARKET] Deleted {deleted.Requests:N0} request result(s) older than {MarketCleanup.RequestKeepDays} days, {deleted.LinkCodes:N0} used or expired link code(s) " +
                    $"and {deleted.PluginTokens:N0} plugin token(s) revoked or expired more than {MarketCleanup.PluginTokenKeepDays} days ago");
        }
    }
}
