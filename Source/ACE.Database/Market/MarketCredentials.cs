using System;
using System.Security.Cryptography;
using System.Text;

namespace ACE.Database.Market
{
    /// <summary>
    /// What plugin sign-in (PluginAuth) and web sessions (WebSessions) share about the credentials they store: random secrets, stored only as
    /// SHA-256 hashes; a fingerprint of the account's password hash, so a password change ends the credential; and times as datetime(6) keeps them.
    /// </summary>
    public static class MarketCredentials
    {
        /// <summary>
        /// A new random secret of the given number of bytes, as base 64 URL text (A-Z, a-z, 0-9, '-' and '_'; never '.')
        /// </summary>
        public static string NewSecret(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>
        /// The stored form of a secret (a link code, plugin token or web session token)
        /// </summary>
        public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

        /// <summary>
        /// A fingerprint of the account's stored password hash. Any password change gives a new hash (bcrypt salts every hash), so the fingerprint stops matching.
        /// </summary>
        public static byte[] Fingerprint(string passwordHash) => SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash ?? ""));

        /// <summary>
        /// True when the credential was issued under this password hash; compared in constant time
        /// </summary>
        public static bool FingerprintMatches(byte[] stored, string passwordHash) => CryptographicOperations.FixedTimeEquals(stored, Fingerprint(passwordHash));

        /// <summary>
        /// The time as datetime(6) keeps it. MySQL rounds extra digits, which could move an expiry past the instant it was meant for.
        /// </summary>
        public static DateTime StoredTime(DateTime utc) => new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}
