using System;
using System.Globalization;

using ACE.Entity.Enum;
using ACE.Server.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// The /vault commands for trade notes, balance, plugin links and tokens. Items move at the Vault chest in Yaraq, over the Vault channel.
    /// Trade notes (MMD) go in and out of the account balance instantly, never through the channel.
    /// </summary>
    public static class VaultCommands
    {
        private const string ChestUsage = "Move items in and out of your Vault at the Vault chest in Yaraq.";

        private const string MarketUsage = "/vault deposit mmd  (all your trade notes)\n/vault withdraw mmd <amount>\n/vault balance\n/vault link  (a code for the UtilityBelt plugin)\n/vault tokens\n/vault tokens revoke <id>";

        /// <summary>
        /// The trade note and plugin lines only while the marketplace is open
        /// </summary>
        private static string Usage => Vault.MarketEnabled ? ChestUsage + "\n" + MarketUsage : ChestUsage;

        [CommandHandler("vault", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Vault trade notes, balance, plugin link and tokens. Items move at the Vault chest in Yaraq",
            "deposit mmd | withdraw mmd <amount> | balance | link | tokens | tokens revoke <id>")]
        public static void HandleVault(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (parameters.Length == 0)
            {
                Tell(session, Usage);
                return;
            }

            switch (parameters[0].ToLowerInvariant())
            {
                case "deposit" when parameters.Length >= 2 && IsMmd(parameters[1]):
                    Vault.DepositNotes(player);
                    break;

                case "withdraw" when parameters.Length >= 2 && IsMmd(parameters[1]):
                    if (parameters.Length < 3 || !long.TryParse(parameters[2], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                    {
                        Tell(session, "Usage: /vault withdraw mmd <amount>, a whole number of trade notes.");
                        return;
                    }

                    Vault.WithdrawNotes(player, amount);
                    return;

                case "balance":
                    if (!Vault.Available)
                    {
                        Tell(session, VaultMessages.For(VaultOutcome.NotAvailable, null));
                        return;
                    }

                    if (!Vault.MarketEnabled)
                    {
                        Tell(session, VaultMessages.For(VaultOutcome.MarketClosed, null));
                        return;
                    }

                    Tell(session, VaultMessages.Balance(Vault.Balance(player)));
                    break;

                case "link":
                    if (!Vault.Available)
                    {
                        Tell(session, VaultMessages.For(VaultOutcome.NotAvailable, null));
                        return;
                    }

                    if (!Vault.MarketEnabled)
                    {
                        Tell(session, VaultMessages.For(VaultOutcome.MarketClosed, null));
                        return;
                    }

                    Tell(session, VaultMessages.LinkCode(VaultPlugin.NewLinkCode(player, out var minutes), minutes));
                    break;

                case "tokens":
                    if (!Vault.Available)
                    {
                        Tell(session, VaultMessages.For(VaultOutcome.NotAvailable, null));
                        return;
                    }

                    if (!Vault.MarketEnabled)
                    {
                        Tell(session, VaultMessages.For(VaultOutcome.MarketClosed, null));
                        return;
                    }

                    if (parameters.Length >= 2 && parameters[1].Equals("revoke", StringComparison.OrdinalIgnoreCase))
                    {
                        if (parameters.Length < 3 || !long.TryParse(parameters[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tokenId))
                        {
                            Tell(session, "Usage: /vault tokens revoke <id>. /vault tokens shows each token's id.");
                            return;
                        }

                        Tell(session, VaultMessages.TokenRevoked(tokenId, VaultPlugin.Revoke(player, tokenId)));
                        return;
                    }

                    foreach (var line in VaultMessages.Tokens(VaultPlugin.ListTokens(player)))
                        Tell(session, line);
                    break;

                default:
                    Tell(session, Usage);
                    break;
            }
        }

        private static bool IsMmd(string text) => text.Equals("mmd", StringComparison.OrdinalIgnoreCase);

        private static void Tell(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }
    }
}
