using System;
using System.Globalization;

using ACE.Entity.Enum;
using ACE.Server.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// The /vault commands. Item deposits and withdrawals take the Vault channel, which then calls the Vault entry point the game bridge will use.
    /// Trade notes (MMD) go in and out of the account balance instantly, never through the channel.
    /// </summary>
    public static class VaultCommands
    {
        private const string Usage = "/vault deposit  (the last item you appraised)\n/vault withdraw <id>\n/vault list\n/vault deposit mmd  (all your trade notes)\n/vault withdraw mmd <amount>\n/vault balance\n/vault link  (a code for the UtilityBelt plugin)\n/vault tokens\n/vault tokens revoke <id>";

        [CommandHandler("vault", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Move items between your pack and your account's Vault",
            "deposit | withdraw <id> | list | deposit mmd | withdraw mmd <amount> | balance | link | tokens | tokens revoke <id>")]
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
                case "deposit":
                    // the outcome is told to the player by the channel and the Vault
                    if (parameters.Length >= 2 && IsMmd(parameters[1]))
                        Vault.DepositNotes(player);
                    else
                        VaultChannel.StartDeposit(player, player.CurrentAppraisalTarget ?? 0);
                    break;

                case "withdraw":
                    if (parameters.Length >= 2 && IsMmd(parameters[1]))
                    {
                        if (parameters.Length < 3 || !long.TryParse(parameters[2], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                        {
                            Tell(session, "Usage: /vault withdraw mmd <amount>, a whole number of trade notes.");
                            return;
                        }

                        Vault.WithdrawNotes(player, amount);
                        return;
                    }

                    if (parameters.Length < 2 || !TryParseId(parameters[1], out var itemGuid))
                    {
                        Tell(session, "Usage: /vault withdraw <id>. /vault list shows each item's id.");
                        return;
                    }

                    VaultChannel.StartWithdraw(player, itemGuid);
                    break;

                case "list":
                {
                    if (!Vault.Available)
                    {
                        Tell(session, VaultMessages.For(VaultOutcome.NotAvailable, null));
                        return;
                    }

                    var items = Vault.List(player);

                    Tell(session, items.Count == 0 ? "Your Vault is empty." : $"Your Vault holds {items.Count:N0} item{(items.Count == 1 ? "" : "s")}:");

                    foreach (var item in items)
                        Tell(session, $"0x{item.ItemGuid:X8}  {item.Name}{(item.StackSize > 1 ? $" x{item.StackSize:N0}" : "")}  [{item.State}]");
                    break;
                }

                case "balance":
                    if (!Vault.Available)
                    {
                        Tell(session, VaultMessages.For(VaultOutcome.NotAvailable, null));
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

                    Tell(session, VaultMessages.LinkCode(VaultPlugin.NewLinkCode(player, out var minutes), minutes));
                    break;

                case "tokens":
                    if (!Vault.Available)
                    {
                        Tell(session, VaultMessages.For(VaultOutcome.NotAvailable, null));
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

        private static bool TryParseId(string text, out uint id)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text.Substring(2), NumberStyles.HexNumber, null, out id);

            return uint.TryParse(text, out id);
        }

        private static void Tell(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }
    }
}
