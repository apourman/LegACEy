using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    /// <summary>
    /// UtilityBelt plugin sign-in, shared by the game (/vault link, /vault tokens) and the Market API (the exchange, bearer tokens, the website's token list).
    /// Link codes and tokens are stored only as SHA-256 hashes. A token carries a fingerprint of the account's password hash at issue,
    /// so a password change stops every token without touching them. Lifetimes are the market settings, read on each use.
    /// </summary>
    public static class PluginAuth
    {
        public const int LabelMaxLength = 64;

        /// <summary>
        /// Crockford's base 32: no I, L, O or U, so a code read off the chat window is hard to mistype
        /// </summary>
        private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>
        /// 10 characters, 50 bits, shown as two groups of 5
        /// </summary>
        private const int CodeLength = 10;

        private const int TokenBytes = 32;

        /// <summary>
        /// Makes a one-time link code for the character's account and stores the hash of the code as shown. Returns the code to tell the player, as XXXXX-XXXXX.
        /// </summary>
        public static string NewLinkCode(ShardDbContext context, uint accountId, uint characterId, DateTime utcNow)
        {
            var code = FormatCode(new string(Enumerable.Range(0, CodeLength).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray()));

            context.MarketLinkCodes.Add(new LinkCode
            {
                CodeHash = Hash(code),
                AccountId = accountId,
                CharacterId = characterId,
                ExpiresTime = TruncateToMicroseconds(utcNow).AddMinutes(LinkCodeMinutes(context)),
            });
            context.SaveChanges();

            return code;
        }

        /// <summary>
        /// The link code setting, in whole minutes. At least 1: an admin's 0 or negative value would make every code expired on arrival.
        /// </summary>
        public static long LinkCodeMinutes(ShardDbContext context) => Math.Clamp(MarketSettings.Get(context, MarketSettings.LinkCodeMinutes), 1, 60 * 24 * 365);

        /// <summary>
        /// The plugin token setting, in whole days. At least 1, for the same reason.
        /// </summary>
        public static long TokenDays(ShardDbContext context) => Math.Clamp(MarketSettings.Get(context, MarketSettings.PluginTokenDays), 1, 365 * 100);

        /// <summary>
        /// The stored form of a link code or token
        /// </summary>
        public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

        /// <summary>
        /// A fingerprint of the account's stored password hash. Any password change gives a new hash (bcrypt salts every hash), so the fingerprint stops matching.
        /// </summary>
        public static byte[] Fingerprint(string passwordHash) => SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash ?? ""));

        /// <summary>
        /// The unused, unexpired link code typed, tracked so Redeem can mark it used; or null. Dashes, spaces and case don't matter.
        /// </summary>
        public static LinkCode FindLinkCode(ShardDbContext context, string code, DateTime utcNow)
        {
            var typed = new string((code ?? "").Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

            if (typed.Length != CodeLength)
                return null;

            var hash = Hash(FormatCode(typed));
            var linkCode = context.MarketLinkCodes.FirstOrDefault(c => c.CodeHash == hash);

            return linkCode != null && linkCode.UsedTime == null && utcNow < linkCode.ExpiresTime ? linkCode : null;
        }

        /// <summary>
        /// Uses up the link code and issues a token for its account, in one save. The code's used time is a concurrency token,
        /// so of two exchanges racing for one code only the first saves; the other gets null and no token.
        /// </summary>
        /// <param name="passwordHash">the account's stored password hash now, which the token is fingerprinted with</param>
        /// <param name="secret">the token to hand to the plugin; only its hash is stored</param>
        public static PluginToken Redeem(ShardDbContext context, LinkCode linkCode, string label, string passwordHash, DateTime utcNow, out string secret)
        {
            secret = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

            var now = TruncateToMicroseconds(utcNow);

            var token = new PluginToken
            {
                TokenHash = Hash(secret),
                AccountId = linkCode.AccountId,
                Label = label,
                CreatedTime = now,
                ExpiresTime = now.AddDays(TokenDays(context)),
                PasswordFingerprint = Fingerprint(passwordHash),
            };

            linkCode.UsedTime = now;
            context.MarketPluginTokens.Add(token);

            try
            {
                context.SaveChanges();
            }
            catch (DbUpdateConcurrencyException)
            {
                secret = null;
                return null;
            }

            return token;
        }

        /// <summary>
        /// The token whose secret this is, tracked so a use can renew it; or null. Says nothing yet about whether it may be used: see IsUsable.
        /// </summary>
        public static PluginToken FindToken(ShardDbContext context, string secret)
        {
            if (string.IsNullOrEmpty(secret))
                return null;

            var hash = Hash(secret);

            return context.MarketPluginTokens.FirstOrDefault(t => t.TokenHash == hash);
        }

        /// <summary>
        /// Not revoked, not expired, and issued under the account's current password. The ban is the caller's to check, against the auth database.
        /// </summary>
        public static bool IsUsable(PluginToken token, string passwordHash, DateTime utcNow)
        {
            return token.RevokedTime == null && utcNow < token.ExpiresTime && CryptographicOperations.FixedTimeEquals(token.PasswordFingerprint, Fingerprint(passwordHash));
        }

        /// <summary>
        /// A use: the token's lifetime starts again from now
        /// </summary>
        public static void Renew(ShardDbContext context, PluginToken token, DateTime utcNow)
        {
            var now = TruncateToMicroseconds(utcNow);

            token.LastUsedTime = now;
            token.ExpiresTime = now.AddDays(TokenDays(context));
            context.SaveChanges();
        }

        /// <summary>
        /// The account's tokens that still work (password unchanged, not revoked, not expired), newest first
        /// </summary>
        public static List<PluginToken> ListTokens(ShardDbContext context, uint accountId, string passwordHash, DateTime utcNow)
        {
            return context.MarketPluginTokens.AsNoTracking()
                .Where(t => t.AccountId == accountId && t.RevokedTime == null && t.ExpiresTime > utcNow)
                .OrderByDescending(t => t.CreatedTime).ThenByDescending(t => t.Id)
                .AsEnumerable()
                .Where(t => IsUsable(t, passwordHash, utcNow))
                .ToList();
        }

        /// <summary>
        /// Revokes one of the account's tokens. False when the account has no token with that id. Revoking a revoked token changes nothing.
        /// </summary>
        public static bool Revoke(ShardDbContext context, uint accountId, long tokenId, DateTime utcNow)
        {
            var token = context.MarketPluginTokens.FirstOrDefault(t => t.Id == tokenId && t.AccountId == accountId);

            if (token == null)
                return false;

            if (token.RevokedTime == null)
            {
                token.RevokedTime = TruncateToMicroseconds(utcNow);
                context.SaveChanges();
            }

            return true;
        }

        /// <summary>
        /// The time as datetime(6) keeps it. MySQL rounds extra digits, which could move an expiry past the instant it was meant for.
        /// </summary>
        private static DateTime TruncateToMicroseconds(DateTime utc) => new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);

        /// <summary>
        /// A code as it's shown, and hashed: XXXXX-XXXXX
        /// </summary>
        private static string FormatCode(string code) => code.Substring(0, CodeLength / 2) + "-" + code.Substring(CodeLength / 2);

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
