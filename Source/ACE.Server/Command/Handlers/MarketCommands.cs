using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Server.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /market: the ledger audit, lifting the pause a failed audit sets, admin corrections to the ledger, the Vault's WCID blocklist,
    /// and the refresh of the Vault search columns after shard SQL updates. Admins only.
    /// </summary>
    public static class MarketCommands
    {
        private const string Usage = "/market audit\n/market resume\n/market adjust <account> <+/-mmd> <reason>\n/market reverse <transfer> <reason>\n" +
            "/market block <wcid> <reason>\n/market unblock <wcid>\n/market blocked\n/market refresh [wcid]";

        [CommandHandler("market", AccessLevel.Admin, CommandHandlerFlag.None, 1,
            "Audit the market ledger, lift its pause, correct it, block item types from the Vault, or refresh the Vault's search columns",
            "audit | resume | adjust <account> <+/-mmd> <reason> | reverse <transfer> <reason> | block <wcid> <reason> | unblock <wcid> | blocked | refresh [wcid]")]
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
                    MarketAdmin.Resume(by, resumed => Tell(session, resumed switch
                    {
                        true => "The market is resumed: purchases and MMD withdrawals are allowed again.",
                        false => "The market is not paused.",
                        null => "The market could not be resumed; see the server log.",
                    }));
                    break;

                case "adjust":
                    Adjust(session, parameters);
                    break;

                case "reverse":
                    Reverse(session, parameters);
                    break;

                case "block":
                    Block(session, parameters);
                    break;

                case "unblock":
                    Unblock(session, parameters, by);
                    break;

                case "blocked":
                    MarketAdmin.Blocked(blocks => TellBlocked(session, blocks));
                    break;

                case "refresh":
                    Refresh(session, parameters, by);
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

            if (!TryAdmin(session, CorrectionsInGame, out var adminAccountId, out var adminCharacterId))
                return;

            var account = parameters[1];

            MarketAdmin.Adjust(account, amount, Memo(parameters, 3), adminAccountId, adminCharacterId, result => Tell(session, result?.Outcome switch
            {
                CorrectionOutcome.Done => $"Adjusted account {account} by {amount:+#;-#} MMD (transfer {result.TransferId}). Its balance is {result.Balance:N0} MMD.",
                CorrectionOutcome.UnknownAccount => $"No account {account}. Name an account, or the id of one with a market balance.",
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

            if (!TryAdmin(session, CorrectionsInGame, out var adminAccountId, out var adminCharacterId))
                return;

            MarketAdmin.Reverse(transferId, Memo(parameters, 2), adminAccountId, adminCharacterId, result => Tell(session, result?.Outcome switch
            {
                CorrectionOutcome.Done => $"Reversed transfer {transferId} (transfer {result.TransferId}). Only MMD moved: items and trade notes stay where they are.",
                CorrectionOutcome.UnknownTransfer => $"There is no transfer {transferId}.",
                CorrectionOutcome.AlreadyReversed => $"Transfer {transferId} has already been reversed.",
                CorrectionOutcome.Unbalanced => $"Transfer {transferId} doesn't add up to zero, so it can't be reversed. Correct it with /market adjust.",
                CorrectionOutcome.InsufficientFunds => $"Reversing transfer {transferId} would take a balance below 0 MMD. Nothing was changed.",
                _ => Refused(result),
            }));
        }

        private static void Block(Session session, string[] parameters)
        {
            var reason = Memo(parameters, 2);

            if (parameters.Length < 3 || !TryWcid(parameters[1], out var wcid) || reason.Length > VaultStore.MaxBlockReasonLength)
            {
                Tell(session, $"Usage: /market block <wcid> <reason>, a weenie class id and a reason of at most {VaultStore.MaxBlockReasonLength} characters.");
                return;
            }

            if (!TryAdmin(session, "A block is recorded with the admin who adds it: use this command in game.", out var adminAccountId, out _))
                return;

            MarketAdmin.Block(wcid, reason, adminAccountId, result => Tell(session, result?.Outcome switch
            {
                BlockOutcome.Blocked => $"Blocked {wcid} ({result.WeenieName}) from new Vault deposits: {reason}." + InVaults(result.InVaults),
                BlockOutcome.AlreadyBlocked => $"{wcid} ({result.WeenieName}) is already blocked: {result.Block.Reason} (account {result.Block.AddedByAccountId}, {Utc(result.Block.AddedTime)}). Nothing was changed.",
                BlockOutcome.UnknownWeenie => $"No weenie {wcid} in the world database. Nothing was changed.",
                _ => "The block failed. Nothing was changed; see the server log.",
            }));
        }

        private static string InVaults(int count) => count == 0 ? "" : $" {count:N0} already in Vaults stay there; they can still be listed, bought and withdrawn.";

        private static void Unblock(Session session, string[] parameters, string by)
        {
            if (parameters.Length != 2 || !TryWcid(parameters[1], out var wcid))
            {
                Tell(session, "Usage: /market unblock <wcid>");
                return;
            }

            MarketAdmin.Unblock(wcid, by, removed => Tell(session, removed switch
            {
                true => $"Unblocked {wcid}: it can be deposited in the Vault again.",
                false => $"{wcid} is not blocked.",
                null => "The unblock failed. Nothing was changed; see the server log.",
            }));
        }

        private static void TellBlocked(Session session, List<BlockedWcid> blocks)
        {
            if (blocks == null)
            {
                Tell(session, "The blocklist could not be read; see the server log.");
                return;
            }

            if (blocks.Count == 0)
            {
                Tell(session, "No item types are blocked from the Vault.");
                return;
            }

            Tell(session, $"{blocks.Count} item type(s) blocked from the Vault:");

            foreach (var block in blocks)
                Tell(session, $"{block.Wcid}: {block.Reason} (account {block.AddedByAccountId}, {Utc(block.AddedTime)})");
        }

        private static void Refresh(Session session, string[] parameters, string by)
        {
            uint wcid = 0;

            if (parameters.Length > 2 || (parameters.Length == 2 && !TryWcid(parameters[1], out wcid)))
            {
                Tell(session, "Usage: /market refresh [wcid], every Vault item or only those of one weenie class.");
                return;
            }

            uint? onlyWcid = parameters.Length == 2 ? wcid : null;

            Tell(session, "Refreshing the Vault search columns...");

            MarketAdmin.Refresh(onlyWcid, by, report => Tell(session, report switch
            {
                null => "The refresh failed; see the server log.",
                { Failed: 0 } => $"Refreshed the search columns of {report.Refreshed:N0} Vault item(s).{Gone(report.Gone)}",
                _ => $"Refreshed the search columns of {report.Refreshed:N0} Vault item(s);{Gone(report.Gone)} {report.Failed:N0} could not be refreshed, see the server log.",
            }));
        }

        private static string Gone(int count) => count == 0 ? "" : $" {count:N0} left the Vault while it ran.";

        private static bool TryWcid(string text, out uint wcid) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out wcid) && wcid > 0;

        private static string Utc(DateTime time) => time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

        private const string CorrectionsInGame = "Ledger corrections are recorded with the admin who makes them: use this command in game.";

        /// <summary>
        /// A correction or a block is recorded with the admin's account (and character), so it can't come from the console
        /// </summary>
        private static bool TryAdmin(Session session, string refusal, out uint accountId, out uint characterId)
        {
            accountId = session?.AccountId ?? 0;
            characterId = session?.Player?.Guid.Full ?? 0;

            if (session?.Player != null)
                return true;

            Tell(session, refusal);
            return false;
        }

        /// <param name="words">how many words come before the memo: the verb and its arguments</param>
        private static string Memo(string[] parameters, int words) => string.Join(" ", parameters.Skip(words));

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
