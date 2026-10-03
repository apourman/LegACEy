using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common.Cryptography;
using ACE.Database.Market;
using ACE.Database.Tests.Market;
using ACE.Server.Command.Handlers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: /vault link and /vault tokens, the in-game half of plugin sign-in. The exchange and bearer use are seam 1 (ACE.MarketApi.Tests).
    /// </summary>
    public partial class VaultTests
    {
        private static readonly Regex LinkCodeText = new Regex(@"\b[0-9A-Z]{5}-[0-9A-Z]{5}\b");

        [TestMethod]
        public void VaultLink_TellsAOneTimeCode_StoredOnlyAsItsHash_ForTheSettingsLifetime()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            VaultTestWorld.TakeSent(player);

            var before = DateTime.UtcNow;
            var code = Link(player, out var chats);
            var after = DateTime.UtcNow;

            Assert.IsTrue(chats.Any(c => c.Contains("5 minutes")), string.Join("\n", chats));

            using (var shard = MarketTestDatabase.CreateContext(VaultTestWorld.Db))
            {
                var row = shard.MarketLinkCodes.AsNoTracking().Single(c => c.AccountId == account);

                CollectionAssert.AreEqual(MarketCredentials.Hash(code), row.CodeHash);
                Assert.IsFalse(row.CodeHash.SequenceEqual(Encoding.UTF8.GetBytes(code)), "the code itself isn't stored");
                Assert.AreEqual(player.Character.Id, row.CharacterId);
                Assert.IsNull(row.UsedTime);
                Assert.IsTrue(row.ExpiresTime >= before.AddMinutes(5).AddMilliseconds(-1) && row.ExpiresTime <= after.AddMinutes(5), $"expires 5 minutes after it was made: {row.ExpiresTime:O}");

                // the code works, typed with or without its dash and in any case
                Assert.IsNotNull(PluginAuth.FindLinkCode(shard, code.Replace("-", "").ToLowerInvariant(), DateTime.UtcNow));
            }

            // every /vault link gives a new code; the setting sets the lifetime
            using var lifetime = Setting(MarketSettings.LinkCodeMinutes, 2);
            var second = Link(player, out var secondChats);
            Assert.AreNotEqual(code, second);
            Assert.IsTrue(secondChats.Any(c => c.Contains("2 minutes")), string.Join("\n", secondChats));
        }

        [TestMethod]
        public void VaultTokens_ListsTheAccountsTokens_AndRevokingOneStopsIt()
        {
            var account = VaultTestWorld.NewAccountId();
            var passwordHash = AddAuthAccount(account);
            var player = VaultTestWorld.NewPlayer(account);
            var other = VaultTestWorld.NewAccountId();
            var otherHash = AddAuthAccount(other);
            var otherPlayer = VaultTestWorld.NewPlayer(other);

            var laptop = Redeem(Link(player, out _), "Laptop", passwordHash);
            var desktop = Redeem(Link(player, out _), "Desktop", passwordHash);
            var othersToken = Redeem(Link(otherPlayer, out _), "Other", otherHash);

            var listed = Tokens(player);
            Assert.IsTrue(listed.Any(c => c.Contains($"#{laptop}") && c.Contains("Laptop")), string.Join("\n", listed));
            Assert.IsTrue(listed.Any(c => c.Contains($"#{desktop}") && c.Contains("Desktop")), string.Join("\n", listed));
            Assert.IsFalse(listed.Any(c => c.Contains("Other")), "only the account's own tokens");

            // another account's token can't be revoked from here
            var refused = Tokens(player, "revoke", othersToken.ToString());
            Assert.IsTrue(refused.Any(c => c.Contains("no plugin token")), string.Join("\n", refused));
            Assert.IsNull(RevokedTime(othersToken));

            var revoked = Tokens(player, "revoke", laptop.ToString());
            Assert.IsTrue(revoked.Any(c => c.Contains("Revoked")), string.Join("\n", revoked));
            Assert.IsNotNull(RevokedTime(laptop), "revoked in the database, so the API refuses it at once");
            Assert.IsNull(RevokedTime(desktop));

            var after = Tokens(player);
            Assert.IsFalse(after.Any(c => c.Contains("Laptop")), string.Join("\n", after));
            Assert.IsTrue(after.Any(c => c.Contains("Desktop")), string.Join("\n", after));

            // after a password change no token works, so none is listed
            MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"UPDATE account SET passwordHash = '{BCryptProvider.HashPassword("changed", 4)}' WHERE accountId = {account};");
            var none = Tokens(player);
            Assert.IsTrue(none.Any(c => c.Contains("no plugin tokens")), string.Join("\n", none));

            var usage = Tokens(player, "revoke", "abc");
            Assert.IsTrue(usage.Any(c => c.Contains("/vault tokens revoke <id>")), string.Join("\n", usage));
        }

        // ---- helpers

        /// <summary>
        /// Runs /vault link and returns the code it told the player
        /// </summary>
        private static string Link(Player player, out System.Collections.Generic.List<string> chats)
        {
            VaultTestWorld.OnWorldThread(() => VaultCommands.HandleVault(player.Session, "link"));

            chats = VaultTestWorld.Chats(VaultTestWorld.TakeSent(player));
            var match = LinkCodeText.Match(string.Join("\n", chats));

            Assert.IsTrue(match.Success, "/vault link told a code: " + string.Join("\n", chats));

            return match.Value;
        }

        /// <summary>
        /// The Market API's half of the exchange, as POST /api/auth/plugin-token runs it. Returns the token's id.
        /// </summary>
        private static long Redeem(string code, string label, string passwordHash)
        {
            using var shard = MarketTestDatabase.CreateContext(VaultTestWorld.Db);

            var linkCode = PluginAuth.FindLinkCode(shard, code, DateTime.UtcNow);
            Assert.IsNotNull(linkCode, "a fresh code is found");

            var token = PluginAuth.Redeem(shard, linkCode, label, passwordHash, DateTime.UtcNow, out _);
            Assert.IsNotNull(token);

            return token.Id;
        }

        private static System.Collections.Generic.List<string> Tokens(Player player, params string[] more)
        {
            VaultTestWorld.TakeSent(player);
            VaultTestWorld.OnWorldThread(() => VaultCommands.HandleVault(player.Session, new[] { "tokens" }.Concat(more).ToArray()));

            return VaultTestWorld.Chats(VaultTestWorld.TakeSent(player));
        }

        private static IDisposable Setting(MarketSetting setting, long value)
        {
            MarketTestDatabase.Execute(VaultTestWorld.Db, $"REPLACE INTO config_properties_long (`key`, `value`, description) VALUES ('{setting.Key}', {value}, 'test');");

            return new Cleanup(() => MarketTestDatabase.Execute(VaultTestWorld.Db, $"DELETE FROM config_properties_long WHERE `key` = '{setting.Key}';"));
        }

        private static DateTime? RevokedTime(long tokenId)
        {
            using var shard = MarketTestDatabase.CreateContext(VaultTestWorld.Db);

            return shard.MarketPluginTokens.AsNoTracking().Single(t => t.Id == tokenId).RevokedTime;
        }

        /// <summary>
        /// The test account's row in the scratch auth database, with a bcrypt password. Returns the password hash.
        /// </summary>
        private static string AddAuthAccount(uint accountId)
        {
            var hash = BCryptProvider.HashPassword("p-pass", 4);

            MarketTestDatabase.Execute(VaultTestWorld.AuthDb, $"INSERT INTO account (accountId, accountName, passwordHash, passwordSalt, accessLevel) VALUES ({accountId}, 'vaulttest{accountId}', '{hash}', 'use bcrypt', 0);");

            return hash;
        }
    }
}
