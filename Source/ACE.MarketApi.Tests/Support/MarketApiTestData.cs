using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using MySqlConnector;

using ACE.Common.Cryptography;
using ACE.Database.Tests.Market;

namespace ACE.MarketApi.Tests.Support
{
    /// <summary>
    /// The scratch auth and shard databases the API tests run against, and seeding through the database directly.
    /// Never touches the configured ace_auth or ace_shard.
    /// </summary>
    internal static class MarketApiTestData
    {
        public const string AuthDatabase = "ace_auth_market_api";

        /// <summary>
        /// Base shard plus every update, market tables included
        /// </summary>
        public const string ShardDatabase = "ace_shard_market_api";

        /// <summary>
        /// Base shard only: no market tables
        /// </summary>
        public const string BareShardDatabase = "ace_shard_market_api_bare";

        private static int counter;

        public static void CreateDatabases()
        {
            MarketTestDatabase.InitializeConfig();

            CreateAuth(AuthDatabase);

            var failures = MarketTestDatabase.CreateFresh(ShardDatabase);
            if (failures.Count > 0)
                throw new InvalidOperationException("shard updates failed: " + string.Join(", ", failures.Select(f => $"{f.Key}: {f.Value.Message}")));

            MarketTestDatabase.CreateFromBase(BareShardDatabase);
        }

        public static void DropDatabases()
        {
            MarketTestDatabase.Drop(AuthDatabase);
            MarketTestDatabase.Drop(ShardDatabase);
            MarketTestDatabase.Drop(BareShardDatabase);
        }

        private static void CreateAuth(string database)
        {
            MarketTestDatabase.Drop(database);

            var sql = File.ReadAllText(Path.Combine(MarketTestDatabase.RepositoryRoot, "Database", "Base", "AuthenticationBase.sql")).Replace("ace_auth", database);

            using var connection = new MySqlConnection(MarketTestDatabase.ConnectionString());
            connection.Open();
            using var command = new MySqlCommand(sql, connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// A name no other test uses
        /// </summary>
        public static string UniqueName(string prefix) => $"{prefix}{Interlocked.Increment(ref counter)}x{Environment.TickCount64 % 100000}";

        /// <summary>
        /// Inserts an account whose password is bcrypt (at the given work factor) or the old SHA512 hash and salt. Returns its id.
        /// </summary>
        public static uint CreateAccount(string name, string password, bool sha512 = false, int bcryptWorkFactor = 4)
        {
            string hash, salt;

            if (sha512)
            {
                var saltBytes = RandomNumberGenerator.GetBytes(64);
                salt = Convert.ToBase64String(saltBytes);
                hash = Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(password).Concat(saltBytes).ToArray()));
            }
            else
            {
                salt = "use bcrypt";
                hash = BCryptProvider.HashPassword(password, bcryptWorkFactor);
            }

            MarketTestDatabase.Execute(AuthDatabase, $"INSERT INTO account (accountName, passwordHash, passwordSalt, accessLevel) VALUES ('{name}', '{hash}', '{salt}', 0);");

            return (uint)MarketTestDatabase.Scalar(AuthDatabase, $"SELECT accountId FROM account WHERE accountName = '{name}';");
        }

        public static void Ban(uint accountId, DateTime expiresUtc)
        {
            MarketTestDatabase.Execute(AuthDatabase, $"UPDATE account SET banned_Time = UTC_TIMESTAMP(), banned_By_Account_Id = 1, ban_Expire_Time = '{expiresUtc:yyyy-MM-dd HH:mm:ss}', ban_Reason = 'test' WHERE accountId = {accountId};");
        }

        /// <summary>
        /// Every stored column of the account, binary ones as hex
        /// </summary>
        public static string AccountRow(uint accountId)
        {
            return MarketTestDatabase.Rows(AuthDatabase,
                "SELECT accountId, accountName, HEX(passwordHash), HEX(passwordSalt), accessLevel, IFNULL(email_Address, '-'), create_Time, IFNULL(HEX(create_I_P), '-'), " +
                "IFNULL(last_Login_Time, '-'), IFNULL(HEX(last_Login_I_P), '-'), total_Times_Logged_In, IFNULL(banned_Time, '-'), IFNULL(banned_By_Account_Id, '-'), IFNULL(ban_Expire_Time, '-'), IFNULL(ban_Reason, '-') " +
                $"FROM account WHERE accountId = {accountId};").Single();
        }

        public static uint AddCharacter(uint accountId, string name, bool deleted = false)
        {
            var id = 0x50000000u + (uint)Interlocked.Increment(ref counter);

            MarketTestDatabase.Execute(ShardDatabase, $"INSERT INTO `character` (id, account_Id, name, is_Plussed, is_Deleted) VALUES ({id}, {accountId}, '{name}', 0, {(deleted ? 1 : 0)});");

            return id;
        }

        public static void SetBalance(uint accountId, long balance)
        {
            MarketTestDatabase.Execute(ShardDatabase, $"INSERT INTO market_balance (account_Id, balance) VALUES ({accountId}, {balance});");
        }

        /// <summary>
        /// An item row and its Vault row in the given state. Returns the item GUID.
        /// </summary>
        public static uint AddVaultItem(uint accountId, uint characterId, string name, string state, uint wcid = 35, int stackSize = 1)
        {
            var guid = 0xC0000000u + (uint)Interlocked.Increment(ref counter);

            MarketTestDatabase.Execute(ShardDatabase, $"INSERT INTO biota (id, weenie_Class_Id, weenie_Type) VALUES ({guid}, {wcid}, 1);");
            MarketTestDatabase.Execute(ShardDatabase,
                "INSERT INTO market_vault_item (item_Guid, account_Id, character_Id, state, deposited_Time, wcid, name, item_Type, stack_Size) " +
                $"VALUES ({guid}, {accountId}, {characterId}, '{state}', UTC_TIMESTAMP(6), {wcid}, '{name}', 2, {stackSize});");

            return guid;
        }

        public static void SetSetting(string key, long value)
        {
            MarketTestDatabase.Execute(ShardDatabase, $"REPLACE INTO config_properties_long (`key`, `value`) VALUES ('{key}', {value});");
        }

        public static void ClearSetting(string key)
        {
            MarketTestDatabase.Execute(ShardDatabase, $"DELETE FROM config_properties_long WHERE `key` = '{key}';");
        }
    }
}
