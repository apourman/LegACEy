using ACE.Common.Cryptography;

using log4net;

using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ACE.Database.Models.Auth
{
    public static class AccountExtensions
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static bool PasswordMatches(this Account account, string password)
        {
            if (account.PasswordSalt == "use bcrypt") // Account password is using bcrypt
            {
                if (Common.ConfigManager.Config.Server.Accounts.ForceWorkFactorMigration &&
                    (BCryptProvider.GetPasswordWorkFactor(account.PasswordHash) != Common.ConfigManager.Config.Server.Accounts.PasswordHashWorkFactor))
                // Upgrade (or downgrade) Password workfactor if not the same as config specifies, ForceWorkFactorMigration is TRUE and Password Matches
                {
                    if (BCryptProvider.Verify(password, account.PasswordHash))
                    {
                        account.SetPassword(password);
                        account.SetSaltForBCrypt();

                        DatabaseManager.Authentication.UpdateAccount(account);

                        return true;
                    }
                    else
                        return false;
                }
                else
                    return BCryptProvider.Verify(password, account.PasswordHash);
            }
            else // Account password is using SHA512 salt
            {
                log.DebugFormat("{0} password verified using SHA512 hash/salt, migrating to bcrypt.", account.AccountName);

                var input = GetPasswordHash(account, password);

                if (input == account.PasswordHash) // If password matches, migrate to bcrypt
                {
                    account.SetPassword(password);
                    account.SetSaltForBCrypt();

                    DatabaseManager.Authentication.UpdateAccount(account);

                    return true;
                }
                else
                    return false;
            }
        }

        /// <summary>
        /// Checks the password without ever changing the account: bcrypt verify, or the SHA512 compare for old accounts.
        /// Unlike PasswordMatches (the game client's check) it never rehashes or migrates, so the Market API's web sign-in can't disturb the game login.
        /// </summary>
        public static bool PasswordMatchesReadOnly(this Account account, string password)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(account.PasswordHash))
                return false;

            try
            {
                if (account.PasswordSalt == "use bcrypt")
                    return BCryptProvider.Verify(password, account.PasswordHash);

                return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(GetPasswordHash(account, password)), Encoding.ASCII.GetBytes(account.PasswordHash));
            }
            catch (Exception ex) when (ex is FormatException || ex is BCrypt.Net.SaltParseException || ex is ArgumentException)
            {
                log.WarnFormat("{0} has an unreadable password hash or salt: {1}", account.AccountName, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// True while a ban is in force, the same rule the game's login uses: a ban expire time still in the future
        /// </summary>
        public static bool IsBanned(this Account account, DateTime utcNow)
        {
            return account.BanExpireTime.HasValue && utcNow < account.BanExpireTime.Value;
        }

        public static void SetPassword(this Account account, string value)
        {
            account.PasswordHash = GetPasswordHash(value);
        }

        public static void SetSalt(this Account account, string value)
        {
            account.PasswordSalt = value;
        }

        public static void SetSaltForBCrypt(this Account account)
        {
            SetSalt(account, "use bcrypt"); // this is used just to indicate that the password is using bcrypt. For migration purposes only.
        }

        private static string GetPasswordHash(string password)
        {
            var workFactor = Common.ConfigManager.Config.Server.Accounts.PasswordHashWorkFactor;

            if (workFactor < 4)
            {
                log.Warn("PasswordHashWorkFactor in config less than minimum value of 4, using 4 and continuing.");
                workFactor = 4;
            }
            else if (workFactor > 31)
            {
                log.Warn("PasswordHashWorkFactor in config greater than maximum value of 31, using 31 and continuing.");
                workFactor = 31;
            }

            return BCryptProvider.HashPassword(password, workFactor);
        }

        private static string GetPasswordHash(Account account, string password)
        {
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] saltBytes = Convert.FromBase64String(account.PasswordSalt);
            byte[] buffer = passwordBytes.Concat(saltBytes).ToArray();
            byte[] hash;

            using (var hasher = SHA512.Create())
                hash = hasher.ComputeHash(buffer);

            return Convert.ToBase64String(hash);
        }

        public static void UpdateLastLogin(this Account account, IPAddress address)
        {
            account.LastLoginIP = address.GetAddressBytes();
            account.LastLoginTime = DateTime.UtcNow;
            account.TotalTimesLoggedIn++;

            DatabaseManager.Authentication.UpdateAccount(account);
        }

        public static void UnBan(this Account account)
        {
            account.BanExpireTime = null;
            account.BannedByAccountId = null;
            account.BannedTime = null;
            account.BanReason = null;

            DatabaseManager.Authentication.UpdateAccount(account);
        }
    }
}
