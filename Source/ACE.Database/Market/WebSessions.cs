using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Market
{
    /// <summary>
    /// Web sessions, issued by the Market API's session sign-in and presented by the BFF as "Authorization: Bearer &lt;token&gt;".
    /// The token is opaque and random; only its SHA-256 hash is stored. A session works until it is revoked, has been idle for the idle lifetime,
    /// reaches its absolute expiry, or the account's password hash changes (the fingerprint taken at sign-in no longer matches).
    /// Lifetimes are the market settings: the idle one is read on each renewal, the absolute one at sign-in.
    /// </summary>
    public static class WebSessions
    {
        /// <summary>
        /// Starts every session token. Plugin tokens are bare base 64 URL text, which never holds a '.', so the two can't be mistaken for each other.
        /// </summary>
        public const string TokenPrefix = "ws.";

        /// <summary>
        /// A use writes the last-used time and the slid idle expiry at most this often
        /// </summary>
        public static readonly TimeSpan RenewInterval = TimeSpan.FromHours(1);

        private const int TokenBytes = 32;

        /// <summary>
        /// The idle lifetime setting, in whole days. At least 1: an admin's 0 or negative value would end every session on arrival.
        /// </summary>
        public static long IdleDays(ShardDbContext context) => Math.Clamp(MarketSettings.Get(context, MarketSettings.WebSessionIdleDays), 1, 365 * 10);

        /// <summary>
        /// The absolute lifetime setting, in whole days. At least 1, for the same reason.
        /// </summary>
        public static long AbsoluteDays(ShardDbContext context) => Math.Clamp(MarketSettings.Get(context, MarketSettings.WebSessionAbsoluteDays), 1, 365 * 10);

        /// <summary>
        /// True when the bearer text is meant to be a web session (whether or not one exists)
        /// </summary>
        public static bool IsSessionToken(string token) => token != null && token.StartsWith(TokenPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Starts a session for the account and stores the hash of its token
        /// </summary>
        /// <param name="passwordHash">the account's stored password hash now, which the session is fingerprinted with</param>
        /// <param name="token">the token to hand to the caller; only its hash is stored</param>
        public static WebSession Create(ShardDbContext context, uint accountId, string passwordHash, DateTime utcNow, out string token)
        {
            token = TokenPrefix + PluginAuth.Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

            var now = PluginAuth.TruncateToMicroseconds(utcNow);
            var absolute = now.AddDays(AbsoluteDays(context));

            var session = new WebSession
            {
                TokenHash = PluginAuth.Hash(token),
                AccountId = accountId,
                CreatedTime = now,
                LastUsedTime = now,
                IdleExpiresTime = Earlier(now.AddDays(IdleDays(context)), absolute),
                AbsoluteExpiresTime = absolute,
                PasswordFingerprint = PluginAuth.Fingerprint(passwordHash),
            };

            context.MarketWebSessions.Add(session);
            context.SaveChanges();

            return session;
        }

        /// <summary>
        /// The session whose token this is, tracked so a use can renew it; or null. Says nothing yet about whether it may be used: see IsUsable.
        /// </summary>
        public static WebSession Find(ShardDbContext context, string token)
        {
            if (!IsSessionToken(token))
                return null;

            var hash = PluginAuth.Hash(token);

            return context.MarketWebSessions.FirstOrDefault(s => s.TokenHash == hash);
        }

        /// <summary>
        /// Not revoked, before both expiries, and started under the account's current password. The ban is the caller's to check, against the auth database.
        /// </summary>
        public static bool IsUsable(WebSession session, string passwordHash, DateTime utcNow)
        {
            return session.RevokedTime == null
                && utcNow < session.IdleExpiresTime
                && utcNow < session.AbsoluteExpiresTime
                && CryptographicOperations.FixedTimeEquals(session.PasswordFingerprint, PluginAuth.Fingerprint(passwordHash));
        }

        /// <summary>
        /// A use. When the last recorded use is at least RenewInterval old, records this one and slides the idle expiry, never past the absolute expiry.
        /// Otherwise writes nothing. Returns true when it wrote. Only the two changed columns are written, so a concurrent revoke is never undone.
        /// </summary>
        public static bool Touch(ShardDbContext context, WebSession session, DateTime utcNow)
        {
            if (utcNow - session.LastUsedTime < RenewInterval)
                return false;

            var now = PluginAuth.TruncateToMicroseconds(utcNow);

            session.LastUsedTime = now;
            session.IdleExpiresTime = Earlier(now.AddDays(IdleDays(context)), session.AbsoluteExpiresTime);
            context.SaveChanges();

            return true;
        }

        /// <summary>
        /// Ends one session (sign-out). Revoking a revoked session changes nothing.
        /// </summary>
        public static void Revoke(ShardDbContext context, long sessionId, DateTime utcNow)
        {
            var now = PluginAuth.TruncateToMicroseconds(utcNow);

            context.MarketWebSessions
                .Where(s => s.Id == sessionId && s.RevokedTime == null)
                .ExecuteUpdate(set => set.SetProperty(s => s.RevokedTime, now));
        }

        /// <summary>
        /// Ends every live session of the accounts, in one statement (a ban was seen). They stay ended after the ban: a revoked session never works again.
        /// Returns how many it ended.
        /// </summary>
        public static int RevokeAll(ShardDbContext context, IReadOnlyCollection<uint> accountIds, DateTime utcNow)
        {
            if (accountIds.Count == 0)
                return 0;

            var now = PluginAuth.TruncateToMicroseconds(utcNow);
            var ids = accountIds.ToList();
            var live = context.MarketWebSessions.Where(s => ids.Contains(s.AccountId) && s.RevokedTime == null);

            // browsing sees every banned account on each request: read first, so the usual case (nothing left to revoke) writes nothing
            if (!live.Any())
                return 0;

            return live.ExecuteUpdate(set => set.SetProperty(s => s.RevokedTime, now));
        }

        private static DateTime Earlier(DateTime a, DateTime b) => a <= b ? a : b;
    }
}
