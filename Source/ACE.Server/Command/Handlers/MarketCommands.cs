using System;
using System.Globalization;
using System.Linq;

using ACE.Database.Market;
using ACE.Entity.Enum;
using ACE.Server.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /market: the ledger audit, lifting the pause a failed audit sets, and admin corrections to the ledger. Admins only.
    /// </summary>
    public static class MarketCommands
    {
        private const string Usage = "/market audit\n/market resume\n/market adjust <account> <+/-mmd> <reason>\n/market reverse <transfer> <reason>";

        [CommandHandler("market", AccessLevel.Admin, CommandHandlerFlag.None, 1,
            "Audit the market ledger, lift its pause, or correct it",
            "audit | resume | adjust <account> <+/-mmd> <reason> | reverse <transfer> <reason>")]
        public static void HandleMarket(Session session, params string[] parameters)
        {
            if (!Vault.Available)
            {
                Tell(session, "The market is not available: its tables are missing.");
                return;
            }

            var by = session == null ? "the console" : $"{session.Player?.Name} (account {session.AccountId})";

            switch (parameters.Length == 0 ? "" : parameters[0].ToLowerInvariant())
            {
                case "audit":
                    Tell(session, "Auditing the market ledger...");
                    MarketAdmin.Audit(by, report => TellAudit(session, report));
                    break;

                case "resume":
                    Tell(session, MarketAdmin.Resume(by)
                        ? "The market is resumed: purchases and MMD withdrawals are allowed again."
                        : "The market is not paused.");
                    break;

                case "adjust":
                    Adjust(session, parameters);
                    break;

                case "reverse":
                    Reverse(session, parameters);
                    break;

                default:
                    Tell(session, Usage);
                    break;
            }
        }

        private static void Adjust(Session session, string[] parameters)
        {
            if (parameters.Length < 4 || !long.TryParse(parameters[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount) || amount == 0)
            {
                Tell(session, "Usage: /market adjust <account> <+/-mmd> <reason>, a whole number of MMD other than 0 and a reason.");
                return;
            }

            if (!TryAdmin(session, out var adminAccountId, out var adminCharacterId))
                return;

            var accountId = MarketAdmin.FindAccount(parameters[1]);

            if (accountId == null)
            {
                Tell(session, $"No account {parameters[1]}. Name an account, or the id of one with a market balance.");
                return;
            }

            var account = accountId.Value;

            MarketAdmin.Adjust(account, amount, Reason(parameters), adminAccountId, adminCharacterId, result => Tell(session, result?.Outcome switch
            {
                CorrectionOutcome.Done => $"Adjusted account {account} by {amount:+#;-#} MMD (transfer {result.TransferId}). Its balance is {result.Balance:N0} MMD.",
                CorrectionOutcome.InsufficientFunds => $"That would take account {account} below 0 MMD. Nothing was changed.",
                _ => Refused(result),
            }));
        }

        private static void Reverse(Session session, string[] parameters)
        {
            if (parameters.Length < 3 || !long.TryParse(parameters[1], NumberStyles.None, CultureInfo.InvariantCulture, out var transferId))
            {
                Tell(session, "Usage: /market reverse <transfer> <reason>, a transfer id and a reason.");
                return;
            }

            if (!TryAdmin(session, out var adminAccountId, out var adminCharacterId))
                return;

            MarketAdmin.Reverse(transferId, Reason(parameters), adminAccountId, adminCharacterId, result => Tell(session, result?.Outcome switch
            {
                CorrectionOutcome.Done => $"Reversed transfer {transferId} (transfer {result.TransferId}).",
                CorrectionOutcome.UnknownTransfer => $"There is no transfer {transferId}.",
                CorrectionOutcome.AlreadyReversed => $"Transfer {transferId} has already been reversed.",
                CorrectionOutcome.Unbalanced => $"Transfer {transferId} doesn't add up to zero, so it can't be reversed. Correct it with /market adjust.",
                CorrectionOutcome.InsufficientFunds => $"Reversing transfer {transferId} would take a balance below 0 MMD. Nothing was changed.",
                _ => Refused(result),
            }));
        }

        /// <summary>
        /// A correction is recorded with the admin's account and character, so it can't come from the console
        /// </summary>
        private static bool TryAdmin(Session session, out uint accountId, out uint characterId)
        {
            accountId = session?.AccountId ?? 0;
            characterId = session?.Player?.Guid.Full ?? 0;

            if (session?.Player != null)
                return true;

            Tell(session, "Ledger corrections are recorded with the admin who makes them: use this command in game.");
            return false;
        }

        private static string Reason(string[] parameters) => string.Join(" ", parameters.Skip(parameters[0].Equals("adjust", StringComparison.OrdinalIgnoreCase) ? 3 : 2));

        private static string Refused(CorrectionResult result) => result?.Outcome switch
        {
            CorrectionOutcome.InvalidMemo => $"Give a reason of at most {LedgerCorrections.MaxMemoLength} characters.",
            CorrectionOutcome.InvalidAmount => "Adjust by a whole number of MMD other than 0.",
            CorrectionOutcome.Busy => "The ledger was busy. Nothing was changed; try again.",
            _ => "The correction failed. Nothing was changed; see the server log.",
        };

        private static void TellAudit(Session session, LedgerAuditReport report)
        {
            if (report == null)
            {
                Tell(session, "The ledger audit could not run; see the server log.");
                return;
            }

            if (report.Passed)
            {
                Tell(session, report.Summary + ".");
                return;
            }

            Tell(session, $"{report.Summary}. The market is paused: purchases and MMD withdrawals are stopped until /market resume.");

            const int shown = 10;

            foreach (var failure in report.Failures.Take(shown))
                Tell(session, $"{failure.Check}: {failure.Detail}");

            if (report.Failures.Count > shown)
                Tell(session, $"...and {report.Failures.Count - shown} more in the server log.");
        }

        /// <summary>
        /// To the admin's chat, as /vault does, or to the console's log
        /// </summary>
        private static void Tell(Session session, string message)
        {
            if (session == null)
                CommandHandlerHelper.WriteOutputInfo(null, message);
            else
                session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }
    }
}
