using System;
using System.Collections.Generic;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Market
{
    /// <summary>
    /// The game's half of plugin sign-in: /vault link makes the one-time code the plugin exchanges with the Market API for a token,
    /// and /vault tokens lists and revokes the account's tokens. Market rows only, read and written synchronously like the Vault's own reads.
    /// </summary>
    public static class VaultPlugin
    {
        /// <summary>
        /// A new link code for the player's account, and how many minutes it lasts
        /// </summary>
        public static string NewLinkCode(Player player, out long minutes)
        {
            using (var context = new ShardDbContext())
            {
                minutes = PluginAuth.LinkCodeMinutes(context);

                return PluginAuth.NewLinkCode(context, player.Character.AccountId, player.Character.Id, DateTime.UtcNow);
            }
        }

        /// <summary>
        /// The account's tokens that still work. The password hash is read from the auth database now, so a password change since login hides every token.
        /// </summary>
        public static List<PluginToken> Tokens(Player player)
        {
            var account = DatabaseManager.Authentication.GetAccountById(player.Character.AccountId);

            if (account == null)
                return new List<PluginToken>();

            using (var context = new ShardDbContext())
                return PluginAuth.ListTokens(context, account.AccountId, account.PasswordHash, DateTime.UtcNow);
        }

        public static bool Revoke(Player player, long tokenId)
        {
            using (var context = new ShardDbContext())
                return PluginAuth.Revoke(context, player.Character.AccountId, tokenId, DateTime.UtcNow);
        }
    }
}
